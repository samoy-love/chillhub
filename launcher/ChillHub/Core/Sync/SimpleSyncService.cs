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
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    using Blake3;

    using ChillHub.Core;
    using ChillHub.Core.Net;

    public class SimpleSyncService : ISyncService {
        private readonly HttpClient http;

        public SimpleSyncService(HttpClient? http = null) {
            this.http = http ?? HttpClientProvider.Shared;
        }

        /// <inheritdoc/>
        public async Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
            var manifest = await this.http.GetFromJsonAsync<Manifest>(manifestUrl, ct)
                           ?? throw new InvalidDataException("manifest is null");
            return manifest;
        }

        /// <inheritdoc/>
        public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct) {
            var plan = new DiffPlan {
                GameId = manifest.GameId,
                Version = manifest.Version,
                LocalRoot = localRoot,
            };

            // Соберём множество файлов из манифеста
            var manifestFiles = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var mf in manifest.Files) {
                var relNorm = mf.Path.Replace('\\', '/');
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

            // Определим новые/изменённые: при наличии хеша сравниваем по хешу, иначе по размеру
            foreach (var kv in manifestFiles) {
                ct.ThrowIfCancellationRequested();
                var rel = kv.Key;
                var mf = kv.Value;
                var localPath = Path.Combine(localRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                bool needDownload = true;
                string reason = "missing";
                if (File.Exists(localPath)) {
                    try {
                        var info = new FileInfo(localPath);

                        // Если есть sha256/blake3 в манифесте — считаем локальный хеш и сравним
                        if (!string.IsNullOrWhiteSpace(mf.Sha256) || !string.IsNullOrWhiteSpace(mf.Blake3)) {
                            using var f = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: false);
                            using var sha = SHA256.Create();
                            var b3 = Blake3.Hasher.New();
                            var buf = new byte[256 * 1024];
                            int r;
                            while ((r = f.Read(buf, 0, buf.Length)) > 0) {
                                sha.TransformBlock(buf, 0, r, null, 0);
                                b3.Update(new ReadOnlySpan<byte>(buf, 0, r));
                            }

                            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            var shaHex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                            var b3out = new byte[32];
                            b3.Finalize(b3out);
                            var b3Hex = Convert.ToHexString(b3out).ToLowerInvariant();

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
            }

            plan.TotalFilesToDownload = plan.Downloads.Count;

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

            // Проверка свободного места на диске под рассчитанный дифф (без запаса)
            if (total > 0) {
                var root = Path.GetPathRoot(Path.GetFullPath(plan.LocalRoot)) ?? plan.LocalRoot;
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < total) {
                    throw new IOException($"Недостаточно свободного места на диске. Требуется {total} байт, доступно {drive.AvailableFreeSpace} байт.");
                }
            }

            progress.Report(new SyncProgress { Stage = "Checking", BytesDownloaded = 0, TotalBytes = total, FilesDownloaded = 0, TotalFiles = totalFiles });

            // Пустые директории будем создавать в самом конце (после очистки),
            // чтобы их не удалить во время Cleanup

            // Скачивание недостающих/изменённых (многопоточно)
            progress.Report(new SyncProgress { Stage = "Downloading", BytesDownloaded = 0, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
            var degree = Math.Clamp(ConfigService.Current.DownloadThreads, 2, 16);
            using (var sem = new SemaphoreSlim(degree)) {
                var tasks = new List<Task>();
                foreach (var t in plan.Downloads) {
                    await sem.WaitAsync(ct);
                    tasks.Add(Task.Run(
                        async () => {
                            try {
                                ct.ThrowIfCancellationRequested();
                                var targetRel = t.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                                var stagingFile = Path.Combine(stagingRoot, targetRel);
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

                                            using var resp = await this.http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

                                            using var src = await resp.Content.ReadAsStreamAsync(ct);
                                            using var dst = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                                            int read;
                                            while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0) {
                                                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                                                Interlocked.Add(ref downloaded, read);
                                                progress.Report(new SyncProgress { Stage = "Downloading", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
                                            }

                                            break; // success
                                        }
                                        catch (Exception ex) {
                                            attempt++;
                                            if (attempt >= maxAttempts) {
                                                throw new IOException($"Ошибка загрузки {t.RelativePath}: {ex.Message}", ex);
                                            }

                                            var delayMs = (int)Math.Min(5000, 500 * Math.Pow(2, attempt - 1));
                                            await Task.Delay(delayMs, ct);

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
                                progress.Report(new SyncProgress { Stage = "Downloading", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
                                sem.Release();
                            }
                        }, ct));
                }

                await Task.WhenAll(tasks);
            }

            // Верификация (хеши пропустим на моках)
            progress.Report(new SyncProgress { Stage = "Verifying", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });

            // Активация: перенести staging файлы в основной корень
            progress.Report(new SyncProgress { Stage = "Activating", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
            foreach (var t in plan.Downloads) {
                ct.ThrowIfCancellationRequested();
                var rel = t.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var dstPath = Path.Combine(plan.LocalRoot, rel);
                var srcPath = Path.Combine(stagingRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                if (File.Exists(dstPath)) {
                    File.Delete(dstPath);
                }

                File.Move(srcPath, dstPath);
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

            // Удаление лишних файлов
            foreach (var rel in plan.ToDelete) {
                var path = Path.Combine(plan.LocalRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                try {
                    if (File.Exists(path)) {
                        File.Delete(path);
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
                var dirPath = Path.Combine(plan.LocalRoot, dirRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(dirPath);
            }

            // Удаляем staging
            try {
                if (Directory.Exists(stagingRoot)) {
                    Directory.Delete(stagingRoot, true);
                }
            }
            catch {
            }

            // Финальный сигнал о завершении
            progress.Report(new SyncProgress { Stage = "Completed", BytesDownloaded = downloaded, TotalBytes = total, FilesDownloaded = filesDone, TotalFiles = totalFiles });
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
    }
}
