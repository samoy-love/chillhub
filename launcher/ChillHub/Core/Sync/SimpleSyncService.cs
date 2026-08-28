// <copyright file="SimpleSyncService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    using Blake3;

    using ChillHub.Core;
    using ChillHub.Core.Net;
    using ChillHub.Update;

    public class SimpleSyncService : ISyncService {
        /// <summary>
        /// Имя файла-маркера незавершённого обновления в корне игры.
        /// Существует только на время фазы активации: если он остался — обновление
        /// прервали (отмена/исключение/выключение), и игру нельзя считать рабочей.
        /// </summary>
        public const string UpdateMarkerFileName = ".updating";

        /// <summary>Минимальный интервал между отчётами о прогрессе скачивания, мс.</summary>
        private const int ProgressThrottleMs = 100;

        /// <summary>Размер буфера чтения сети при скачивании файла.</summary>
        private const int DownloadBufferBytes = 256 * 1024;

        /// <summary>Сколько файлов из плана показать в логе поимённо; остальные — только в сводке.</summary>
        private const int PlanLogSamples = 20;

        /// <summary>
        /// Сколько ждать следующий байт от сервера, прежде чем считать попытку зависшей, мс.
        /// Это таймаут ПРОСТОЯ, а не всей загрузки: пока данные идут, он сбрасывается,
        /// поэтому многогигабайтный файл на медленном канале докачивается, а мёртвое
        /// соединение всё равно обрывается и уходит в ретрай с докачкой по Range.
        /// </summary>
        private const int StallTimeoutMs = 100_000;

        private readonly HttpClient http;
        private readonly HttpClient downloadHttp;

        public SimpleSyncService(HttpClient? http = null) {
            this.http = http ?? HttpClientProvider.Shared;

            // Тело файла читается клиентом без общего таймаута: HttpClient.Timeout тикает
            // и во время чтения потока, то есть на общем клиенте (100 с) любой файл, который
            // качается дольше, обрывался посреди загрузки. Явно переданный клиент уважаем
            // как есть — его подставляют тесты.
            this.downloadHttp = http ?? HttpClientProvider.Downloads;
        }

        /// <summary>
        /// Проверяет наличие маркера незавершённого обновления в папке игры.
        /// </summary>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <returns>true, если обновление было прервано и требуется докатить его.</returns>
        public static bool HasUpdateMarker(string localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot)) {
                    return false;
                }

                return File.Exists(Path.Combine(localRoot, UpdateMarkerFileName));
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"HasUpdateMarker({localRoot})");
                return false;
            }
        }

        /// <summary>
        /// Читает содержимое маркера незавершённого обновления (для диагностики/подсказок).
        /// </summary>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <returns>Содержимое маркера либо пустая строка.</returns>
        public static string ReadUpdateMarker(string localRoot) {
            try {
                var path = Path.Combine(localRoot, UpdateMarkerFileName);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"ReadUpdateMarker({localRoot})");
                return string.Empty;
            }
        }

        /// <inheritdoc/>
        public async Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
            var manifest = await this.http.GetFromJsonAsync<Manifest>(manifestUrl, ct)
                           ?? throw new InvalidDataException("manifest is null");

            // Проверяем ДО того, как что-либо качать и применять: манифест задаёт список
            // файлов и их хеши, то есть именно он определяет, какие исполняемые файлы
            // окажутся на диске. Проверка здесь, в единственной точке загрузки манифеста,
            // закрывает и синхронизацию игр, и самообновление лаунчера.
            // Опасный путь отвергается ВСЕГДА, в любом режиме совместимости.
            ManifestValidator.Validate(manifest, manifestUrl);
            return manifest;
        }

        /// <inheritdoc/>
        public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct) {
            return this.PlanAsync(manifest, localRoot, contentBaseUrl, PlanOptions.Default, ct);
        }

        /// <inheritdoc/>
        public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct) {
            options ??= PlanOptions.Default;

            // Планировщик — вторая (и последняя) точка входа манифеста в код,
            // который трогает диск: сюда попадают и манифесты, полученные не через
            // GetManifestAsync. Проверка идемпотентна и стоит копейки.
            ManifestValidator.Validate(manifest, $"план для '{localRoot}'");

            // Пути, которыми в этом же корне владеет ЧУЖОЙ манифест: для синхронизации
            // игры это файлы установленного модпака. Модпак ставится в папку игры, а не
            // в отдельный профиль (иначе BepInEx не заработает), поэтому без этого списка
            // первое же обновление игры вынесло бы все моды как «лишние файлы».
            var foreignPaths = NormalizeRelSet(options.ForeignPaths);

            // Файлы, которые правит сам лаунчер: ставим, если их нет, и больше не
            // сверяем. Иначе переключение «с модами / без модов», меняющее значение
            // в doorstop_config.ini, читалось бы как повреждение файла.
            var preservePaths = NormalizeRelSet(options.PreservePaths);

            var plan = new DiffPlan {
                GameId = manifest.GameId,
                Version = manifest.Version,
                LocalRoot = localRoot,

                // Едет вместе с планом ради второго рубежа в FinishPlan: тот статичен
                // и настроек плана не видит, а удаление необратимо.
                ForeignPaths = new List<string>(foreignPaths),
            };

            // Соберём множество файлов из манифеста
            var manifestFiles = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            var foreignInManifest = 0;
            foreach (var mf in manifest.Files) {
                var relNorm = mf.Path.Replace('\\', '/');
                if (IsServiceRelFile(relNorm)) {
                    // Маркер незавершённого обновления — служебный файл лаунчера, в план он не попадает
                    continue;
                }

                if (IsIgnoredRelFile(relNorm)) {
                    // Исключаем спецфайл FreeTP/.hash из проверки и обновления для каждой игры.
                    // Это нужно для пиратских сборок с сайта FreeTP.Org, чтобы при запуске игры
                    // не открывался этот сайт каждый раз (файл ".hash" в папке FreeTP трогать не нужно).
                    continue;
                }

                if (foreignPaths.Contains(relNorm)) {
                    // Тот же путь есть и в чужом манифесте. Так выглядит миграция: старые
                    // сборки игры содержат BepInEx внутри себя, а модпак теперь владеет
                    // теми же файлами. Хозяин один, и это НЕ мы: скачаем — затрём моды
                    // содержимым из сборки, удалим — уроним моды вовсе.
                    foreignInManifest++;
                    continue;
                }

                manifestFiles[relNorm] = mf;
            }

            if (foreignInManifest > 0) {
                ChillHub.Core.Logging.Logger.Info(
                    $"Plan gid={manifest.GameId} ver={manifest.Version}: {foreignInManifest} файл(ов) манифеста отдано чужому манифесту в том же корне");
            }

            // Локальные файлы относительно корня. Чужие пути сюда не попадают — значит
            // и в ToDelete они не попадут, — но остаются в отдельном списке: они живы,
            // и кеш хешей не должен считать их исчезнувшими.
            var foreignExisting = new List<string>();
            var localExisting = ListLocalFiles(localRoot, foreignPaths, foreignExisting);

            // Кеш хешей: при неизменных размере и времени модификации файл не перечитывается.
            // Кеш свой на КАЖДУЮ папку: одна и та же игра живёт и в копии из Steam, и в
            // сборке с сервера, а записи ключуются относительным путём.
            var hashCache = FileHashCache.Load(manifest.GameId, localRoot);

            // Счётчики для отчёта о прогрессе: считаем «проверенные» файлы манифеста и их байты
            var checkedFiles = 0;
            long checkedBytes = 0;
            var totalToCheck = manifestFiles.Count;
            long totalBytesToCheck = 0;

            // Считаем полный вес сборки ВСЕГДА, а не только при подписке на
            // прогресс: это же число уходит в метрику экономии трафика, и
            // молча терять его на тихих сценариях (самообновление, проверка
            // целостности) нельзя.
            foreach (var kv in manifestFiles) {
                totalBytesToCheck += kv.Value.Size;
            }

            plan.TotalManifestBytes = totalBytesToCheck;
            plan.TotalManifestFiles = totalToCheck;

            if (options.Progress != null) {
                options.Progress.Report(new SyncProgress {
                    Stage = "Checking",
                    FilesDownloaded = 0,
                    TotalFiles = totalToCheck,
                    BytesDownloaded = 0,
                    TotalBytes = totalBytesToCheck,
                });
            }

            // Причины попадания в план: сводка вместо строки лога на каждый файл.
            // Построчный лог обходился в открытие файла на запись на КАЖДЫЙ файл сборки
            // (десятки тысяч), а мегабайты однотипных строк вытесняли ротацией всё, ради
            // чего лог и читают. Примеры оставляем — по ним чинят конкретные сборки.
            var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var reasonSamples = new List<string>();

            // Определим новые/изменённые: при наличии хеша сравниваем по хешу, иначе по размеру
            foreach (var kv in manifestFiles) {
                ct.ThrowIfCancellationRequested();
                var rel = kv.Key;
                var mf = kv.Value;

                // ManifestPath.Combine, а не Path.Combine: путь пришёл из манифеста,
                // и он обязан остаться внутри корня игры (включая случай с junction'ом).
                var localPath = ManifestPath.Combine(localRoot, rel);
                bool needDownload = true;
                string reason = "missing";
                long localSize = 0;

                // Файл из preserve-списка уже на месте — он наш, его правит лаунчер,
                // и содержимое манифеста для него не эталон. Отсутствующий ставим как
                // обычно: без него моды не запустятся вовсе.
                if (preservePaths.Contains(rel) && File.Exists(localPath)) {
                    checkedFiles++;
                    checkedBytes += mf.Size;
                    continue;
                }

                if (File.Exists(localPath)) {
                    try {
                        var info = new FileInfo(localPath);
                        localSize = info.Length;

                        // Если есть sha256/blake3 в манифесте — считаем локальный хеш и сравним
                        if (!string.IsNullOrWhiteSpace(mf.Sha256) || !string.IsNullOrWhiteSpace(mf.Blake3)) {
                            if (mf.Size > 0 && info.Length != mf.Size) {
                                // Размер отличается — хеш заведомо не совпадёт, файл читать незачем
                                needDownload = true;
                                reason = $"size_mismatch local={info.Length} manifest={mf.Size}";
                            }
                            else {
                                var mtimeTicks = info.LastWriteTimeUtc.Ticks;
                                string shaHex;
                                string b3Hex;

                                // В режиме проверки целостности кеш не спрашиваем: он подтвердил бы
                                // повреждённый файл по совпадению размера и времени модификации.
                                if (options.ForceRehash || !hashCache.TryGet(rel, info.Length, mtimeTicks, out shaHex, out b3Hex)) {
                                    FileHasher.ComputeHashes(localPath, out shaHex, out b3Hex, ct);
                                    hashCache.Set(rel, info.Length, mtimeTicks, shaHex, b3Hex);
                                }

                                var shaOk = string.IsNullOrWhiteSpace(mf.Sha256) || string.Equals(shaHex, mf.Sha256, StringComparison.OrdinalIgnoreCase);
                                var b3Ok = string.IsNullOrWhiteSpace(mf.Blake3) || string.Equals(b3Hex, mf.Blake3, StringComparison.OrdinalIgnoreCase);
                                if (shaOk && b3Ok) {
                                    needDownload = false;
                                }
                                else {
                                    needDownload = true;
                                    reason = $"hash_mismatch shaOk={shaOk} b3Ok={b3Ok}";

                                    // Файл на месте и нужного размера, а хеш другой:
                                    // это порча, а не «ещё не скачали». Считаем
                                    // отдельно от остальных причин загрузки.
                                    plan.HashMismatches++;
                                }
                            }
                        }
                        else {
                            // Фоллбэк: сравнение по размеру
                            if (info.Length == mf.Size) {
                                needDownload = false;
                            }
                            else {
                                reason = $"size_mismatch local={info.Length} manifest={mf.Size}";
                            }
                        }
                    }
                    catch (OperationCanceledException) {
                        // Отмену не глушим: иначе отменённая проверка «нашла бы» повреждённый файл
                        throw;
                    }
                    catch {
                    }
                }

                if (needDownload) {
                    var task = new FileTask {
                        RelativePath = rel,
                        Size = mf.Size,
                        Url = CombineUrl(contentBaseUrl, rel),
                        Blake3 = mf.Blake3,
                        Sha256 = mf.Sha256,
                        Executable = mf.Executable,
                    };

                    // Такой же файл уже лежит в другой копии этой игры — возьмём оттуда.
                    // Сверку он пройдёт ту же, что и скачанный, поэтому ошибиться здесь
                    // можно разве что лишним копированием.
                    task.LocalSource = LocalDonors.Find(options.Donors, task) ?? string.Empty;
                    if (task.LocalSource.Length > 0) {
                        plan.ReusedFiles++;
                        plan.ReusedBytes += mf.Size;
                    }

                    plan.Downloads.Add(task);
                    plan.TotalDownloadBytes += mf.Size;

                    // Место, которое освободит этот файл, когда новая версия встанет на
                    // его место. Нужно для честной оценки требуемого свободного места:
                    // заменяемый файл не прибавляет к занятому объёму, он его меняет.
                    plan.ReplacedBytes += localSize;

                    // Ключ сводки — вид причины без цифр: «size_mismatch local=… manifest=…»
                    // у каждого файла свой, и как ключ он бы дал столько же строк, сколько файлов.
                    var kind = reason.Split(' ')[0];
                    reasonCounts[kind] = reasonCounts.TryGetValue(kind, out var c) ? c + 1 : 1;
                    if (reasonSamples.Count < PlanLogSamples) {
                        reasonSamples.Add($"'{rel}' size={mf.Size} reason={reason}");
                    }
                }

                if (options.Progress != null) {
                    checkedFiles++;
                    checkedBytes += mf.Size;
                    options.Progress.Report(new SyncProgress {
                        Stage = "Checking",
                        FilesDownloaded = checkedFiles,
                        TotalFiles = totalToCheck,
                        BytesDownloaded = checkedBytes,
                        TotalBytes = totalBytesToCheck,
                    });
                }
            }

            plan.TotalFilesToDownload = plan.Downloads.Count;

            if (plan.Downloads.Count > 0) {
                var byReason = string.Join(", ", reasonCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}"));
                ChillHub.Core.Logging.Logger.Info(
                    $"Plan gid={manifest.GameId} ver={manifest.Version} files={plan.Downloads.Count}/{totalToCheck} bytes={plan.TotalDownloadBytes}/{totalBytesToCheck} reasons: {byReason}");
                foreach (var sample in reasonSamples) {
                    ChillHub.Core.Logging.Logger.Info($"Plan include {sample}");
                }

                if (plan.Downloads.Count > reasonSamples.Count) {
                    ChillHub.Core.Logging.Logger.Info($"Plan include ... ещё {plan.Downloads.Count - reasonSamples.Count} файл(ов), см. сводку выше");
                }
            }

            // Чистим кеш от записей об исчезнувших файлах и сохраняем, если что-то поменялось.
            //
            // Живыми считаем И чужие файлы тоже. Кеш ключуется относительным путём и
            // лежит один на корень, то есть записи обоих манифестов в нём вперемешку.
            // Отдай мы сюда только свой список — прополка выбросила бы записи соседа,
            // и следующая синхронизация модпака перечитала бы с диска все свои гигабайты
            // заново. Не ошибка, а молчаливая потеря минут на ровном месте.
            var aliveForCache = new List<string>(localExisting.Count + foreignExisting.Count);
            aliveForCache.AddRange(localExisting);
            aliveForCache.AddRange(foreignExisting);
            hashCache.PruneAndSave(aliveForCache);

            // Пустые директории для создания.
            //
            // Канонизируем ровно так же, как валидатор: он допускает завершающий
            // слеш у каталога и проверяет форму УЖЕ без него. Клади мы в план сырую
            // строку — ApplyPlan подставил бы её в ManifestPath.Combine, а тот
            // отвергает неканоническую форму, и обновление падало бы на манифесте,
            // который сам же лаунчер только что признал корректным.
            foreach (var d in manifest.EmptyDirs) {
                plan.EmptyDirsToCreate.Add(ManifestPath.Canonicalize(d));
            }

            // Файлы к удалению. Что именно считается лишним, зависит от того, чем этот
            // манифест владеет в корне: сборка игры владеет всем, модпак — только тем,
            // что сам когда-то положил.
            if (options.Scope == ManifestScope.OwnFilesOnly) {
                AddDeletionsForOwnedManifest(plan, options, manifestFiles, localExisting, foreignPaths);
            }
            else {
                AddDeletionsForWholeRoot(plan, manifestFiles, localExisting);
            }

            return Task.FromResult(plan);
        }

        /// <summary>
        /// Список на удаление для манифеста, который владеет всем корнем (сборка игры):
        /// лишнее — это всё, что лежит локально и чего нет в манифесте.
        /// </summary>
        /// <param name="plan">План, который дополняем.</param>
        /// <param name="manifestFiles">Файлы манифеста по относительному пути.</param>
        /// <param name="localExisting">Файлы, найденные в корне (без служебных и чужих).</param>
        private static void AddDeletionsForWholeRoot(
            DiffPlan plan,
            Dictionary<string, ManifestFile> manifestFiles,
            List<string> localExisting) {
            foreach (var relLocal in localExisting) {
                var norm = relLocal.Replace('\\', '/');
                if (IsIgnoredRelFile(norm)) {
                    continue; // не удаляем FreeTP/.hash
                }

                if (!manifestFiles.ContainsKey(norm)) {
                    // "<файл>.new" рядом с файлом ИЗ манифеста — это отложенная замена
                    // заблокированного файла, уже поставленная в очередь MoveFileEx на
                    // перезагрузку. В манифесте её нет по определению, и без этой проверки
                    // следующий план молча стирал её, отменяя обновление этого файла.
                    if (norm.EndsWith(".new", StringComparison.OrdinalIgnoreCase)
                        && manifestFiles.ContainsKey(norm.Substring(0, norm.Length - 4))) {
                        continue;
                    }

                    // "<файл>.part" рядом с файлом ИЗ манифеста — недокачанное тело этого
                    // самого файла: загрузка идёт прямо в папку игры, и по нему обновление
                    // возобновляется с места обрыва. В манифесте его нет по определению,
                    // и без этой проверки план стирал бы ровно то, ради чего докачка есть.
                    if (norm.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        && manifestFiles.ContainsKey(norm.Substring(0, norm.Length - 5))) {
                        continue;
                    }

                    plan.ToDelete.Add(norm);
                }
            }
        }

        /// <summary>
        /// Список на удаление для манифеста, который делит корень с чужим (модпак).
        /// <para>
        /// Правило одно: <c>удалить = пути ПРЕДЫДУЩЕЙ версии этого манифеста − пути НОВОЙ</c>.
        /// Обычное «всё, чего нет в манифесте» здесь означало бы снести игру целиком —
        /// десять гигабайт, о которых манифест модов ничего не знает и знать не должен.
        /// </para>
        /// <para>
        /// Предыдущего списка нет (первая установка модпака или потерянный
        /// <c>.mods.manifest.json</c>) — не удаляем НИЧЕГО. Это осознанный перекос в
        /// сторону мусора: пара забытых файлов мода безобиднее стёртой сборки.
        /// </para>
        /// </summary>
        /// <param name="plan">План, который дополняем.</param>
        /// <param name="options">Настройки плана — источник путей предыдущей установки.</param>
        /// <param name="manifestFiles">Файлы НОВОГО манифеста по относительному пути.</param>
        /// <param name="localExisting">Файлы, найденные в корне (без служебных и чужих).</param>
        /// <param name="foreignPaths">Пути чужого манифеста в этом же корне.</param>
        private static void AddDeletionsForOwnedManifest(
            DiffPlan plan,
            PlanOptions options,
            Dictionary<string, ManifestFile> manifestFiles,
            List<string> localExisting,
            HashSet<string> foreignPaths) {
            if (options.PreviousOwnedPaths == null || options.PreviousOwnedPaths.Count == 0) {
                return;
            }

            // Сверяемся с тем, что реально лежит на диске: путь из прошлой установки мог
            // исчезнуть и сам (игрок удалил файл руками), а лишняя запись в ToDelete
            // раздувает и вопрос пользователю «будет удалено файлов: N», и метрику.
            var onDisk = new HashSet<string>(localExisting, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var previous in options.PreviousOwnedPaths) {
                if (string.IsNullOrWhiteSpace(previous)) {
                    continue;
                }

                var norm = NormalizeRel(previous);
                if (manifestFiles.ContainsKey(norm)) {
                    continue; // файл остаётся в новой версии модпака
                }

                if (IsServiceRelFile(norm) || IsIgnoredRelFile(norm) || foreignPaths.Contains(norm)) {
                    continue;
                }

                if (!onDisk.Contains(norm)) {
                    continue;
                }

                if (seen.Add(norm)) {
                    plan.ToDelete.Add(norm);
                }
            }
        }

        /// <inheritdoc/>
        public async Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
            long downloaded = 0;
            int filesDone = 0;
            var total = plan.TotalDownloadBytes;
            var totalFiles = plan.TotalFilesToDownload;

            Directory.CreateDirectory(plan.LocalRoot);

            // Наследство прежней схемы: файлы качались в .staging и переезжали в игру
            // одним пакетом в конце. Это держало на диске вторую копию всего, что
            // обновление заменяет, — на сборке, которая меняется целиком, выходило
            // двойное место. Теперь каждый файл встаёт на своё место сразу после сверки
            // хеша, а брошенный staging от прерванных обновлений только занимает диск.
            var legacyStaging = Path.Combine(plan.LocalRoot, ".staging");
            if (Directory.Exists(legacyStaging)) {
                ChillHub.Core.Logging.Logger.Info($"Убираем staging от прежней схемы: {legacyStaging}");
                TryDeleteDirectoryWithRetry(legacyStaging, recursive: true, attempts: 3, delayMs: 150);
            }

            var degree = Math.Clamp(ConfigService.Current.DownloadThreads, 2, 16);

            // Общий на все параллельные загрузки лимитер: один и тот же экземпляр
            // делят все потоки скачивания, поэтому ограничивается суммарная скорость,
            // а не скорость каждого потока по отдельности. null — лимита нет.
            var speedLimiter = SpeedLimiter.Create(ConfigService.Current.SpeedLimitMbps);

            // Проверка свободного места (без запаса) на КАЖДОМ задействованном диске.
            // Скачиваем в LocalRoot, а применяем в ApplyRoot — при самообновлении это
            // разные тома (%TEMP% и каталог установки), и проверка только по одному
            // пропускала случай «в TEMP место есть, а на системном диске нет».
            var required = RequiredFreeBytes(plan, degree);
            if (required > 0) {
                foreach (var checkedRoot in EnumerateDistinctDrives(plan.LocalRoot, plan.ApplyRoot)) {
                    var drive = new DriveInfo(checkedRoot);
                    if (drive.AvailableFreeSpace < required) {
                        throw new IOException(
                            $"Недостаточно свободного места на диске {checkedRoot}. " +
                            $"Требуется {required} байт, доступно {drive.AvailableFreeSpace} байт.");
                    }
                }
            }

            progress.Report(new SyncProgress { Stage = "Checking", BytesDownloaded = 0, TotalBytes = total, FilesDownloaded = 0, TotalFiles = totalFiles });

            // Маркер ставится ДО первой записи в папку игры, а не перед активацией.
            // Раньше он и не был нужен раньше: до самого конца загрузки папку игры никто
            // не трогал. Теперь файлы встают на место по мере готовности, и с первого же
            // из них сборка смешанная — прерви обновление, и запускать её нельзя. Пока
            // маркер на месте, лаунчер показывает «требуется обновление», а не «играть».
            var changesDisk = plan.Downloads.Count > 0 || plan.ToDelete.Count > 0;
            if (changesDisk) {
                WriteUpdateMarker(plan.LocalRoot, plan.Version);
            }

            // Файлы, которые физически заменятся только после перезагрузки. Собирается из
            // потоков загрузки, поэтому потокобезопасный: пока список не пуст, обновление
            // НЕ завершено, сколько бы удачно ни прошло остальное.
            var deferred = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Файлы, вставшие на место, вместе со сверенными хешами: из них пополняется
            // кеш хешей, чтобы следующая проверка не перечитывала только что скачанное.
            var applied = new System.Collections.Concurrent.ConcurrentBag<(FileTask Task, string Path)>();

            // Пустые директории будем создавать в самом конце (после очистки),
            // чтобы их не удалить во время Cleanup

            // Скачивание недостающих/изменённых (многопоточно)
            progress.Report(new SyncProgress { Stage = "Downloading", BytesDownloaded = 0, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });

            // Отчёт о прогрессе шёл на каждые 256 КБ в каждом потоке: при восьми потоках это
            // сотни обращений к диспетчеру в секунду, и UI занимался только перерисовкой
            // строки скорости. Глазу хватает десяти обновлений в секунду.
            var lastReportTicks = 0L;
            void ReportDownloadProgress() {
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref lastReportTicks);
                if (now - last < ProgressThrottleMs) {
                    return;
                }

                // Отчёт делает тот поток, который выиграл обмен: остальные просто пропускают такт
                if (Interlocked.CompareExchange(ref lastReportTicks, now, last) != last) {
                    return;
                }

                progress.Report(new SyncProgress {
                    Stage = "Downloading",
                    BytesDownloaded = Interlocked.Read(ref downloaded),
                    TotalBytes = total,
                    FilesDownloaded = Volatile.Read(ref filesDone),
                    TotalFiles = totalFiles,
                });
            }

            using (var sem = new SemaphoreSlim(degree)) {
                var tasks = new List<Task>();
                try {
                    foreach (var t in plan.Downloads) {
                        await sem.WaitAsync(ct).ConfigureAwait(false);
                        tasks.Add(Task.Run(
                            async () => {
                                try {
                                    ct.ThrowIfCancellationRequested();

                                    // Целевое имя — сразу конечное, без промежуточного каталога.
                                    // Старый файл при этом остаётся нетронутым до последнего
                                    // момента: рядом с ним копится ".part", и подменяется он одним
                                    // переименованием, когда содержимое уже сверено с манифестом.
                                    var dstPath = ManifestPath.Combine(plan.LocalRoot, t.RelativePath);
                                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);

                                    var partPath = dstPath + ".part";

                                    // Такой же файл уже лежит в другой копии этой игры:
                                    // копия с диска вместо загрузки по сети. Сверку он
                                    // проходит ту же самую, а не прошедший её просто
                                    // качается дальше обычным путём.
                                    var reused = TryCopyFromDisk(t, partPath);
                                    if (reused) {
                                        Interlocked.Add(ref downloaded, t.Size);
                                        ReportDownloadProgress();
                                    }

                                    // Скачивание в .part. Уцелевший от прерванной попытки
                                    // докачивается по Range — это и есть возобновление.
                                    if (!reused) {
                                        long existing = 0;
                                        if (File.Exists(partPath)) {
                                            try {
                                                existing = new FileInfo(partPath).Length;
                                            }
                                            catch {
                                            }
                                        }

                                        var attempt = 0;
                                        var maxAttempts = 3;

                                        // Буфер БЕРЁТСЯ ИЗ ПУЛА, а не заводится на каждый файл.
                                        // 256 КиБ живут в большой куче объектов (всё, что крупнее
                                        // 85 КиБ), а сборка игры — это тысячи файлов: на каждый
                                        // приходилась своя такая аллокация, и куча дробилась там,
                                        // где хватает одного буфера на поток загрузки.
                                        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferBytes);
                                        try {
                                            while (true) {
                                                ct.ThrowIfCancellationRequested();

                                                // Дедлайн на попытку — по ПРОСТОЮ, а не по общему времени: таймер
                                                // переводится после каждой порции данных, поэтому длинная честная
                                                // загрузка доживает до конца, а зависшее соединение обрывается.
                                                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                                stallCts.CancelAfter(StallTimeoutMs);
                                                var attemptCt = stallCts.Token;
                                                try {
                                                    using var req = new HttpRequestMessage(HttpMethod.Get, t.Url);
                                                    if (existing > 0 && existing < t.Size) {
                                                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                                                    }

                                                    using var resp = await this.downloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attemptCt).ConfigureAwait(false);
                                                    resp.EnsureSuccessStatusCode();

                                                    // Если сервер вернул 200 OK, несмотря на Range — перезаписываем файл заново
                                                    if (existing > 0 && resp.StatusCode == HttpStatusCode.OK) {
                                                        existing = 0;
                                                        try {
                                                            File.Delete(partPath);
                                                        }
                                                        catch {
                                                        }
                                                    }

                                                    // Блок обязателен: поток записи должен быть ЗАКРЫТ до проверки.
                                                    // `using var` живёт до конца try, а файл открыт с FileShare.None —
                                                    // то есть любой другой доступ к нему запрещён. Проверка,
                                                    // вызванная при живом дескрипторе, падала с «файл занят другим
                                                    // процессом», хотя процесс был наш собственный. Три повтора
                                                    // упирались в то же самое, и обновление обрывалось.
                                                    using (var src = await resp.Content.ReadAsStreamAsync(attemptCt).ConfigureAwait(false))
                                                    using (var dst = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true)) {
                                                        int read;
                                                        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), attemptCt).ConfigureAwait(false)) > 0) {
                                                            await dst.WriteAsync(buffer.AsMemory(0, read), attemptCt).ConfigureAwait(false);
                                                            Interlocked.Add(ref downloaded, read);

                                                            // Ограничение скорости: список токенов общий на все потоки загрузки,
                                                            // поэтому ждём здесь, а не после записи — иначе сверхлимитные байты
                                                            // уже осели бы на диске до того, как поток притормозил.
                                                            if (speedLimiter != null) {
                                                                await speedLimiter.ThrottleAsync(read, attemptCt).ConfigureAwait(false);
                                                            }

                                                            // Данные пришли — отодвигаем дедлайн простоя
                                                            stallCts.CancelAfter(StallTimeoutMs);
                                                            ReportDownloadProgress();
                                                        }
                                                    }

                                                    // Проверка хешей — ВНУТРИ цикла ретраев. Раньше она стояла
                                                    // за ним, и «протухший» .part (докачанный поверх обрывка от
                                                    // другой версии) валил всё обновление целиком, хотя лечится
                                                    // одной перезакачкой с нуля.
                                                    VerifyDownloadedFile(partPath, t);

                                                    break; // success
                                                }
                                                catch (Exception ex) {
                                                    // Отмену пользователя не превращаем в «ошибку загрузки» и не тратим
                                                    // на неё попытки: с появлением связанного CTS обрыв по простою и
                                                    // настоящая отмена приходят одним и тем же типом исключения.
                                                    ct.ThrowIfCancellationRequested();

                                                    attempt++;
                                                    if (attempt >= maxAttempts) {
                                                        throw ex is InvalidDataException
                                                            ? new InvalidDataException($"Файл {t.RelativePath} не прошёл проверку хеша после {maxAttempts} попыток", ex)
                                                            : new IOException($"Ошибка загрузки {t.RelativePath}: {ex.Message}", ex);
                                                    }

                                                    if (ex is InvalidDataException) {
                                                        // Докачивать битый файл бессмысленно: начинаем с нуля
                                                        SafeDeleteFile(partPath);
                                                        existing = 0;
                                                        ChillHub.Core.Logging.Logger.Warn($"Скачанный файл '{t.RelativePath}' не прошёл проверку хеша, качаем заново (попытка {attempt + 1} из {maxAttempts})");
                                                    }

                                                    var delayMs = (int)Math.Min(5000, 500 * Math.Pow(2, attempt - 1));
                                                    await Task.Delay(delayMs, ct).ConfigureAwait(false);

                                                    // обновить existing на случай частичного дозаписи
                                                    try {
                                                        existing = new FileInfo(partPath).Length;
                                                    }
                                                    catch {
                                                    }
                                                }
                                            }
                                        }
                                        finally {
                                            ArrayPool<byte>.Shared.Return(buffer);
                                        }
                                    }

                                    // Содержимое сверено — ставим файл на место. С этого
                                    // момента сборка на диске смешанная, о чём и говорит маркер.
                                    if (ApplyDownloadedFile(partPath, dstPath, t.RelativePath)) {
                                        // Хеши этого файла только что сверены — грех не
                                        // запомнить: иначе первая же проверка после
                                        // установки перечитает с диска всё, что скачано.
                                        applied.Add((t, dstPath));
                                    }
                                    else {
                                        deferred.Add(t.RelativePath);
                                    }
                                }
                                finally {
                                    Interlocked.Increment(ref filesDone);
                                    ReportDownloadProgress();
                                    sem.Release();
                                }
                            }, ct));
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch {
                    // Из using нельзя выходить, пока живы задачи: каждая делает sem.Release()
                    // в finally, а семафор к тому моменту был бы уже уничтожен. Отмена
                    // прилетала прямо из sem.WaitAsync(ct), Task.WhenAll пропускался — и
                    // после «Отменено» скачивание продолжалось в фоне, роняя ObjectDisposedException.
                    try {
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    catch (Exception drainEx) {
                        // Причина остановки уже известна из внешнего исключения
                        ChillHub.Core.Logging.Logger.Info($"Загрузка остановлена: {drainEx.Message}");
                    }

                    throw;
                }
            }

            RememberHashes(plan, applied);

            // Итоговые цифры скачивания — уже без троттлинга, иначе счётчик файлов
            // может замереть на предпоследнем значении
            progress.Report(new SyncProgress { Stage = "Downloading", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });

            // Верификация (хеши пропустим на моках)
            progress.Report(new SyncProgress { Stage = "Verifying", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });

            // Завершение: убрать лишние файлы, опустевшие каталоги и снять маркер. Сами
            // файлы игры уже на своих местах — их поставили потоки загрузки. Фаза синхронная
            // и блокирующая (SafeDeleteFile с ожиданиями, обход дерева каталогов), а
            // вызывающие стартуют ExecuteAsync с UI-потока — уводим её в пул, иначе окно
            // замирает и «Отмена» физически не нажимается.
            progress.Report(new SyncProgress { Stage = "Activating", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
            await Task.Run(() => FinishPlan(plan, deferred, changesDisk, ct), ct).ConfigureAwait(false);

            // Финальный сигнал о завершении
            progress.Report(new SyncProgress { Stage = "Completed", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
        }

        /// <summary>
        /// Сколько места на диске обновлению нужно на самом деле.
        /// <para>
        /// Пока файлы качались в staging, ответом был весь объём загрузки: вторая копия
        /// лежала на диске целиком, рядом со старой сборкой. Теперь файл качается в
        /// «.part» рядом с целью и подменяет её сразу после сверки, поэтому старые байты
        /// освобождаются по ходу дела. Нужно ровно два слагаемых: прирост занятого места
        /// и запас на файлы, которые лежат недокачанными ОДНОВРЕМЕННО.
        /// </para>
        /// <para>
        /// Требовать по-прежнему весь объём загрузки — значит отказывать в обновлении,
        /// которое спокойно поместилось бы: сборка, где меняется почти всё, просила бы
        /// свободным второй свой размер, хотя реально ей нужно место под несколько
        /// файлов в работе.
        /// </para>
        /// </summary>
        /// <param name="plan">План различий.</param>
        /// <param name="degree">Сколько файлов качается одновременно.</param>
        /// <returns>Требуемое число свободных байт.</returns>
        internal static long RequiredFreeBytes(DiffPlan plan, int degree) {
            if (plan.TotalDownloadBytes <= 0) {
                return 0;
            }

            // Прирост может быть и отрицательным (новая сборка легче старой) — тогда
            // по этой части требований нет.
            var growth = Math.Max(0, plan.TotalDownloadBytes - plan.ReplacedBytes);

            // Запас: самые тяжёлые файлы, которые могут оказаться в работе разом. Каждый
            // из них какое-то время лежит на диске дважды — старой версией и своим ".part".
            var inFlight = plan.Downloads
                .Select(d => d.Size)
                .OrderByDescending(s => s)
                .Take(Math.Max(1, degree))
                .Sum();

            return growth + inFlight;
        }

        /// <summary>
        /// Берёт файл из другой копии игры вместо загрузки по сети.
        /// <para>
        /// Копия кладётся в тот же «.part» и проходит ту же сверку хешей, что и
        /// скачанное. Не сошлось или не скопировалось — молча возвращаемся к загрузке:
        /// донор это удобство, а не источник истины.
        /// </para>
        /// </summary>
        /// <param name="t">Задача загрузки.</param>
        /// <param name="partPath">Куда положить содержимое.</param>
        /// <returns>true, если файл готов к постановке на место.</returns>
        private static bool TryCopyFromDisk(FileTask t, string partPath) {
            if (string.IsNullOrEmpty(t.LocalSource)) {
                return false;
            }

            try {
                SafeDeleteFile(partPath);
                File.Copy(t.LocalSource, partPath, overwrite: true);
                VerifyDownloadedFile(partPath, t);
                ChillHub.Core.Logging.Logger.Info($"Файл '{t.RelativePath}' взят с диска: '{t.LocalSource}'");
                return true;
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn(
                    $"Не вышло взять '{t.RelativePath}' из '{t.LocalSource}' ({ex.Message}) — качаем из сети");
                SafeDeleteFile(partPath);
                return false;
            }
        }

        /// <summary>
        /// Складывает хеши только что поставленных файлов в кеш этой папки.
        /// <para>
        /// Хеши уже сверены при загрузке, а кеш о них не знал: он пополнялся только на
        /// этапе плана, при ЧТЕНИИ файлов с диска. Из-за этого первая же проверка после
        /// установки перечитывала гигабайты, содержимое которых лаунчер только что
        /// посчитал сам.
        /// </para>
        /// </summary>
        /// <param name="plan">План, который применяли.</param>
        /// <param name="applied">Файлы, вставшие на место, и их пути.</param>
        private static void RememberHashes(
            DiffPlan plan, System.Collections.Concurrent.ConcurrentBag<(FileTask Task, string Path)> applied) {
            if (applied.IsEmpty || string.IsNullOrWhiteSpace(plan.GameId)) {
                return;
            }

            try {
                var cache = FileHashCache.Load(plan.GameId, plan.LocalRoot);
                var stored = 0;
                foreach (var (task, path) in applied) {
                    if (string.IsNullOrWhiteSpace(task.Sha256) || string.IsNullOrWhiteSpace(task.Blake3)) {
                        // Неполные хеши кеш не принимает: TryGet требует оба, и запись
                        // без одного из них была бы вечным промахом.
                        continue;
                    }

                    var info = new FileInfo(path);
                    if (!info.Exists) {
                        continue;
                    }

                    cache.Set(task.RelativePath, info.Length, info.LastWriteTimeUtc.Ticks, task.Sha256!, task.Blake3);
                    stored++;
                }

                if (stored > 0) {
                    // Без прополки: живые файлы этой папки уже посчитаны планом, а
                    // выбрасывать по неполному списку значит выбросить лишнее.
                    cache.SaveOnly();
                    ChillHub.Core.Logging.Logger.Info($"Кеш хешей пополнен: {stored} файл(ов) в '{plan.LocalRoot}'");
                }
            }
            catch (Exception ex) {
                // Кеш — ускорение, а не условие работы: его потеря стоит одного
                // пересчёта, а не сорванного обновления.
                ChillHub.Core.Logging.Logger.Warn($"RememberHashes: {ex.Message}");
            }
        }

        /// <summary>
        /// Ставит скачанный и сверенный файл на его место в игре.
        /// <para>
        /// Быстрый путь — одно переименование с заменой. Медленная ветка нужна только для
        /// файлов, которые кто-то держит (запущенная игра, античит, антивирус): старый
        /// отводится в сторону, новый кладётся рядом как «.new», а замена планируется на
        /// перезагрузку. Пока такие файлы есть, обновление не завершено.
        /// </para>
        /// </summary>
        /// <param name="partPath">Скачанный файл (.part).</param>
        /// <param name="dstPath">Куда он должен встать.</param>
        /// <param name="rel">Относительный путь — для сообщений в лог.</param>
        /// <returns>true, если файл встал на место; false — если замена отложена до перезагрузки.</returns>
        private static bool ApplyDownloadedFile(string partPath, string dstPath, string rel) {
            try {
                File.Move(partPath, dstPath, overwrite: true);
                return true;
            }
            catch (IOException) {
            }
            catch (UnauthorizedAccessException) {
            }

            if (File.Exists(dstPath)) {
                SafeDeleteFile(dstPath);
            }

            if (!File.Exists(dstPath)) {
                File.Move(partPath, dstPath, overwrite: true);
                return true;
            }

            // Удалить не вышло — пробуем отвести старый файл в сторону
            var backup = dstPath + $".old.{Environment.ProcessId}";
            try {
                File.Move(dstPath, backup, overwrite: true);
            }
            catch {
            }

            if (!File.Exists(dstPath)) {
                File.Move(partPath, dstPath, overwrite: true);
                return true;
            }

            // Последнее средство: кладём новый файл рядом и планируем замену на перезагрузку
            var pending = dstPath + ".new";
            try {
                if (File.Exists(pending)) {
                    SafeDeleteFile(pending);
                }
            }
            catch {
            }

            File.Move(partPath, pending, overwrite: true);

            // REPLACE_EXISTING обязателен: сюда мы попадаем ровно тогда, когда dstPath
            // СУЩЕСТВУЕТ и не удаляется. Без флага MoveFileEx на существующем целевом
            // файле возвращает ошибку — то есть резервный путь, ради которого всё это
            // написано, не срабатывал никогда: .new оставался мусором, старый файл на
            // месте, хеш не сходился, и «требуется обновление» повторялось бесконечно,
            // накапливая по .new за попытку.
            try {
                var scheduled = NativeMethods.MoveFileEx(
                    pending,
                    dstPath,
                    NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT | NativeMethods.MOVEFILE_REPLACE_EXISTING);
                if (scheduled) {
                    ChillHub.Core.Logging.Logger.Info(
                        $"Файл '{rel}' занят другим процессом: замена запланирована на перезагрузку.");
                }
                else {
                    ChillHub.Core.Logging.Logger.Warn(
                        $"Не удалось запланировать замену '{rel}' на перезагрузку (код {Marshal.GetLastWin32Error()}).");
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"MoveFileEx('{rel}'): {ex.Message}");
            }

            // И при успешном планировании, и при отказе MoveFileEx на диске сейчас
            // лежит СТАРОЕ содержимое: игра обновлена не полностью.
            return false;
        }

        /// <summary>
        /// Сверяет скачанный .part с хешами из манифеста (SHA-256 и Blake3 за один проход).
        /// Файл не удаляет: решение о повторной попытке принимает цикл ретраев.
        /// </summary>
        /// <param name="partPath">Путь к скачанному файлу.</param>
        /// <param name="t">Задание из плана с ожидаемыми хешами.</param>
        /// <exception cref="InvalidDataException">Содержимое не совпало с манифестом.</exception>
        private static void VerifyDownloadedFile(string partPath, FileTask t) {
            if (string.IsNullOrWhiteSpace(t.Sha256) && string.IsNullOrWhiteSpace(t.Blake3)) {
                return;
            }

            // ComputeHashes закрывает файл до выхода: иначе повторная попытка не смогла бы его удалить.
            FileHasher.ComputeHashes(partPath, out var shaHex, out var b3Hex);

            // Файл закрыт: иначе повторная попытка не смогла бы его удалить
            if (!string.IsNullOrWhiteSpace(t.Sha256) && !string.Equals(shaHex, t.Sha256, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException($"Хеш SHA-256 не совпадает: {t.RelativePath}");
            }

            if (!string.IsNullOrWhiteSpace(t.Blake3) && !string.Equals(b3Hex, t.Blake3, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException($"Хеш Blake3 не совпадает: {t.RelativePath}");
            }
        }

        /// <summary>
        /// Завершение обновления: удаление лишних файлов, очистка опустевших каталогов,
        /// создание пустых каталогов из манифеста и снятие маркера. Сами файлы игры к
        /// этому моменту уже на своих местах — их поставили потоки загрузки.
        /// Синхронная и блокирующая — вызывать только из пула потоков.
        /// </summary>
        /// <param name="plan">План различий.</param>
        /// <param name="deferred">Файлы, замена которых отложена до перезагрузки.</param>
        /// <param name="changesDisk">Ставился ли маркер незавершённого обновления.</param>
        /// <param name="ct">Токен отмены.</param>
        internal static void FinishPlan(
            DiffPlan plan,
            System.Collections.Concurrent.ConcurrentBag<string> deferred,
            bool changesDisk,
            CancellationToken ct) {
            ChillHub.Core.Logging.Logger.Info(
                $"Applied gid={plan.GameId} ver={plan.Version} files={plan.Downloads.Count} deferred={deferred.Count} toDelete={plan.ToDelete.Count}");

            // Пути чужого манифеста (для синхронизации игры — файлы установленного
            // модпака). Планировщик их в ToDelete не кладёт, но список сюда мог приехать
            // и не от него: FinishPlan статичен, план — обычный объект с полями.
            var foreignPaths = NormalizeRelSet(plan.ForeignPaths);

            // Удаление лишних файлов (с устойчивостью к блокировкам сторонними процессами)
            var deletedRel = new List<string>();
            foreach (var rel in plan.ToDelete) {
                // FreeTP/.hash не удаляем НИКОГДА — вторая проверка поверх планировщика.
                // Построитель плана его уже отфильтровал, но удаление необратимо, а
                // цена ошибки здесь особая: без этого файла пиратская сборка открывает
                // сайт FreeTP.Org при каждом запуске игры. Один if дешевле, чем
                // надеяться, что список к нам приехал именно от PlanAsync.
                if (IsIgnoredRelFile(rel)) {
                    continue;
                }

                // Служебные файлы лаунчера — по той же логике второго рубежа. Стереть
                // `.mods.version` или `.mods.manifest.json` значит потерять память о том,
                // какой модпак стоит и какими файлами он владеет: следующая синхронизация
                // игры сочтёт все моды лишними и вынесет их.
                if (IsServiceRelFile(rel)) {
                    continue;
                }

                // Файл чужого манифеста в общем корне. Для сборки игры это моды: они
                // лежат в её папке, но принадлежат не ей.
                if (foreignPaths.Contains(NormalizeRel(rel))) {
                    ChillHub.Core.Logging.Logger.Warn(
                        $"FinishPlan gid={plan.GameId}: '{rel}' принадлежит другому манифесту в том же корне, не удаляем");
                    continue;
                }

                try {
                    // Список сформирован обходом самой папки игры, но удаление — необратимо,
                    // поэтому проверяем и его: подмена DiffPlan не должна стирать чужие файлы.
                    //
                    // Combine СТОИТ ВНУТРИ try осознанно. Он бросает ManifestPathException на
                    // любом пути, который не проходит проверку, а в папке игры такой путь
                    // заводится без всякой подмены: файл с именем устройства (CON.txt), имя
                    // с краевым пробелом или точкой, путь длиннее 1024 символов — всё это
                    // NTFS позволяет создать, и обход папки честно вернёт их в ToDelete.
                    // Пока исключение улетало наружу, один такой файл ронял ВСЮ фазу
                    // завершения: остальные удаления не выполнялись, пустые каталоги из
                    // манифеста не создавались, а маркер незавершённого обновления не
                    // снимался — игра навсегда оставалась «обновление прервано» и чинилась
                    // только удалением вручную. Пропустить одну запись несравнимо дешевле.
                    var path = ManifestPath.Combine(plan.LocalRoot, rel);
                    if (File.Exists(path)) {
                        SafeDeleteFile(path);
                        if (!File.Exists(path)) {
                            deletedRel.Add(rel);
                        }
                    }
                }
                catch (Exception ex) {
                    ChillHub.Core.Logging.Logger.Warn($"FinishPlan gid={plan.GameId}: '{rel}' не удалён: {ex.Message}");
                }
            }

            // Убираем каталоги, опустевшие ИМЕННО из-за нашего удаления. Раньше здесь шёл
            // обход всего дерева игры, и под нож попадали пустые папки, созданные самой
            // игрой (Saves, Config, логи) — игра теряла их при каждом обновлении.
            var keep = new HashSet<string>(plan.EmptyDirsToCreate.Select(s => s.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
            CleanupDirsEmptiedByUpdate(plan.LocalRoot, deletedRel, keep);

            // Создаём пустые директории из манифеста (гарантированно после очистки)
            foreach (var dirRel in plan.EmptyDirsToCreate) {
                ct.ThrowIfCancellationRequested();
                var dirPath = ManifestPath.Combine(plan.LocalRoot, dirRel);
                Directory.CreateDirectory(dirPath);
            }

            if (!changesDisk) {
                // Ничего не меняли — и маркера не ставили, снимать нечего.
                return;
            }

            // Маркер снимаем ТОЛЬКО если обновление реально доведено до конца. Если хотя бы
            // один файл был занят и заменится лишь после перезагрузки, снятие маркера
            // означало бы «игра обновлена», хотя на диске у неё старый исполняемый файл:
            // запуск такой сборки — это как раз то, от чего маркер и защищает.
            var pending = deferred.ToArray();
            if (pending.Length > 0) {
                WriteRebootPendingMarker(plan.LocalRoot, plan.Version, pending);
                ChillHub.Core.Logging.Logger.Warn(
                    $"Обновление до {plan.Version} применено не полностью: {pending.Length} файл(ов) заменятся после перезагрузки ({string.Join(", ", pending.Take(5))}).");
                return;
            }

            ClearUpdateMarker(plan.LocalRoot);
        }


        /// <summary>
        /// Корни томов для переданных путей, без повторов и без пустых значений.
        /// Если оба пути лежат на одном диске (обычный случай для игр), вернётся один корень.
        /// </summary>
        /// <param name="paths">Проверяемые каталоги.</param>
        /// <returns>Уникальные корни дисков.</returns>
        private static IEnumerable<string> EnumerateDistinctDrives(params string[] paths) {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths) {
                if (string.IsNullOrWhiteSpace(p)) {
                    continue;
                }

                string? root;
                try {
                    root = Path.GetPathRoot(Path.GetFullPath(p));
                }
                catch (Exception ex) {
                    // Кривой путь не должен ронять обновление: пропустим эту проверку
                    ChillHub.Core.Logging.Logger.Warn($"EnumerateDistinctDrives('{p}'): {ex.Message}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(root) && seen.Add(root)) {
                    yield return root;
                }
            }
        }

        private static string CombineUrl(string baseUrl, string relativePath) {
            baseUrl = baseUrl.TrimEnd('/') + "/";
            relativePath = relativePath.Replace("\\", "/");
            return baseUrl + relativePath;
        }

        private static List<string> ListLocalFiles(string root) => ListLocalFiles(root, null, null);

        /// <summary>
        /// Файлы в корне, которые эта синхронизация считает своими.
        /// </summary>
        /// <param name="root">Корень локальной папки игры.</param>
        /// <param name="foreignPaths">
        /// Пути чужого манифеста в этом же корне: их сюда не кладём — значит, они
        /// не попадут ни в список на удаление, ни в счётчик лишних файлов.
        /// </param>
        /// <param name="foreignFound">
        /// Куда сложить найденные на диске ЧУЖИЕ файлы. Нужны ровно для кеша хешей:
        /// он лежит один на корень, и если объявить чужие файлы исчезнувшими, прополка
        /// выбросит их записи, а соседняя синхронизация пересчитает их с диска заново.
        /// </param>
        /// <returns>Свои файлы относительно корня, разделитель '/'.</returns>
        private static List<string> ListLocalFiles(string root, HashSet<string>? foreignPaths, List<string>? foreignFound) {
            var list = new List<string>();
            if (!Directory.Exists(root)) {
                return list;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.StartsWith(".staging/", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (IsServiceRelFile(rel)) {
                    // Служебный маркер незавершённого обновления: не качаем, не удаляем, не считаем файлом игры
                    continue;
                }

                if (IsIgnoredRelFile(rel)) {
                    continue; // не учитываем FreeTP/.hash
                }

                if (foreignPaths != null && foreignPaths.Contains(rel)) {
                    // Файл принадлежит чужому манифесту в том же корне (моды рядом с игрой):
                    // для нас его как будто нет вовсе.
                    foreignFound?.Add(rel);
                    continue;
                }

                list.Add(rel);
            }

            return list;
        }

        /// <summary>
        /// Приводит относительный путь к той же форме, в которой лежат ключи манифеста:
        /// прямые слеши, без ведущего разделителя и краевых пробелов.
        /// Формы «BepInEx/core/x.dll», «BepInEx\core\x.dll» и «/BepInEx/core/x.dll» —
        /// это один и тот же файл, и списки владения обязаны сходиться на всех трёх.
        /// </summary>
        /// <param name="rel">Относительный путь в любой из форм.</param>
        /// <returns>Канонический относительный путь.</returns>
        internal static string NormalizeRel(string? rel) => ManifestPath.Canonicalize(rel);

        /// <summary>
        /// Собирает набор относительных путей для быстрой проверки принадлежности:
        /// регистронезависимый (как файловая система Windows) и канонизированный.
        /// </summary>
        /// <param name="paths">Исходные пути; null и пустые записи пропускаются.</param>
        /// <returns>Набор канонических путей.</returns>
        internal static HashSet<string> NormalizeRelSet(IEnumerable<string>? paths) {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (paths == null) {
                return set;
            }

            foreach (var p in paths) {
                if (string.IsNullOrWhiteSpace(p)) {
                    continue;
                }

                var norm = NormalizeRel(p);
                if (norm.Length > 0) {
                    set.Add(norm);
                }
            }

            return set;
        }

        // ВАЖНО: игнорируем специальный файл FreeTP/.hash для всех игр.
        // Это сделано для пиратских сборок с сайта FreeTP.Org, чтобы при запуске игр
        // не открывался сайт FreeTP при каждом запуске. Лаунчер не должен проверять,
        // скачивать или удалять этот файл.
        internal static bool IsIgnoredRelFile(string rel) {
            if (string.IsNullOrWhiteSpace(rel)) {
                return false;
            }

            var r = rel.Replace('\\', '/').TrimStart('/');
            return r.Equals("freetp/.hash", StringComparison.OrdinalIgnoreCase);
        }

        // ВАЖНО: сохраняем директорию FreeTP внутри игры и не удаляем её при очистке пустых папок.
        // Это также связано с пиратскими сборками с FreeTP.Org — пустая папка может понадобиться,
        // а её удаление может спровоцировать нежелательное поведение (например, открытие сайта).
        internal static bool IsIgnoredRelDir(string relDir) {
            if (string.IsNullOrWhiteSpace(relDir)) {
                return false;
            }

            var r = relDir.Replace('\\', '/').Trim('/');

            // Совпадение папки "FreeTP" в корне игры
            return r.Equals("freetp", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Удаляет каталоги, опустевшие из-за удаления перечисленных файлов, поднимаясь
        /// от каждого такого файла к корню игры. Каталоги, которых мы не касались (в том
        /// числе пустые папки, созданные самой игрой), не трогаем.
        /// </summary>
        /// <param name="root">Корень игры.</param>
        /// <param name="deletedRel">Относительные пути файлов, которые мы удалили.</param>
        /// <param name="keep">Каталоги из манифеста, которые должны остаться.</param>
        internal static void CleanupDirsEmptiedByUpdate(string root, IEnumerable<string> deletedRel, HashSet<string> keep) {
            if (!Directory.Exists(root)) {
                return;
            }

            string rootFull;
            try {
                rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"CleanupDirsEmptiedByUpdate('{root}'): {ex.Message}");
                return;
            }

            foreach (var rel in deletedRel) {
                var dir = Path.GetDirectoryName(ManifestPath.Combine(root, rel));
                while (!string.IsNullOrEmpty(dir)) {
                    string full;
                    try {
                        full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                    }
                    catch {
                        break;
                    }

                    // До корня игры и не выше: снаружи хозяйничать нельзя
                    if (string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
                        || !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                        break;
                    }

                    var relDir = Path.GetRelativePath(rootFull, full).TrimEnd(Path.DirectorySeparatorChar);

                    // Папку FreeTP сохраняем даже пустой: см. IsIgnoredRelDir
                    if (IsIgnoredRelDir(relDir) || keep.Contains(relDir)) {
                        break;
                    }

                    try {
                        if (Directory.Exists(full)) {
                            if (Directory.EnumerateFileSystemEntries(full).Any()) {
                                break; // в каталоге ещё что-то есть — выше подниматься незачем
                            }

                            Directory.Delete(full, false);
                        }
                    }
                    catch {
                        break;
                    }

                    dir = Path.GetDirectoryName(full);
                }
            }
        }

        private static class NativeMethods {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
            internal const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
            internal const int MOVEFILE_REPLACE_EXISTING = 0x00000001;
        }

        // Служебные файлы лаунчера в корне игры, которые не участвуют
        // ни в плане загрузки, ни в удалении, ни в подсчёте локальных файлов.
        internal static bool IsServiceRelFile(string rel) {
            if (string.IsNullOrWhiteSpace(rel)) {
                return false;
            }

            var r = rel.Replace('\\', '/').TrimStart('/');

            // .updating — маркер незавершённого обновления.
            // .version — маркер установленной версии. Его тоже нельзя трогать: в манифесте
            // его нет, поэтому без этой проверки он попадал в ToDelete и стирался при
            // синхронизации. При обычной установке это маскировалось — сразу после
            // ExecuteAsync вызывается WriteLocalVersion. Но «проверка целостности» из
            // настроек делает ExecuteAsync БЕЗ последующей записи маркера, и после
            // успешного ремонта игра показывалась как неустановленная.
            //
            // .mods.version и .mods.manifest.json — то же самое для модпака, который
            // живёт в ТОМ ЖЕ корне. Их в корне игры не ждёт ни один из двух манифестов,
            // поэтому без этой проверки каждая из двух синхронизаций записала бы их себе
            // в ToDelete. Потеря `.mods.manifest.json` особенно дорога: из него берётся
            // список файлов модпака, и без него синхронизация игры перестаёт понимать,
            // что моды — не мусор.
            return r.Equals(UpdateMarkerFileName, StringComparison.OrdinalIgnoreCase)
                || r.Equals(IntegrityChecker.VersionMarkerFileName, StringComparison.OrdinalIgnoreCase)
                || r.Equals(IntegrityChecker.ModsVersionMarkerFileName, StringComparison.OrdinalIgnoreCase)
                || r.Equals(IntegrityChecker.ModsManifestFileName, StringComparison.OrdinalIgnoreCase);
        }

        // Ставит маркер незавершённого обновления перед фазой активации.
        internal static void WriteUpdateMarker(string localRoot, string version) {
            try {
                Directory.CreateDirectory(localRoot);
                var path = Path.Combine(localRoot, UpdateMarkerFileName);
                var text = $"version={version}\r\nstartedUtc={DateTime.UtcNow:o}\r\npid={Environment.ProcessId}\r\n";
                File.WriteAllText(path, text);
                ChillHub.Core.Logging.Logger.Info($"UpdateMarker set root='{localRoot}' version='{version}'");
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"WriteUpdateMarker({localRoot})");
            }
        }

        // Оставляет маркер на месте, но с причиной: часть файлов заменится после перезагрузки.
        // Содержимое читает ReadUpdateMarker и показывает в подсказке/логе.
        internal static void WriteRebootPendingMarker(string localRoot, string version, IReadOnlyList<string> deferred) {
            try {
                var path = Path.Combine(localRoot, UpdateMarkerFileName);
                var lines = new List<string> {
                    $"version={version}",
                    "state=reboot-required",
                    $"updatedUtc={DateTime.UtcNow:o}",
                    $"pid={Environment.ProcessId}",
                    $"pending={deferred.Count}",
                };
                lines.AddRange(deferred.Take(20).Select(r => $"pendingFile={r}"));
                File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"WriteRebootPendingMarker({localRoot})");
            }
        }

        // Снимает маркер после успешного завершения активации.
        internal static void ClearUpdateMarker(string localRoot) {
            try {
                var path = Path.Combine(localRoot, UpdateMarkerFileName);
                if (File.Exists(path)) {
                    SafeDeleteFile(path);
                    ChillHub.Core.Logging.Logger.Info($"UpdateMarker cleared root='{localRoot}'");
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"ClearUpdateMarker({localRoot})");
            }
        }

        internal static void SafeDeleteFile(string path) {
            try {
                if (!File.Exists(path)) {
                    return;
                }

                // Remove read-only/system attributes if present
                try {
                    var attrs = File.GetAttributes(path);
                    if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                        File.SetAttributes(path, attrs & ~(FileAttributes.ReadOnly | FileAttributes.System));
                    }
                }
                catch {
                }

                int attempts = 5;
                for (int i = 0; i < attempts; i++) {
                    try {
                        File.Delete(path);
                        if (!File.Exists(path)) {
                            return;
                        }
                    }
                    catch (IOException) {
                    }
                    catch (UnauthorizedAccessException) {
                    }

                    // Один раз даём финализаторам закрыть забытые FileStream'ы. Именно
                    // один: полная блокирующая сборка стоит десятки миллисекунд и на
                    // втором заходе уже ничего не находит — файл держит чужой процесс,
                    // а не наш мусор. Повторять её на каждой из пяти попыток по каждому
                    // занятому файлу — чистая трата времени фазы активации.
                    if (i == 0) {
                        try {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                        catch {
                        }
                    }

                    Thread.Sleep(120 * (i + 1));
                }

                // Fallback: schedule delete on reboot
                try {
                    NativeMethods.MoveFileEx(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
                }
                catch {
                }
            }
            catch {
            }
        }

        private static void TryDeleteDirectoryWithRetry(string dir, bool recursive, int attempts, int delayMs) {
            for (int i = 0; i < attempts; i++) {
                try {
                    if (!Directory.Exists(dir)) {
                        return;
                    }

                    Directory.Delete(dir, recursive);
                    if (!Directory.Exists(dir)) {
                        return;
                    }
                }
                catch (IOException) {
                }
                catch (UnauthorizedAccessException) {
                }

                try {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                catch {
                }

                Thread.Sleep(delayMs * (i + 1));
            }

            // best-effort: leave as is; directory should be empty besides locked files which will remain until release
        }
    }
}
