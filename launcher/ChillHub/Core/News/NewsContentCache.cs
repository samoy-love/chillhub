// <copyright file="NewsContentCache.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    /// <summary>Текст новости, сохранённый на диске, вместе с метками сверки.</summary>
    /// <param name="Text">Сам markdown.</param>
    /// <param name="ETag">Заголовок ETag ответа, если сервер его дал.</param>
    /// <param name="LastModified">Заголовок Last-Modified ответа, если сервер его дал.</param>
    internal sealed record CachedNews(string Text, string? ETag, string? LastModified);

    /// <summary>
    /// Тексты новостей на диске: <c>%APPDATA%\ChillHub\newscache</c>.
    /// <para>
    /// Открытая новость перечитывалась с сервера каждый раз — при том что текст её не
    /// меняется неделями, а без сети она не открывалась вовсе. Теперь markdown лежит
    /// рядом с картинками, сверяется с сервером условным запросом и достаётся с диска,
    /// когда сети нет.
    /// </para>
    /// <para>
    /// Свой каталог, а не общий с картинками: текст новости — это килобайты, обложка —
    /// мегабайты, и вытеснять первое вторым было бы обидно. Ограничение здесь по числу
    /// файлов, а не по объёму: столько новостей никто не открывает.
    /// </para>
    /// </summary>
    internal static class NewsContentCache {
        /// <summary>Сколько новостей держим на диске.</summary>
        internal const int MaxEntries = 200;

        private static readonly object Gate = new object();

        private static string DefaultDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChillHub",
                "newscache");

        /// <summary>Каталог кеша. Подменяется тестами: трогать настоящий %APPDATA% в прогоне нельзя.</summary>
        internal static AsyncLocal<string?> ScopedDir { get; } = new AsyncLocal<string?>();

        /// <summary>Работает ли кеш. Выключается на время прогона тестов.</summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>Текущий каталог кеша.</summary>
        internal static string Dir => ScopedDir.Value ?? DefaultDir;

        /// <summary>Уводит кеш в отдельный каталог — для тестов.</summary>
        /// <param name="dir">Каталог, играющий роль %APPDATA%\ChillHub\newscache.</param>
        /// <returns>Объект, возвращающий кеш на настоящее место.</returns>
        internal static IDisposable OverrideDirForTests(string dir) => new DirOverride(dir);

        /// <summary>Читает новость из кеша; null — её там нет.</summary>
        /// <param name="url">Адрес markdown-файла.</param>
        /// <returns>Текст и метки сверки либо null.</returns>
        internal static CachedNews? Read(string url) {
            if (!Enabled) {
                return null;
            }

            try {
                var path = PathFor(url);
                if (!File.Exists(path)) {
                    return null;
                }

                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                return string.IsNullOrEmpty(entry?.Text)
                    ? null
                    : new CachedNews(entry!.Text, entry.ETag, entry.LastModified);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"NewsContentCache.Read('{url}'): {ex.Message}");
                return null;
            }
        }

        /// <summary>Кладёт новость в кеш вместе с метками сверки.</summary>
        /// <param name="url">Адрес markdown-файла.</param>
        /// <param name="text">Текст новости.</param>
        /// <param name="etag">Заголовок ETag ответа.</param>
        /// <param name="lastModified">Заголовок Last-Modified ответа.</param>
        internal static void Save(string url, string text, string? etag, string? lastModified) {
            if (!Enabled || string.IsNullOrEmpty(text)) {
                return;
            }

            try {
                lock (Gate) {
                    Directory.CreateDirectory(Dir);
                    var entry = new Entry { Url = url, Text = text, ETag = etag, LastModified = lastModified };
                    ChillHub.Update.AtomicFile.WriteAllText(
                        PathFor(url),
                        JsonSerializer.Serialize(entry),
                        Core.SelfUpdate.SelfUpdateRules.Utf8NoBom);
                    Prune();
                }
            }
            catch (Exception ex) {
                // Кеш — ускорение, а не источник правды: не записался, значит в
                // следующий раз новость приедет по сети.
                Logging.Logger.Warn($"NewsContentCache.Save('{url}'): {ex.Message}");
            }
        }

        /// <summary>Сервер ответил «не менялось» — обновляем только метки сверки.</summary>
        /// <param name="url">Адрес markdown-файла.</param>
        /// <param name="etag">Заголовок ETag ответа.</param>
        /// <param name="lastModified">Заголовок Last-Modified ответа.</param>
        internal static void Touch(string url, string? etag, string? lastModified) {
            var cached = Read(url);
            if (cached != null) {
                Save(url, cached.Text, etag ?? cached.ETag, lastModified ?? cached.LastModified);
            }
        }

        /// <summary>Оставляет только самые свежие записи: старые новости никто не переоткрывает.</summary>
        private static void Prune() {
            try {
                var files = new DirectoryInfo(Dir).GetFiles("*.json");
                if (files.Length <= MaxEntries) {
                    return;
                }

                foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxEntries)) {
                    try {
                        old.Delete();
                    }
                    catch (IOException) {
                        // Занят — уйдёт в следующую уборку.
                    }
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"NewsContentCache.Prune: {ex.Message}");
            }
        }

        /// <summary>Имя файла — хеш адреса: адреса содержат недопустимые в путях символы.</summary>
        /// <param name="url">Адрес markdown-файла.</param>
        /// <returns>Полный путь к записи кеша.</returns>
        private static string PathFor(string url) {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url ?? string.Empty)))
                .ToLowerInvariant();
            return Path.Combine(Dir, key + ".json");
        }

        /// <summary>Одна запись кеша.</summary>
        private sealed class Entry {
            public string Url { get; set; } = string.Empty;

            public string Text { get; set; } = string.Empty;

            public string? ETag { get; set; }

            public string? LastModified { get; set; }
        }

        /// <summary>Возвращает кеш на настоящее место после <see cref="OverrideDirForTests"/>.</summary>
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
