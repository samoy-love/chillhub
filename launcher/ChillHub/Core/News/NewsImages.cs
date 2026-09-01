// <copyright file="NewsImages.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Подставляет картинки новости прямо в её html.
    /// <para>
    /// НА СТРАНИЦЕ НОВОСТИ КАРТИНОК НЕ БЫЛО ВИДНО. Страница отдаётся в WebView2 через
    /// NavigateToString, и origin у неё — <c>about:blank</c>: тег <c>base</c> адрес
    /// собирает, а вот подгрузить по нему картинку такой странице уже не дают. С
    /// сервера при этом всё отдавалось исправно.
    /// </para>
    /// <para>
    /// Поэтому картинки не подгружаются, а вкладываются в саму страницу — байтами,
    /// взятыми ТЕМ ЖЕ путём, что и обложки новостей на главном экране: общий кеш на
    /// диске, условный запрос к серверу и содержимое с диска, когда сети нет. Открытая
    /// однажды новость показывает картинки и без сети.
    /// </para>
    /// </summary>
    internal static class NewsImages {
        /// <summary>Адрес картинки в теге img.</summary>
        private static readonly Regex ImageSource = new Regex(
            @"<img\s[^>]*?src\s*=\s*(?<q>[""'])(?<src>[^""']+)\k<q>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Потолок на одну вкладываемую картинку.
        /// <para>
        /// Вложенная картинка раздувает саму страницу — она уезжает в WebView2 одной
        /// строкой. Что крупнее, оставляем ссылкой: пусть лучше не покажется одна
        /// тяжёлая, чем страница станет неподъёмной целиком.
        /// </para>
        /// </summary>
        internal const int MaxInlineBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Заменяет адреса картинок на их содержимое.
        /// </summary>
        /// <param name="html">Готовая страница новости.</param>
        /// <param name="origin">База для относительных адресов.</param>
        /// <param name="fetch">Чем забирать байты: тот же загрузчик, что и у обложек.</param>
        /// <returns>Страница, в которой картинки лежат внутри.</returns>
        internal static async Task<string> InlineAsync(string html, string origin, Func<string, Task<byte[]>> fetch) {
            if (string.IsNullOrEmpty(html) || fetch == null) {
                return html;
            }

            // Один адрес — одна загрузка, сколько бы раз он ни встретился в тексте.
            // Адреса сверяются посимвольно: ассеты лежат на Linux, где Before.png и
            // before.png — разные файлы. Без учёта регистра вторая картинка считалась
            // бы уже загруженной, и на её место вставали байты первой.
            var byUrl = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in ImageSource.Matches(html)) {
                var src = m.Groups["src"].Value;
                if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || byUrl.ContainsKey(src)) {
                    continue;
                }

                if (Resolve(src, origin) is not string url) {
                    continue;
                }

                try {
                    var bytes = await fetch(url).ConfigureAwait(false);
                    if (bytes is { Length: > 0 } && bytes.Length <= MaxInlineBytes) {
                        byUrl[src] = "data:" + MimeOf(url) + ";base64," + Convert.ToBase64String(bytes);
                    }
                }
                catch (Exception ex) {
                    // Не достали — оставляем ссылку как была: пустое место вместо
                    // картинки хуже, чем картинка, которая, может быть, ещё загрузится.
                    Logging.Logger.Warn($"[news] картинка '{url}' не вложена: {ex.Message}");
                }
            }

            if (byUrl.Count == 0) {
                return html;
            }

            return ImageSource.Replace(html, m => {
                var src = m.Groups["src"].Value;
                return byUrl.TryGetValue(src, out var data)
                    ? m.Value.Replace(src, data, StringComparison.Ordinal)
                    : m.Value;
            });
        }

        /// <summary>Собирает абсолютный адрес картинки; null — собрать не вышло.</summary>
        /// <param name="src">Адрес из тега.</param>
        /// <param name="origin">База страницы.</param>
        /// <returns>Абсолютный адрес или null.</returns>
        private static string? Resolve(string src, string origin) {
            if (Uri.TryCreate(src, UriKind.Absolute, out var absolute)) {
                return absolute.ToString();
            }

            return Uri.TryCreate(new Uri(origin.TrimEnd('/') + "/"), src, out var combined)
                ? combined.ToString()
                : null;
        }

        /// <summary>Тип картинки по расширению: браузеру он нужен в самой строке data.</summary>
        /// <param name="url">Адрес картинки.</param>
        /// <returns>MIME-тип.</returns>
        private static string MimeOf(string url) {
            var path = url;
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) {
                path = path.Substring(0, cut);
            }

            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
                return "image/png";
            }

            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) {
                return "image/gif";
            }

            if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) {
                return "image/webp";
            }

            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) {
                return "image/svg+xml";
            }

            return "image/jpeg";
        }
    }
}
