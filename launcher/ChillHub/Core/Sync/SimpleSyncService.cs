// <copyright file="SimpleSyncService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
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

        private readonly HttpClient http;

        public SimpleSyncService(HttpClient? http = null) {
            this.http = http ?? HttpClientProvider.Shared;
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

            // Структура — раньше подписи. Опасный путь отвергается ВСЕГДА, в любом
            // режиме совместимости: подпись отвечает на вопрос «наш ли это манифест»,
            // а не «не пишет ли он файл в автозагрузку».
            ManifestValidator.Validate(manifest, manifestUrl);

            // Подпись проверяем ДО того, как что-либо качать и применять: манифест
            // задаёт список файлов и их хеши, значит именно он определяет, какие
            // исполняемые файлы окажутся на диске. Проверка здесь, в единственной
            // точке загрузки манифеста, закрывает и синхронизацию игр, и
            // самообновление лаунчера.
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

            var plan = new DiffPlan {
                GameId = manifest.GameId,
                Version = manifest.Version,
                LocalRoot = localRoot,
            };

            // Соберём множество файлов из манифеста
            var manifestFiles = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
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

                manifestFiles[relNorm] = mf;
            }

            // Локальные файлы относительно корня
            var localExisting = ListLocalFiles(localRoot);

            // Кеш хешей: при неизменных размере и времени модификации файл не перечитывается
            var hashCache = FileHashCache.Load(manifest.GameId);

            // Счётчики для отчёта о прогрессе: считаем «проверенные» файлы манифеста и их байты
            var checkedFiles = 0;
            long checkedBytes = 0;
            var totalToCheck = manifestFiles.Count;
            long totalBytesToCheck = 0;
            if (options.Progress != null) {
                foreach (var kv in manifestFiles) {
                    totalBytesToCheck += kv.Value.Size;
                }

                options.Progress.Report(new SyncProgress {
                    Stage = "Checking",
                    FilesDownloaded = 0,
                    TotalFiles = totalToCheck,
                    BytesDownloaded = 0,
                    TotalBytes = totalBytesToCheck,
                });
            }

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
                if (File.Exists(localPath)) {
                    try {
                        var info = new FileInfo(localPath);

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
                                    ComputeHashes(localPath, out shaHex, out b3Hex, ct);
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
                    plan.Downloads.Add(new FileTask {
                        RelativePath = rel,
                        Size = mf.Size,
                        Url = CombineUrl(contentBaseUrl, rel),
                        Blake3 = mf.Blake3,
                        Sha256 = mf.Sha256,
                        Executable = mf.Executable,
                    });
                    plan.TotalDownloadBytes += mf.Size;
                    try {
                        ChillHub.Core.Logging.Logger.Info($"Plan include gid={manifest.GameId} file='{rel}' size={mf.Size} reason={reason}");
                    }
                    catch {
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

            // Чистим кеш от записей об исчезнувших файлах и сохраняем, если что-то поменялось
            hashCache.PruneAndSave(localExisting);

            // Пустые директории для создания
            foreach (var d in manifest.EmptyDirs) {
                plan.EmptyDirsToCreate.Add(NormalizeRelPath(d));
            }

            // Файлы к удалению (есть локально, нет в манифесте)
            foreach (var relLocal in localExisting) {
                var norm = relLocal.Replace('\\', '/');
                if (IsIgnoredRelFile(norm)) {
                    continue; // не удаляем FreeTP/.hash
                }

                if (!manifestFiles.ContainsKey(norm)) {
                    plan.ToDelete.Add(norm);
                }
            }

            return Task.FromResult(plan);
        }

        /// <inheritdoc/>
        public async Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
            long downloaded = 0;
            int filesDone = 0;
            var total = plan.TotalDownloadBytes;
            var totalFiles = plan.TotalFilesToDownload;

            // Создать папку назначения и staging
            var stagingRoot = Path.Combine(plan.LocalRoot, ".staging");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(plan.LocalRoot);

            // Проверка свободного места (без запаса) на КАЖДОМ задействованном диске.
            // Скачиваем в LocalRoot, а применяем в ApplyRoot — при самообновлении это
            // разные тома (%TEMP% и каталог установки), и проверка только по одному
            // пропускала случай «в TEMP место есть, а на системном диске нет».
            if (total > 0) {
                foreach (var checkedRoot in EnumerateDistinctDrives(plan.LocalRoot, plan.ApplyRoot)) {
                    var drive = new DriveInfo(checkedRoot);
                    if (drive.AvailableFreeSpace < total) {
                        throw new IOException(
                            $"Недостаточно свободного места на диске {checkedRoot}. " +
                            $"Требуется {total} байт, доступно {drive.AvailableFreeSpace} байт.");
                    }
                }
            }

            progress.Report(new SyncProgress { Stage = "Checking", BytesDownloaded = 0, TotalBytes = total, FilesDownloaded = 0, TotalFiles = totalFiles });

            // Пустые директории будем создавать в самом конце (после очистки),
            // чтобы их не удалить во время Cleanup

            // Скачивание недостающих/изменённых (многопоточно)
            progress.Report(new SyncProgress { Stage = "Downloading", BytesDownloaded = 0, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
            var degree = Math.Clamp(ConfigService.Current.DownloadThreads, 2, 16);

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
                                    var stagingFile = ManifestPath.Combine(stagingRoot, t.RelativePath);
                                    var stagingDir = Path.GetDirectoryName(stagingFile)!;
                                    Directory.CreateDirectory(stagingDir);

                                    // Скачивание в .part
                                    var partPath = stagingFile + ".part";
                                    {
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
                                        var buffer = new byte[256 * 1024];
                                        while (true) {
                                            ct.ThrowIfCancellationRequested();
                                            try {
                                                using var req = new HttpRequestMessage(HttpMethod.Get, t.Url);
                                                if (existing > 0 && existing < t.Size) {
                                                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                                                }

                                                using var resp = await this.http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

                                                using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                                                using var dst = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                                                int read;
                                                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0) {
                                                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                                    Interlocked.Add(ref downloaded, read);
                                                    ReportDownloadProgress();
                                                }

                                                break; // success
                                            }
                                            catch (Exception ex) {
                                                attempt++;
                                                if (attempt >= maxAttempts) {
                                                    throw new IOException($"Ошибка загрузки {t.RelativePath}: {ex.Message}", ex);
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

                                    // Верификация хешей (SHA-256 и Blake3), если доступны — за один проход
                                    if (!string.IsNullOrWhiteSpace(t.Sha256) || !string.IsNullOrWhiteSpace(t.Blake3)) {
                                        using var f = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
                                        using var sha = SHA256.Create();
                                        var b3 = Blake3.Hasher.New();
                                        var buf = new byte[256 * 1024];
                                        int r;

                                        // NOTE: Use synchronous reads to avoid awaiting while a ref-struct (Hasher) is alive (C# 12 limitation)
                                        while ((r = f.Read(buf, 0, buf.Length)) > 0) {
                                            sha.TransformBlock(buf, 0, r, null, 0);
                                            b3.Update(new ReadOnlySpan<byte>(buf, 0, r));
                                        }

                                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                        var shaHex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                                        var b3out = new byte[32];
                                        b3.Finalize(b3out);
                                        var b3Hex = Convert.ToHexString(b3out).ToLowerInvariant();

                                        if (!string.IsNullOrWhiteSpace(t.Sha256) && !string.Equals(shaHex, t.Sha256, StringComparison.OrdinalIgnoreCase)) {
                                            File.Delete(partPath);
                                            throw new InvalidDataException($"Хеш SHA-256 не совпадает: {t.RelativePath}");
                                        }

                                        if (!string.IsNullOrWhiteSpace(t.Blake3) && !string.Equals(b3Hex, t.Blake3, StringComparison.OrdinalIgnoreCase)) {
                                            File.Delete(partPath);
                                            throw new InvalidDataException($"Хеш Blake3 не совпадает: {t.RelativePath}");
                                        }
                                    }

                                    // Переименовать .part -> готовый stagingFile
                                    if (File.Exists(stagingFile)) {
                                        File.Delete(stagingFile);
                                    }

                                    File.Move(partPath, stagingFile);
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

            // Итоговые цифры скачивания — уже без троттлинга, иначе счётчик файлов
            // может замереть на предпоследнем значении
            progress.Report(new SyncProgress { Stage = "Downloading", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });

            // Верификация (хеши пропустим на моках)
            progress.Report(new SyncProgress { Stage = "Verifying", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });

            // Активация: перенести staging файлы в основной корень.
            // Фаза целиком синхронная и тяжёлая (File.Move по каждому файлу, SafeDeleteFile
            // с ожиданиями и GC.Collect, обход дерева каталогов). Вызывающие стартуют
            // ExecuteAsync с UI-потока, поэтому уводим её в пул: иначе окно замирает
            // на десятки секунд и «Отмена» физически не нажимается.
            progress.Report(new SyncProgress { Stage = "Activating", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
            await Task.Run(() => ApplyPlan(plan, stagingRoot, ct), ct).ConfigureAwait(false);

            // Финальный сигнал о завершении
            progress.Report(new SyncProgress { Stage = "Completed", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
        }

        /// <summary>
        /// Фаза активации: перенос скачанного из staging в корень игры, удаление лишних
        /// файлов, очистка пустых каталогов и снятие маркера. Синхронная и блокирующая —
        /// вызывать только из пула потоков.
        /// </summary>
        /// <param name="plan">План различий.</param>
        /// <param name="stagingRoot">Каталог со скачанными файлами.</param>
        /// <param name="ct">Токен отмены.</param>
        private static void ApplyPlan(DiffPlan plan, string stagingRoot, CancellationToken ct) {
            WriteUpdateMarker(plan.LocalRoot, plan.Version);
            foreach (var t in plan.Downloads) {
                ct.ThrowIfCancellationRequested();
                var dstPath = ManifestPath.Combine(plan.LocalRoot, t.RelativePath);
                var srcPath = ManifestPath.Combine(stagingRoot, t.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                if (File.Exists(dstPath)) {
                    SafeDeleteFile(dstPath);
                }

                if (File.Exists(dstPath)) {
                    // Still cannot remove: try rename old away and put new file in place
                    var pid = Environment.ProcessId;
                    var backup = dstPath + $".old.{pid}";
                    try {
                        File.Move(dstPath, backup, overwrite: true);
                    }
                    catch { }

                    if (File.Exists(dstPath)) {
                        // As a last resort: place new file as .new and schedule replacement on reboot
                        var pending = dstPath + ".new";
                        try { if (File.Exists(pending)) { SafeDeleteFile(pending); } } catch { }
                        File.Move(srcPath, pending);
                        // REPLACE_EXISTING обязателен: сюда мы попадаем ровно тогда, когда
                        // dstPath СУЩЕСТВУЕТ и не удаляется (файл держит игра или античит).
                        // Без флага MoveFileEx на существующем целевом файле возвращает
                        // ошибку — то есть резервный путь, ради которого всё это написано,
                        // не срабатывал никогда: .new оставался мусором, старый файл на
                        // месте, хеш не сходился, и «требуется обновление» повторялось
                        // бесконечно, накапливая по .new за попытку.
                        try {
                            var moved = NativeMethods.MoveFileEx(
                                pending,
                                dstPath,
                                NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT | NativeMethods.MOVEFILE_REPLACE_EXISTING);
                            if (moved) {
                                ChillHub.Core.Logging.Logger.Info(
                                    $"Файл '{t.RelativePath}' занят другим процессом: замена запланирована на перезагрузку.");
                            }
                            else {
                                ChillHub.Core.Logging.Logger.Warn(
                                    $"Не удалось запланировать замену '{t.RelativePath}' на перезагрузку (код {Marshal.GetLastWin32Error()}).");
                            }
                        }
                        catch (Exception ex) {
                            ChillHub.Core.Logging.Logger.Warn($"MoveFileEx('{t.RelativePath}'): {ex.Message}");
                        }
                    }
                    else {
                        File.Move(srcPath, dstPath);
                    }
                }
                else {
                    File.Move(srcPath, dstPath);
                }
                if (t.Executable) {
                    // Для Windows можно оставить как есть; при необходимости добавить атрибуты
                }

                try {
                    long len = 0;
                    bool exists = File.Exists(dstPath);
                    if (exists) {
                        try {
                            len = new FileInfo(dstPath).Length;
                        }
                        catch {
                        }
                    }
                    ChillHub.Core.Logging.Logger.Info($"Activated file='{t.RelativePath}' exists={exists} len={len} expected={t.Size}");
                }
                catch {
                }
            }

            // Удаление лишних файлов (с устойчивостью к блокировкам сторонними процессами)
            foreach (var rel in plan.ToDelete) {
                // Список сформирован обходом самой папки игры, но удаление — необратимо,
                // поэтому проверяем и его: подмена DiffPlan не должна стирать чужие файлы.
                var path = ManifestPath.Combine(plan.LocalRoot, rel);
                try {
                    if (File.Exists(path)) {
                        SafeDeleteFile(path);
                    }
                }
                catch {
                }
            }

            // Очистка пустых папок, которых нет в манифесте
            var keep = new HashSet<string>(plan.EmptyDirsToCreate.Select(s => s.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
            CleanupEmptyDirs(plan.LocalRoot, keep);

            // Создаём пустые директории из манифеста (гарантированно после очистки)
            foreach (var dirRel in plan.EmptyDirsToCreate) {
                ct.ThrowIfCancellationRequested();
                var dirPath = ManifestPath.Combine(plan.LocalRoot, dirRel);
                Directory.CreateDirectory(dirPath);
            }

            // Удаляем staging (с короткой повторной попыткой)
            try {
                if (Directory.Exists(stagingRoot)) {
                    TryDeleteDirectoryWithRetry(stagingRoot, recursive: true, attempts: 3, delayMs: 150);
                }
            }
            catch {
            }

            // Обновление доведено до конца — снимаем маркер незавершённого обновления
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

        private static List<string> ListLocalFiles(string root) {
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

                list.Add(rel);
            }

            return list;
        }

        private static string NormalizeRelPath(string rel) {
            var r = rel.Replace('\\', '/');
            return r.TrimStart('/');
        }

        // ВАЖНО: игнорируем специальный файл FreeTP/.hash для всех игр.
        // Это сделано для пиратских сборок с сайта FreeTP.Org, чтобы при запуске игр
        // не открывался сайт FreeTP при каждом запуске. Лаунчер не должен проверять,
        // скачивать или удалять этот файл.
        private static bool IsIgnoredRelFile(string rel) {
            if (string.IsNullOrWhiteSpace(rel)) {
                return false;
            }

            var r = rel.Replace('\\', '/').TrimStart('/');
            return r.Equals("freetp/.hash", StringComparison.OrdinalIgnoreCase);
        }

        // ВАЖНО: сохраняем директорию FreeTP внутри игры и не удаляем её при очистке пустых папок.
        // Это также связано с пиратскими сборками с FreeTP.Org — пустая папка может понадобиться,
        // а её удаление может спровоцировать нежелательное поведение (например, открытие сайта).
        private static bool IsIgnoredRelDir(string relDir) {
            if (string.IsNullOrWhiteSpace(relDir)) {
                return false;
            }

            var r = relDir.Replace('\\', '/').Trim('/');

            // Совпадение папки "FreeTP" в корне игры
            return r.Equals("freetp", StringComparison.OrdinalIgnoreCase);
        }

        private static void CleanupEmptyDirs(string root, HashSet<string> keep) {
            if (!Directory.Exists(root)) {
                return;
            }

            // Проходим снизу вверх
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Length)) {
                try {
                    // Нормализуем относительный путь для сравнения с keep
                    var rel = Path.GetRelativePath(root, dir).TrimEnd(Path.DirectorySeparatorChar);
                    if (IsIgnoredRelDir(rel)) {
                        // Не удаляем директорию FreeTP даже если она пуста.
                        // Это нужно для пиратских сборок с FreeTP.Org, чтобы при запуске игр
                        // не провоцировать открытие сайта. Папку FreeTP сохраняем.
                        continue;
                    }

                    if (!Directory.EnumerateFileSystemEntries(dir).Any() && !keep.Contains(rel)) {
                        Directory.Delete(dir, false);
                    }
                }
                catch {
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
        private static bool IsServiceRelFile(string rel) {
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
            return r.Equals(UpdateMarkerFileName, StringComparison.OrdinalIgnoreCase)
                || r.Equals(IntegrityChecker.VersionMarkerFileName, StringComparison.OrdinalIgnoreCase);
        }

        // Считает SHA-256 и Blake3 за один проход по файлу.
        // Отмену проверяем на каждом блоке: у больших файлов один проход — это минуты.
        private static void ComputeHashes(string path, out string sha256Hex, out string blake3Hex, CancellationToken ct = default) {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: false);
            using var sha = SHA256.Create();
            var b3 = Blake3.Hasher.New();
            var buf = new byte[256 * 1024];
            int r;
            while ((r = f.Read(buf, 0, buf.Length)) > 0) {
                ct.ThrowIfCancellationRequested();
                sha.TransformBlock(buf, 0, r, null, 0);
                b3.Update(new ReadOnlySpan<byte>(buf, 0, r));
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha256Hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            var b3out = new byte[32];
            b3.Finalize(b3out);
            blake3Hex = Convert.ToHexString(b3out).ToLowerInvariant();
        }

        // Ставит маркер незавершённого обновления перед фазой активации.
        private static void WriteUpdateMarker(string localRoot, string version) {
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

        // Снимает маркер после успешного завершения активации.
        private static void ClearUpdateMarker(string localRoot) {
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

        private static void SafeDeleteFile(string path) {
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

                    // Give GC a chance to finalize any lingering FileStreams and retry
                    try {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    catch {
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
