// <copyright file="SelfUpdateDownloader.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;
    using ChillHub.Update;

    /// <summary>Чем закончилась подготовка и загрузка пакета обновления.</summary>
    internal enum SelfUpdateDownloadResult {
        /// <summary>Версии нет — делать нечего, окно не трогаем.</summary>
        Nothing,

        /// <summary>A6. Версия перестала быть допустимой между проверкой и применением.</summary>
        InvalidVersion,

        /// <summary>A12. Копировать и удалять нечего — апдейтер не нужен.</summary>
        AlreadyUpToDate,

        /// <summary>Манифест самообновления отклонён проверкой структуры.</summary>
        ManifestRejected,

        /// <summary>Скачанный файл не сошёлся по хешу.</summary>
        IntegrityFailed,

        /// <summary>Любой другой отказ загрузки: нет сети, не хватило места, файл занят.</summary>
        Failed,

        /// <summary>Пакет на месте — можно применять.</summary>
        Ready,
    }

    /// <summary>Результат шага загрузки.</summary>
    internal sealed class SelfUpdateDownload {
        internal SelfUpdateDownloadResult Result { get; init; }

        /// <summary>Каталог с полезной нагрузкой — прежнее поле <c>pendingTempRoot</c>.</summary>
        internal string? TempRoot { get; init; }

        /// <summary>Служебный каталог со списками и журналом — прежнее поле <c>pendingWorkDir</c>.</summary>
        internal string? WorkDir { get; init; }

        /// <summary>Пересчитанный strip-prefix; null — до манифеста не дошли.</summary>
        internal string? StripPrefix { get; init; }

        /// <summary>Пакет скачан — прежнее поле <c>downloaded</c>.</summary>
        internal bool Downloaded => this.Result == SelfUpdateDownloadResult.Ready;
    }

    /// <summary>
    /// Подготовка и загрузка пакета самообновления: манифест, честный диффовый план,
    /// служебные списки для апдейтера и собственно скачивание.
    /// <para>
    /// Сеть и файловая система приходят снаружи (<see cref="ISyncService"/> и
    /// <see cref="SelfUpdatePaths"/>), про контролы класс не знает ничего: всё, что
    /// нужно показать по ходу дела, уезжает в колбэк <c>apply</c>.
    /// </para>
    /// </summary>
    internal sealed class SelfUpdateDownloader {
        private readonly ISyncService sync;
        private readonly Func<string> baseApi;
        private readonly SelfUpdatePaths paths;
        private readonly UpdateAttemptsStore attempts;
        private readonly Action<SelfUpdateUiState> apply;

        internal SelfUpdateDownloader(
            ISyncService sync,
            Func<string> baseApi,
            SelfUpdatePaths paths,
            UpdateAttemptsStore attempts,
            Action<SelfUpdateUiState> apply) {
            this.sync = sync;
            this.baseApi = baseApi;
            this.paths = paths;
            this.attempts = attempts;
            this.apply = apply;
        }

        /// <summary>Готовит каталог сессии, считает дифф и скачивает изменившиеся файлы.</summary>
        /// <param name="remoteVersion">Версия, на которую обновляемся.</param>
        /// <returns>Результат шага.</returns>
        internal async Task<SelfUpdateDownload> DownloadAsync(string? remoteVersion) {
            if (string.IsNullOrWhiteSpace(remoteVersion)) {
                return new SelfUpdateDownload { Result = SelfUpdateDownloadResult.Nothing };
            }

            // A6. Повторная проверка перед использованием: версия попадает в путь
            // временного каталога и в URL, а между проверкой в Window_Loaded и этим
            // местом поле могло быть переприсвоено.
            if (!SelfUpdateVersions.IsValidVersion(remoteVersion)) {
                this.apply(new SelfUpdateUiState {
                    StatusText = "Недопустимый номер версии — обновление отменено.",
                    ButtonEnabled = false,
                });
                return new SelfUpdateDownload { Result = SelfUpdateDownloadResult.InvalidVersion };
            }

            string manifestUrl = string.Empty;
            string contentBase = string.Empty;
            try {
                this.apply(new SelfUpdateUiState {
                    ButtonEnabled = false,
                    StatusText = "Запрос манифеста лаунчера...",
                    Indeterminate = true,
                });

                manifestUrl = $"{this.baseApi()}/manifests/launcher/{remoteVersion}.json";
                contentBase = $"{this.baseApi()}/content/launcher/{remoteVersion}/files";
                this.apply(new SelfUpdateUiState { StatusText = $"Манифест: {manifestUrl}" });
                var manifest = await this.sync.GetManifestAsync(manifestUrl, CancellationToken.None);
                var stripPrefix = SelfUpdateVersions.ComputeStripPrefix(manifest);

                this.apply(new SelfUpdateUiState { StatusText = "Подготовка каталога загрузки..." });

                // A6. Полезная нагрузка и служебные файлы — в РАЗНЫХ подкаталогах.
                // Раньше это был один путь, из-за чего «остаточное зеркалирование» в апдейтере
                // копировало filelist.txt / apply-update.log / updater\ прямо в папку установки.
                var sessionRoot = this.paths.SessionRoot(remoteVersion);
                var tempRoot = this.paths.PayloadDir(remoteVersion);
                var workDir = this.paths.WorkDir(remoteVersion);

                // Чистим сессию целиком, чтобы не было нулевых файлов от прошлых попыток
                try {
                    if (Directory.Exists(sessionRoot)) {
                        Directory.Delete(sessionRoot, true);
                    }
                }
                catch {
                }

                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(workDir);

                var filesListPath = Path.Combine(workDir, "filelist.txt");
                var emptyDirsPath = Path.Combine(workDir, "emptydirs.txt");
                var deleteListPath = Path.Combine(workDir, "deletelist.txt");

                // A12. План считаем против ПАПКИ УСТАНОВКИ, а не против пустого temp,
                // иначе «недостающими» окажутся все файлы манифеста и лаунчер качается целиком.
                var plan = this.BuildSelfUpdatePlan(manifest, stripPrefix, tempRoot, contentBase);

                var toDelete = this.BuildDeleteList(manifest, stripPrefix);

                // A12. Нечего копировать и нечего удалять — обновление вообще не запускаем.
                // Иначе получаем полный цикл «останов лаунчера → апдейтер → перезапуск» впустую.
                if (plan.Downloads.Count == 0 && toDelete.Count == 0) {
                    this.MarkAlreadyUpToDate(manifest, remoteVersion);
                    return new SelfUpdateDownload {
                        Result = SelfUpdateDownloadResult.AlreadyUpToDate,
                        StripPrefix = stripPrefix,
                    };
                }

                // Эти три списка — ЕДИНСТВЕННОЕ, из чего апдейтер узнаёт, что именно
                // копировать и что удалять. Проглоченная ошибка записи меняла его
                // поведение молча: без filelist.txt он переходит в режим полного
                // зеркалирования, без deletelist.txt не удаляет ничего, а оборванная
                // на середине запись (кончилось место) оставляла файл с ЧАСТЬЮ диффа —
                // и всё это выглядело как обычное обновление. Лучше остановиться здесь,
                // до остановки лаунчера и запуска апдейтера.
                try {
                    // Формируем файлы для копирования из реально изменённых (diff plan),
                    // исключая preserve-файлы: апдейтер их всё равно не тронет.
                    var changed = plan.Downloads
                        .Select(t => t.RelativePath.Replace('\\', '/'))
                        .Where(rel => !SelfUpdateRules.Preserve.ShouldPreserve(rel) && !PreserveMatcher.IsUpdaterArtifact(rel))
                        .ToArray();
                    File.WriteAllLines(filesListPath, changed, SelfUpdateRules.Utf8NoBom);

                    // Пустые директории — из манифеста
                    var dirLines = manifest.EmptyDirs.Select(d => SelfUpdateVersions.StripLocal(stripPrefix, d)).ToArray();
                    File.WriteAllLines(emptyDirsPath, dirLines, SelfUpdateRules.Utf8NoBom);

                    File.WriteAllLines(deleteListPath, toDelete, SelfUpdateRules.Utf8NoBom);
                }
                catch (Exception ex) {
                    try {
                        Logging.Logger.Error(ex, "UpdateWindow.WriteUpdateLists");
                    }
                    catch {
                    }

                    throw new IOException(
                        $"Не удалось записать списки файлов обновления в {workDir}: {ex.Message}", ex);
                }

                try {
                    Logging.Logger.Info(
                        $"SelfUpdate diff: download={plan.Downloads.Count} files, {plan.TotalDownloadBytes} bytes; delete={toDelete.Count}; manifest files={manifest.Files.Count}");
                }
                catch {
                }

                this.apply(new SelfUpdateUiState {
                    StatusText = $"Скачивание из: {contentBase}\nВременная папка: {tempRoot}",
                });

                this.apply(new SelfUpdateUiState {
                    StatusText = plan.Downloads.Count > 0
                        ? $"Скачивание обновления: {plan.Downloads.Count} файл(ов) из {manifest.Files.Count}..."
                        : "Изменившихся файлов нет, применяем удаления...",
                });
                var prog = new Progress<SyncProgress>(p => {
                    this.apply(new SelfUpdateUiState {
                        Indeterminate = false,
                        ProgressValue = p.TotalBytes > 0
                            ? Math.Min(100, Math.Max(0, (p.BytesDownloaded * 100.0) / p.TotalBytes))
                            : null,
                    });
                });

                await this.sync.ExecuteAsync(plan, prog, CancellationToken.None);

                this.apply(new SelfUpdateUiState { StatusText = "Обновление загружено. Применяем и перезапускаем..." });
                return new SelfUpdateDownload {
                    Result = SelfUpdateDownloadResult.Ready,
                    TempRoot = tempRoot,
                    WorkDir = workDir,
                    StripPrefix = stripPrefix,
                };
            }
            catch (ManifestValidationException ex) {
                // Манифест самообновления отклонён. Ни одного байта ещё не
                // скачано, и скачано не будет: манифест определяет, что именно
                // ляжет на диск вместо ChillHub.exe.
                this.apply(new SelfUpdateUiState {
                    StatusText = $"Обновление отменено: {ex.Message}",
                    ButtonEnabled = false,
                });
                try {
                    Logging.Logger.Error(ex, "UpdateWindow.ManifestValidation");
                }
                catch {
                }

                return new SelfUpdateDownload { Result = SelfUpdateDownloadResult.ManifestRejected };
            }
            catch (InvalidDataException ex) {
                // Обычно это несоответствие хэшей (sha256/blake3)
                this.apply(new SelfUpdateUiState {
                    StatusText = $"Проверка целостности не пройдена: {ex.Message}. Попробуйте ещё раз. Если проблема повторяется — обратитесь в поддержку.",
                    ButtonEnabled = true,
                });
                try {
                    Logging.Logger.Error(ex, "UpdateWindow.DownloadIntegrity");
                }
                catch {
                }

                return new SelfUpdateDownload { Result = SelfUpdateDownloadResult.IntegrityFailed };
            }
            catch (Exception ex) {
                this.apply(new SelfUpdateUiState {
                    StatusText = $"Ошибка загрузки/проверки обновления (manifest: {manifestUrl}, content: {contentBase}): {ex.Message}",
                    ButtonEnabled = true,
                });
                try {
                    Logging.Logger.Error(ex, "UpdateWindow.DownloadUpdate");
                }
                catch {
                }

                return new SelfUpdateDownload { Result = SelfUpdateDownloadResult.Failed };
            }
        }

        /// <summary>
        /// A12. Строит ЧЕСТНЫЙ диффовый план самообновления.
        ///
        /// Раньше план считался против пустого временного каталога, поэтому «недостающими»
        /// оказывались ВСЕ файлы манифеста и каждое обновление тянуло лаунчер целиком.
        /// Теперь сравнение идёт с фактической папкой установки, а качаем всё равно во временный
        /// каталог: файлы работающего лаунчера залочены, копирует их внешний updater после выхода.
        /// </summary>
        /// <param name="manifest">Манифест целевой версии.</param>
        /// <param name="stripPrefix">Общая корневая папка пакета.</param>
        /// <param name="tempRoot">Временный каталог загрузки (LocalRoot плана).</param>
        /// <param name="contentBase">База URL с файлами версии.</param>
        /// <returns>План, в котором Downloads — только реально изменившиеся файлы.</returns>
        internal DiffPlan BuildSelfUpdatePlan(Manifest manifest, string stripPrefix, string tempRoot, string contentBase) {
            // Свой план — свой рубеж. Этот метод обходит SimpleSyncService.PlanAsync
            // вместе с его проверкой манифеста, а результат ложится поверх каталога
            // установки лаунчера. Дальше проверять уже нечем: запись без хешей
            // сверка скачанного файла пропускает молча — сверять не с чем.
            ManifestValidator.Validate(manifest, "план самообновления");

            var baseDir = this.paths.InstallDir;
            var plan = new DiffPlan {
                GameId = manifest.GameId,
                Version = manifest.Version,
                LocalRoot = tempRoot,

                // Качаем в %TEMP%, а применяем в каталог установки — это может быть
                // другой диск. Без ApplyRoot проверка места смотрела бы только на TEMP.
                ApplyRoot = baseDir,
            };

            foreach (var f in manifest.Files) {
                var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                if (rel.Length == 0) {
                    continue;
                }

                // Preserve-файлы апдейтер не перезаписывает — качать их бессмысленно,
                // а служебный мусор апдейтера в пакет вообще попадать не должен.
                if (SelfUpdateRules.Preserve.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel)) {
                    continue;
                }

                if (SelfUpdateVersions.LocalFileMatches(baseDir, stripPrefix, f, out var reason)) {
                    continue;
                }

                plan.Downloads.Add(new FileTask {
                    RelativePath = rel,
                    Size = f.Size,
                    Url = ContentUrl.Combine(contentBase, rel),
                    Blake3 = f.Blake3,
                    Sha256 = f.Sha256,
                    Executable = f.Executable,
                });
                plan.TotalDownloadBytes += f.Size;
                try {
                    Logging.Logger.Info($"SelfUpdate diff include '{rel}' size={f.Size} reason={reason}");
                }
                catch {
                }
            }

            plan.TotalFilesToDownload = plan.Downloads.Count;

            // ВАЖНО: ToDelete и EmptyDirsToCreate плана намеренно пусты.
            // Их LocalRoot — это временный каталог, и ExecuteAsync применил бы их к нему,
            // а не к папке установки. Реальные удаления/пустые каталоги едут отдельными
            // списками (deletelist.txt / emptydirs.txt) и применяются апдейтером.
            return plan;
        }

        /// <summary>
        /// Список удалений — всё, чего нет в манифесте.
        /// A10: пути манифеста приводим к путям относительно папки установки (strip-prefix),
        /// иначе при упакованной корневой папке в список попадёт ВСЯ папка установки.
        /// </summary>
        /// <param name="manifest">Манифест целевой версии.</param>
        /// <param name="stripPrefix">Общая корневая папка пакета.</param>
        /// <returns>Пути относительно папки установки.</returns>
        internal List<string> BuildDeleteList(Manifest manifest, string stripPrefix) {
            var toDelete = new List<string>();
            try {
                var targetDirForDel = this.paths.TargetDir;
                var manifestSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.Files) {
                    manifestSet.Add(SelfUpdateVersions.StripLocal(stripPrefix, f.Path ?? string.Empty));
                }

                if (manifestSet.Count > 0) {
                    foreach (var diskFile in Directory.EnumerateFiles(targetDirForDel, "*", SearchOption.AllDirectories)) {
                        var rel = diskFile.Substring(targetDirForDel.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                        if (SelfUpdateRules.Preserve.ShouldPreserve(rel)) {
                            continue;
                        }

                        if (PreserveMatcher.IsUpdaterArtifact(rel)) {
                            // Служебный мусор апдейтера удаляет он сам (CleanupUpdaterArtifacts).
                            continue;
                        }

                        if (!manifestSet.Contains(rel)) {
                            toDelete.Add(rel);
                        }
                    }
                }

                // Пустой манифест — удалять нечего; страхуемся от сноса установки.
            }
            catch {
                // Не смогли посчитать удаления — обновление это не отменяет, список остаётся пустым.
                toDelete.Clear();
            }

            return toDelete;
        }

        /// <summary>
        /// A12. Ситуация «версии разные, но все файлы манифеста уже лежат на месте».
        /// Гонять апдейтер незачем — копировать и удалять нечего. Обновляем только маркер версии,
        /// иначе диалог обновления будет всплывать при каждом запуске.
        /// </summary>
        /// <param name="manifest">Манифест целевой версии (пустой манифест маркер не обновляет).</param>
        /// <param name="remoteVersion">Версия, которую записываем в маркер.</param>
        private void MarkAlreadyUpToDate(Manifest manifest, string remoteVersion) {
            // A8. Раньше ошибка записи маркера просто проглатывалась, а ResetUpdateAttempts()
            // вызывался всё равно. Итог: маркер по-прежнему показывает старую версию, диалог
            // обновления всплывает при КАЖДОМ запуске, а счётчик попыток обнулён — то есть
            // защита от петли, которая обязана была её остановить, обезврежена этим же кодом.
            // Теперь неудача — это неудача: счётчик не сбрасываем, попытку засчитываем
            // (после MaxSameVersionAttempts сработает loop guard и предложит выход),
            // и пользователь видит причину, а не молчаливо зацикленный диалог.
            if (manifest.Files.Count > 0 && !string.IsNullOrWhiteSpace(remoteVersion)) {
                if (!SelfUpdateVersions.TryWriteVersionMarker(this.paths.InstallDir, remoteVersion, out var error)) {
                    this.attempts.Register(remoteVersion);
                    this.apply(new SelfUpdateUiState {
                        Indeterminate = false,
                        ProgressValue = 100,
                        ButtonContent = "Продолжить",
                        ButtonEnabled = true,
                        StatusText =
                            "Файлы лаунчера уже соответствуют новой версии, но записать отметку о версии не удалось:\n" +
                            $"{error}\n" +
                            $"Файл: {this.paths.VersionMarker}\n" +
                            "Пока это не исправлено, окно обновления будет появляться при каждом запуске.",
                    });
                    return;
                }
            }

            this.attempts.Reset();
            this.apply(new SelfUpdateUiState {
                Indeterminate = false,
                ProgressValue = 100,
                ButtonContent = "Продолжить",
                ButtonEnabled = true,
                StatusText = "Файлы лаунчера уже соответствуют новой версии — обновление не требуется.",
            });
        }
    }
}
