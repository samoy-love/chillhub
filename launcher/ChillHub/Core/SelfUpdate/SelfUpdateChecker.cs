// <copyright file="SelfUpdateChecker.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;
    using ChillHub.Update;

    /// <summary>Чем закончилась проверка обновления при старте лаунчера.</summary>
    internal enum SelfUpdateState {
        /// <summary>Установлена актуальная версия — обновляться не нужно.</summary>
        UpToDate,

        /// <summary>A6. Сервер сообщил номер версии, который нельзя пускать в путь и в аргументы.</summary>
        InvalidRemoteVersion,

        /// <summary>Ни локальная, ни удалённая версия не известны — решает пользователь.</summary>
        VersionUnknown,

        /// <summary>Есть новая версия, обновление можно применять.</summary>
        UpdateAvailable,

        /// <summary>Манифест целевой версии не прошёл проверку структуры.</summary>
        ManifestRejected,

        /// <summary>A4. Автообновление остановлено защитой от петли.</summary>
        LoopBlocked,

        /// <summary>Проверка не состоялась (нет сети, сервер отдал мусор) — решает пользователь.</summary>
        CheckFailed,
    }

    /// <summary>Решение проверки вместе с тем, что окну показать.</summary>
    internal sealed class SelfUpdateDecision {
        internal SelfUpdateState State { get; init; }

        /// <summary>Установленная версия (для состояния <see cref="SelfUpdateState.UpdateAvailable"/>).</summary>
        internal string LocalVersion { get; init; } = string.Empty;

        /// <summary>Версия с сервера; она же становится <c>remoteVersion</c> окна.</summary>
        internal string RemoteVersion { get; init; } = string.Empty;

        /// <summary>A10. Общая корневая папка пакета, посчитанная по манифесту.</summary>
        internal string StripPrefix { get; init; } = string.Empty;

        internal SelfUpdateUiState Ui { get; init; } = new SelfUpdateUiState();

        /// <summary>Есть ли новая версия — прежнее поле <c>updateRequired</c>.</summary>
        internal bool UpdateRequired => this.State == SelfUpdateState.UpdateAvailable;

        /// <summary>A4: автообновление остановлено защитой от петли — прежнее поле <c>loopBlocked</c>.</summary>
        internal bool LoopBlocked => this.State == SelfUpdateState.LoopBlocked;
    }

    /// <summary>Результат выхода из состояния «остановлено защитой от петли».</summary>
    internal sealed class IntegrityCheckResult {
        internal SelfUpdateUiState Ui { get; init; } = new SelfUpdateUiState();

        /// <summary>Пересчитанный strip-prefix; null — проверка до манифеста не дошла.</summary>
        internal string? StripPrefix { get; init; }
    }

    /// <summary>
    /// Решение «нужно ли обновляться» и выход из тупика защиты от петли.
    /// <para>
    /// Это самая дорогая ошибка во всём лаунчере: неверное «да» кладёт чужие файлы
    /// поверх работающей установки, неверное «нет» оставляет пользователя на старой
    /// версии навсегда. Класс ничего не знает про окно — он возвращает решение.
    /// </para>
    /// </summary>
    internal sealed class SelfUpdateChecker {
        /// <summary>
        /// Сколько раз подряд разрешено применять обновление на одну и ту же версию.
        /// Больше — значит апдейтер не доводит дело до конца, и мы крутимся в петле.
        /// </summary>
        internal const int MaxSameVersionAttempts = 3;

        private readonly HttpClient http;
        private readonly ISyncService sync;
        private readonly Func<string> baseApi;
        private readonly SelfUpdatePaths paths;
        private readonly UpdateAttemptsStore attempts;

        internal SelfUpdateChecker(
            HttpClient http,
            ISyncService sync,
            Func<string> baseApi,
            SelfUpdatePaths paths,
            UpdateAttemptsStore attempts) {
            this.http = http;
            this.sync = sync;
            this.baseApi = baseApi;
            this.paths = paths;
            this.attempts = attempts;
        }

        private sealed class LatestMeta {
            public string Version { get; set; } = string.Empty;
        }

        /// <summary>
        /// Спрашивает сервер о последней версии и решает, надо ли обновляться.
        /// </summary>
        /// <returns>Решение и состояние окна.</returns>
        internal async Task<SelfUpdateDecision> CheckAsync() {
            try {
                var latest = await this.http.GetFromJsonAsync<LatestMeta>($"{this.baseApi()}/manifests/launcher/latest.json");
                var remote = latest?.Version?.Trim();
                var local = SelfUpdateVersions.ReadLocalVersion(this.paths.InstallDir);

                // A6. Версия с сервера — недоверенные данные: она станет частью пути,
                // URL и аргументов внешнего процесса. Всё, что не похоже на версию,
                // отбрасываем целиком, а не «чистим».
                if (!string.IsNullOrWhiteSpace(remote) && !SelfUpdateVersions.IsValidVersion(remote)) {
                    try {
                        Logging.Logger.Error(new InvalidOperationException($"Rejected remote version from latest.json: '{remote}'"), "UpdateWindow.VersionValidation");
                    }
                    catch {
                    }

                    return new SelfUpdateDecision {
                        State = SelfUpdateState.InvalidRemoteVersion,
                        Ui = new SelfUpdateUiState {
                            StatusText =
                                "Сервер сообщил недопустимый номер версии — обновление заблокировано.\n" +
                                "Обратитесь в поддержку.",
                            Indeterminate = false,
                            ProgressValue = 0,
                            ButtonContent = "Продолжить",
                            ButtonEnabled = true,
                        },
                    };
                }

                if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(local)) {
                    // Ничего не знаем — даём пользователю решить
                    return new SelfUpdateDecision {
                        State = SelfUpdateState.VersionUnknown,
                        Ui = new SelfUpdateUiState {
                            StatusText = "Информация о версии отсутствует.",
                            Indeterminate = false,
                            ProgressValue = 0,
                            ButtonContent = "Продолжить",
                            ButtonEnabled = true,
                        },
                    };
                }

                // A1. Главный предохранитель: если версии совпали — обновляться не надо ВООБЩЕ.
                // Посимвольную сверку хешей запускать нельзя: preserve-файлы (config.json,
                // launcher.version) заведомо расходятся с манифестом и дают вечную петлю.
                if (string.Equals(remote, local, StringComparison.OrdinalIgnoreCase)) {
                    this.attempts.Reset();
                    return UpToDate(local, remote, string.Empty);
                }

                // Понижение версии обновлением не считается. Раньше «не равно» означало
                // «обновляемся»: latest.json, указывающий на СТАРУЮ сборку (откат оператора,
                // протухший кеш, чужой адрес сервера в config.json), молча заменял лаунчер
                // на более раннюю версию — вместе с уже закрытыми в ней дырами. Подписи
                // манифестов из формата убраны, поэтому кроме этой проверки понижение
                // ничем не ограничено. Сравниваем по смыслу — тем же VersionOrder,
                // которым давно упорядочиваются сборки игр.
                if (VersionOrder.Compare(remote, local) < 0) {
                    try {
                        Logging.Logger.Warn($"Self-update отклонён: сервер предлагает версию {remote}, установлена более новая {local}");
                    }
                    catch {
                    }

                    return UpToDate(local, remote, string.Empty);
                }

                // Версии разные — уточняем решение по манифесту (вдруг файлы уже на месте).
                Manifest? mf = null;
                try {
                    var manifestUrl = $"{this.baseApi()}/manifests/launcher/{remote}.json";
                    mf = await this.sync.GetManifestAsync(manifestUrl, CancellationToken.None);
                }
                catch (ManifestValidationException ex) {
                    // Манифест отклонён проверкой структуры — предлагать обновление
                    // нельзя: качать по такому манифесту мы всё равно откажемся.
                    try {
                        Logging.Logger.Error(ex, "UpdateWindow.CheckManifestValidation");
                    }
                    catch {
                    }

                    return new SelfUpdateDecision {
                        State = SelfUpdateState.ManifestRejected,
                        LocalVersion = local,
                        RemoteVersion = remote,
                        Ui = new SelfUpdateUiState {
                            StatusText = $"Обновление заблокировано: {ex.Message}",
                            ButtonEnabled = false,
                        },
                    };
                }
                catch {
                    // Фоллбэк: если манифест не доступен — используем сравнение по версии, как раньше
                    return this.ApplyDecision(true, local, remote, string.Empty);
                }

                // A10. Пакет может быть упакован с корневой папкой — считаем префикс один раз
                // и используем его симметрично: и в сверке хешей, и в списке удалений, и в аргументах апдейтера.
                var stripPrefix = SelfUpdateVersions.ComputeStripPrefix(mf);

                // 2) Сравниваем локальные файлы с хешами из манифеста
                bool allMatch = true;
                try {
                    foreach (var f in mf.Files) {
                        var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                        if (rel.Length == 0) {
                            continue;
                        }

                        // A2. Preserve-файлы принципиально не совпадают с манифестом
                        // (апдейтер их не перезаписывает) — они не могут быть причиной обновления.
                        if (SelfUpdateRules.Preserve.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel)) {
                            continue;
                        }

                        if (!SelfUpdateVersions.LocalFileMatches(this.paths.InstallDir, stripPrefix, f, out _)) {
                            allMatch = false;
                            break;
                        }
                    }
                }
                catch {
                    allMatch = false;
                }

                return this.ApplyDecision(!allMatch, local, remote, stripPrefix);
            }
            catch (Exception ex) {
                // Нет сети/latest — даём пользователю решить
                try {
                    Logging.Logger.Error(ex, "UpdateWindow.Window_Loaded");
                }
                catch {
                }

                return new SelfUpdateDecision {
                    State = SelfUpdateState.CheckFailed,
                    Ui = new SelfUpdateUiState {
                        StatusText = $"Не удалось проверить обновление (GET {this.baseApi()}/manifests/launcher/latest.json): {ex.Message}",
                        Indeterminate = false,
                        ProgressValue = 0,
                        ButtonContent = "Продолжить",
                        ButtonEnabled = true,
                    },
                };
            }
        }

        /// <summary>
        /// A4. Выход из состояния «автообновление остановлено защитой от петли».
        /// <para>
        /// Сверяет установку с манифестом целевой версии. Совпало всё — установка
        /// исправна, значит петля была ложной (например, обновление уже применилось,
        /// а маркер не записался): пишем маркер и сбрасываем счётчик. Не совпало —
        /// счётчик НЕ трогаем (защита обязана остаться), но показываем конкретные
        /// файлы и разблокируем кнопку «Продолжить», чтобы пользователь не оставался
        /// заперт в диалоге обновления.
        /// </para>
        /// </summary>
        /// <param name="remoteVersion">Версия, на которую лаунчер пытался обновиться.</param>
        /// <param name="setChecking">Показать «идёт проверка» до первого обращения к сети.</param>
        /// <returns>Что показать в окне.</returns>
        internal async Task<IntegrityCheckResult> VerifyIntegrityAsync(string? remoteVersion, Action<SelfUpdateUiState> setChecking) {
            var remote = remoteVersion;
            if (string.IsNullOrWhiteSpace(remote) || !SelfUpdateVersions.IsValidVersion(remote)) {
                return new IntegrityCheckResult {
                    Ui = new SelfUpdateUiState { ButtonContent = "Продолжить", ButtonEnabled = true },
                };
            }

            setChecking(new SelfUpdateUiState {
                ButtonEnabled = false,
                Indeterminate = true,
                StatusText = $"Проверка целостности установки по манифесту {remote}...",
            });

            try {
                var manifest = await this.sync.GetManifestAsync(
                    $"{this.baseApi()}/manifests/launcher/{remote}.json", CancellationToken.None);
                var stripPrefix = SelfUpdateVersions.ComputeStripPrefix(manifest);

                var bad = new List<string>();
                foreach (var f in manifest.Files) {
                    var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                    if (rel.Length == 0 || SelfUpdateRules.Preserve.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel)) {
                        continue;
                    }

                    if (!SelfUpdateVersions.LocalFileMatches(this.paths.InstallDir, stripPrefix, f, out var reason)) {
                        bad.Add($"{rel} — {reason}");
                    }
                }

                if (bad.Count == 0) {
                    if (!SelfUpdateVersions.TryWriteVersionMarker(this.paths.InstallDir, remote!, out var markerError)) {
                        return new IntegrityCheckResult {
                            StripPrefix = stripPrefix,
                            Ui = new SelfUpdateUiState {
                                Indeterminate = false,
                                StatusText =
                                    "Файлы установки соответствуют новой версии, но записать отметку о версии не удалось:\n" +
                                    $"{markerError}\n" +
                                    "Счётчик попыток не сброшен. Проверьте права на папку установки.",
                                ButtonContent = "Продолжить",
                                ButtonEnabled = true,
                            },
                        };
                    }

                    this.attempts.Reset();
                    try {
                        Logging.Logger.Info($"Loop guard released: integrity ok for {remote}, attempts reset");
                    }
                    catch {
                    }

                    return new IntegrityCheckResult {
                        StripPrefix = stripPrefix,
                        Ui = UpToDateUi(),
                    };
                }

                // Расхождения есть — счётчик оставляем как есть, но выпускаем пользователя.
                try {
                    Logging.Logger.Error(
                        new InvalidOperationException($"Loop guard integrity check failed for {remote}: {string.Join("; ", bad.Take(20))}"),
                        "UpdateWindow.LoopGuardIntegrity");
                }
                catch {
                }

                return new IntegrityCheckResult {
                    StripPrefix = stripPrefix,
                    Ui = new SelfUpdateUiState {
                        Indeterminate = false,
                        ProgressValue = 0,
                        StatusText =
                            $"Проверка целостности не пройдена: расхождений {bad.Count}.\n" +
                            string.Join("\n", bad.Take(5)) +
                            (bad.Count > 5 ? $"\n... и ещё {bad.Count - 5}" : string.Empty) + "\n" +
                            "Счётчик попыток не сброшен — автообновление остаётся остановленным.\n" +
                            "Переустановите лаунчер вручную или обратитесь в поддержку. Запустить лаунчер можно кнопкой ниже.",
                        ButtonContent = "Продолжить",
                        ButtonEnabled = true,
                    },
                };
            }
            catch (Exception ex) {
                try {
                    Logging.Logger.Error(ex, "UpdateWindow.LoopGuardIntegrity");
                }
                catch {
                }

                return new IntegrityCheckResult {
                    Ui = new SelfUpdateUiState {
                        Indeterminate = false,
                        StatusText =
                            $"Не удалось проверить целостность: {ex.Message}\n" +
                            "Счётчик попыток не сброшен. Попробуйте позже или переустановите лаунчер вручную.",
                        ButtonContent = "Продолжить",
                        ButtonEnabled = true,
                    },
                };
            }
        }

        /// <summary>
        /// Применяет решение «нужно обновление / не нужно» с учётом защиты от зацикливания (A1).
        /// </summary>
        private SelfUpdateDecision ApplyDecision(bool needUpdate, string local, string remote, string stripPrefix) {
            if (!needUpdate) {
                this.attempts.Reset();
                return UpToDate(local, remote, stripPrefix);
            }

            var attempts = this.attempts.Get(remote);
            if (attempts >= MaxSameVersionAttempts) {
                // Обновление на одну и ту же версию применяется по кругу — дальше не пускаем.
                //
                // A4. Но и тупик здесь недопустим. К этому моменту установка уже в
                // смешанном состоянии, а счётчик сбрасывался ТОЛЬКО при remote == local,
                // то есть ровно в том случае, до которого зацикленный лаунчер и не
                // доходит: обновление запрещалось навсегда, и единственным выходом
                // оставалась переустановка вслепую. Даём конкретное действие —
                // проверку целостности: если файлы на самом деле в порядке, счётчик
                // сбрасывается и лаунчер продолжает работу.
                var logDir = this.paths.WorkDir(remote);
                try {
                    Logging.Logger.Error(new InvalidOperationException($"Self-update loop detected: {local} -> {remote}, attempts={attempts}"), "UpdateWindow.LoopGuard");
                }
                catch {
                }

                return new SelfUpdateDecision {
                    State = SelfUpdateState.LoopBlocked,
                    LocalVersion = local,
                    RemoteVersion = remote,
                    StripPrefix = stripPrefix,
                    Ui = new SelfUpdateUiState {
                        Indeterminate = false,
                        ProgressValue = 0,
                        StatusText =
                            $"Обновление {local} → {remote} применялось {attempts} раз(а) подряд и не завершилось успехом.\n" +
                            "Чтобы не зацикливаться, автообновление остановлено.\n" +
                            $"Журнал: {Path.Combine(logDir, "apply-update.log")}\n" +
                            $"Счётчик попыток: {this.attempts.FilePath}\n" +
                            "Нажмите «Проверить целостность»: файлы будут сверены с манифестом версии " +
                            $"{remote}. Если расхождений нет, счётчик сбросится и лаунчер продолжит работу; " +
                            "если есть — вы увидите список файлов, и лаунчер всё равно можно будет запустить.",
                        ButtonContent = "Проверить целостность",
                        ButtonEnabled = true,
                    },
                };
            }

            return new SelfUpdateDecision {
                State = SelfUpdateState.UpdateAvailable,
                LocalVersion = local,
                RemoteVersion = remote,
                StripPrefix = stripPrefix,

                // Текст статуса здесь намеренно пуст: «1.2.3 → 1.2.4» рисуется цветными
                // Inlines и остаётся в окне. Всё остальное состояние — здесь.
                Ui = new SelfUpdateUiState {
                    Indeterminate = false,
                    ProgressValue = 0,
                    ButtonContent = "Обновить и перезапустить",
                    ButtonEnabled = true,
                },
            };
        }

        /// <summary>Состояние «установлена актуальная версия лаунчера».</summary>
        internal static SelfUpdateUiState UpToDateUi() => new SelfUpdateUiState {
            StatusText = "Установлена актуальная версия лаунчера.",
            Indeterminate = false,
            ProgressValue = 100,
            ButtonContent = "Продолжить",
            ButtonEnabled = true,
        };

        private static SelfUpdateDecision UpToDate(string local, string remote, string stripPrefix) => new SelfUpdateDecision {
            State = SelfUpdateState.UpToDate,
            LocalVersion = local,
            RemoteVersion = remote,
            StripPrefix = stripPrefix,
            Ui = UpToDateUi(),
        };
    }
}
