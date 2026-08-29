// <copyright file="ImageDiskCache.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    /// <summary>Что лежит в кеше по одному адресу: сами байты и метки для сверки с сервером.</summary>
    /// <param name="Bytes">Содержимое картинки.</param>
    /// <param name="ETag">Значение заголовка ETag, если сервер его дал.</param>
    /// <param name="LastModified">Значение заголовка Last-Modified, если сервер его дал.</param>
    internal sealed record CachedImage(byte[] Bytes, string? ETag, string? LastModified);

    /// <summary>
    /// Картинки на диске: обложки новостей, значки игр и обложки витрины переживают
    /// перезапуск лаунчера.
    /// <para>
    /// До этого кеш был только в памяти: каждый запуск заново качал все значки и обложки,
    /// хотя на сервере они не менялись месяцами. Теперь байты лежат в
    /// <c>%APPDATA%\ChillHub\imagecache</c>, а с сервером сверяются условным запросом —
    /// он отвечает «не менялось» (304) без тела. Картинка скачивается один раз за всё
    /// время, пока её не заменят на сервере.
    /// </para>
    /// <para>
    /// Имя файла — хеш адреса: адреса содержат символы, недопустимые в путях, и бывают
    /// длиннее предела файловой системы. Рядом с байтами лежит .json с метками сверки и
    /// самим адресом — по нему кеш можно прочитать глазами, когда что-то пойдёт не так.
    /// </para>
    /// </summary>
    internal static class ImageDiskCache {
        /// <summary>
        /// Потолок кеша на диске.
        /// <para>
        /// Обложки и значки — это десятки килобайт штука; сотня мегабайт с запасом
        /// покрывает всё, что лаунчер способен показать, и при этом не превращается в
        /// незаметно растущую свалку у пользователя.
        /// </para>
        /// </summary>
        internal const long MaxTotalBytes = 100L * 1024 * 1024;

        private static readonly object PruneLock = new object();

        /// <summary>
        /// Работает ли кеш на диске. Выключается на время прогона тестов: иначе они
        /// писали бы картинки в настоящий %APPDATA% пользователя, а соседний тест находил
        /// бы там ответ вместо подставленного ему.
        /// </summary>
        internal static bool Enabled { get; set; } = true;

        private static string DefaultDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChillHub",
                "imagecache");

        /// <summary>
        /// Каталог кеша. Подменяется тестами: трогать настоящий %APPDATA% в прогоне нельзя.
        /// </summary>
        internal static AsyncLocal<string?> ScopedDir { get; } = new AsyncLocal<string?>();

        /// <summary>Текущий каталог кеша.</summary>
        internal static string Dir => ScopedDir.Value ?? DefaultDir;

        /// <summary>Уводит кеш в отдельный каталог — для тестов.</summary>
        /// <param name="dir">Каталог, играющий роль %APPDATA%\ChillHub\imagecache.</param>
        /// <returns>Объект, возвращающий кеш на настоящее место.</returns>
        internal static IDisposable OverrideDirForTests(string dir) => new DirOverride(dir);

        /// <summary>Читает картинку из кеша; null — её там нет.</summary>
        /// <param name="url">Адрес картинки.</param>
        /// <returns>Байты и метки сверки либо null.</returns>
        internal static CachedImage? Read(string url) {
            if (!Enabled) {
                return null;
            }

            try {
                var (blob, meta) = PathsFor(url);
                if (!File.Exists(blob)) {
                    return null;
                }

                var bytes = File.ReadAllBytes(blob);
                if (bytes.Length == 0) {
                    return null;
                }

                var entry = File.Exists(meta)
                    ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(meta))
                    : null;

                return new CachedImage(bytes, entry?.ETag, entry?.LastModified);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ImageDiskCache.Read('{url}'): {ex.Message}");
                return null;
            }
        }

        /// <summary>Кладёт картинку в кеш вместе с метками сверки.</summary>
        /// <param name="url">Адрес картинки.</param>
        /// <param name="bytes">Содержимое.</param>
        /// <param name="etag">Заголовок ETag ответа.</param>
        /// <param name="lastModified">Заголовок Last-Modified ответа.</param>
        internal static void Save(string url, byte[] bytes, string? etag, string? lastModified) {
            if (!Enabled) {
                return;
            }

            try {
                if (bytes == null || bytes.Length == 0) {
                    return;
                }

                Directory.CreateDirectory(Dir);
                var (blob, meta) = PathsFor(url);
                File.WriteAllBytes(blob, bytes);

                var entry = new CacheEntry { Url = url, ETag = etag, LastModified = lastModified };
                File.WriteAllText(meta, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));

                Prune();
            }
            catch (Exception ex) {
                // Кеш — ускорение, а не источник правды: не записался, значит в следующий
                // раз картинка приедет по сети. Ронять из-за этого показ нельзя.
                Logging.Logger.Warn($"ImageDiskCache.Save('{url}'): {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет метки сверки, не переписывая байты: сервер ответил «не менялось».
        /// </summary>
        /// <param name="url">Адрес картинки.</param>
        /// <param name="etag">Заголовок ETag ответа.</param>
        /// <param name="lastModified">Заголовок Last-Modified ответа.</param>
        internal static void Touch(string url, string? etag, string? lastModified) {
            if (!Enabled) {
                return;
            }

            try {
                var (blob, meta) = PathsFor(url);
                if (!File.Exists(blob)) {
                    return;
                }

                // Время последнего обращения — по нему кеш решает, кого вытеснять первым.
                File.SetLastWriteTimeUtc(blob, DateTime.UtcNow);

                if (etag == null && lastModified == null) {
                    return;
                }

                var entry = new CacheEntry { Url = url, ETag = etag, LastModified = lastModified };
                File.WriteAllText(meta, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ImageDiskCache.Touch('{url}'): {ex.Message}");
            }
        }

        /// <summary>Стирает кеш целиком: явное «обновить» от пользователя.</summary>
        internal static void Clear() {
            try {
                if (Directory.Exists(Dir)) {
                    Directory.Delete(Dir, recursive: true);
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ImageDiskCache.Clear: {ex.Message}");
            }
        }

        /// <summary>
        /// Сколько байт занимают сами картинки. Считаются только они: рядом лежат ещё
        /// файлы с метками сверки, но это сотня байт на картинку, и мерить потолок кеша
        /// нужно тем же, чем он вытесняется, — иначе счёт и вытеснение расходятся.
        /// </summary>
        /// <returns>Суммарный размер картинок в кеше.</returns>
        internal static long TotalBytes() {
            try {
                if (!Directory.Exists(Dir)) {
                    return 0;
                }

                return new DirectoryInfo(Dir).EnumerateFiles("*.bin").Sum(f => f.Length);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ImageDiskCache.TotalBytes: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Держит кеш в пределах <see cref="MaxTotalBytes"/>, вытесняя те картинки, к
        /// которым дольше всего не обращались.
        /// </summary>
        internal static void Prune() {
            lock (PruneLock) {
                try {
                    if (!Directory.Exists(Dir)) {
                        return;
                    }

                    var blobs = new DirectoryInfo(Dir).GetFiles("*.bin");
                    var total = blobs.Sum(f => f.Length);
                    if (total <= MaxTotalBytes) {
                        return;
                    }

                    foreach (var f in blobs.OrderBy(f => f.LastWriteTimeUtc)) {
                        if (total <= MaxTotalBytes) {
                            break;
                        }

                        total -= f.Length;
                        var meta = Path.ChangeExtension(f.FullName, ".json");
                        SafeDelete(f.FullName);
                        SafeDelete(meta);
                    }
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"ImageDiskCache.Prune: {ex.Message}");
                }
            }
        }

        private static void SafeDelete(string path) {
            try {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ImageDiskCache: не удалось удалить '{path}': {ex.Message}");
            }
        }

        private static (string Blob, string Meta) PathsFor(string url) {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url ?? string.Empty))).ToLowerInvariant();
            return (Path.Combine(Dir, key + ".bin"), Path.Combine(Dir, key + ".json"));
        }

        /// <summary>Метки сверки одной картинки; адрес — чтобы кеш читался глазами.</summary>
        private sealed class CacheEntry {
            public string Url { get; set; } = string.Empty;

            public string? ETag { get; set; }

            public string? LastModified { get; set; }
        }

        private sealed class DirOverride : IDisposable {
            private readonly string? previous;

            internal DirOverride(string dir) {
                this.previous = ScopedDir.Value;
                ScopedDir.Value = dir;
            }

            public void Dispose() => ScopedDir.Value = this.previous;
        }
    }
}
