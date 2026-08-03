// <copyright file="HttpClientProvider.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Net {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;

    public static class HttpClientProvider {
        private static readonly Lazy<HttpClient> shared = new Lazy<HttpClient>(() => Create(TimeSpan.FromSeconds(100)));
        private static readonly Lazy<HttpClient> downloads = new Lazy<HttpClient>(() => Create(Timeout.InfiniteTimeSpan));

        /// <summary>
        /// Gets клиент для коротких запросов (API, манифесты): общий таймаут 100 с.
        /// </summary>
        public static HttpClient Shared => shared.Value;

        /// <summary>
        /// Gets клиент для скачивания файлов игры без общего таймаута.
        /// HttpClient.Timeout считает ВСЮ операцию, включая чтение тела ответа, поэтому
        /// на нём любой файл, который качается дольше таймаута, обрывался посреди потока
        /// (в сборках игр встречаются файлы по 400 МБ — на медленном канале это гарантированный
        /// разрыв). Ограничение по времени здесь ставит вызывающий: на каждую попытку
        /// заводится связанный CTS, который срабатывает только при простое канала.
        /// </summary>
        public static HttpClient Downloads => downloads.Value;

        private static HttpClient Create(TimeSpan timeout) {
            var handler = new HttpClientHandler {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false,
            };
            var http = new HttpClient(handler, disposeHandler: true) {
                Timeout = timeout,
            };
            http.DefaultRequestHeaders.UserAgent.Clear();

            // Set a single well-formed UA string, including a comment per RFC 7231
            // Example: "ChillHub/1.0 (+https://chillhub.local)"
            try {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ChillHub/1.0 (+https://chillhub.local)");
            }
            catch {
                // Fallback to minimal UA if parsing fails
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ChillHub/1.0");
            }

            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
            return http;
        }
    }
}
