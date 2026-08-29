// <copyright file="NewsContentClient.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    /// <summary>
    /// Забирает текст новости с сервера. Отдельным типом — чтобы страница проверялась
    /// на «нет сети», «404» и «пустой ответ» без живого WebView2: клиент подставляемый.
    /// <para>
    /// ТЕКСТ НОВОСТИ ЛЕЖИТ НА ДИСКЕ. Открытая новость перечитывалась с сервера каждый
    /// раз, хотя не меняется неделями, а без сети не открывалась вовсе. Теперь с
    /// сервером сверяются условным запросом: ответ «не менялось» приходит без тела, а
    /// пропавшая сеть отдаёт сохранённое.
    /// </para>
    /// </summary>
    internal sealed class NewsContentClient {
        private readonly HttpClient http;

        /// <summary>Initializes a new instance of the <see cref="NewsContentClient"/> class.</summary>
        /// <param name="http">Клиент, которым забирается markdown новости.</param>
        internal NewsContentClient(HttpClient http) => this.http = http;

        /// <summary>Забирает markdown новости. Отказ сервера и обрыв сети выходят исключением.</summary>
        /// <param name="markdownUrl">Адрес markdown-файла новости.</param>
        /// <returns>Текст новости.</returns>
        internal async Task<string> FetchAsync(string markdownUrl) {
            var cached = NewsContentCache.Read(markdownUrl);
            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, markdownUrl);
                if (cached?.ETag is { Length: > 0 } etag) {
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag, isWeak: etag.StartsWith("W/", StringComparison.Ordinal)));
                }
                else if (cached?.LastModified is { Length: > 0 } modified
                         && DateTimeOffset.TryParse(modified, out var since)) {
                    request.Headers.IfModifiedSince = since;
                }

                using var response = await this.http.SendAsync(request).ConfigureAwait(false);

                // «Не менялось» приходит без тела — берём сохранённое и продлеваем метки.
                if (response.StatusCode == HttpStatusCode.NotModified && cached != null) {
                    NewsContentCache.Touch(markdownUrl, Tag(response), Modified(response));
                    return cached.Text;
                }

                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                NewsContentCache.Save(markdownUrl, text, Tag(response), Modified(response));
                return text;
            }
            catch (Exception) when (cached != null) {
                // Сети нет, сервер отказал — открытая однажды новость всё равно
                // открывается: отказ вместо уже сохранённого текста ничего не даёт.
                Logging.Logger.Info($"[news] '{markdownUrl}' взята с диска: сервер недоступен");
                return cached.Text;
            }
        }

        private static string? Tag(HttpResponseMessage response) => response.Headers.ETag?.ToString();

        private static string? Modified(HttpResponseMessage response) =>
            response.Content.Headers.LastModified?.ToString("R");
    }
}
