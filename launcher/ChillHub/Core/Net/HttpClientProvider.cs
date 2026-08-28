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
        /// <summary>
        /// Как лаунчер представляется серверу.
        /// <para>
        /// Заголовок обязателен для каждого клиента, а не только для этого: сайт стоит за
        /// Cloudflare, и запросы без User-Agent тот молча роняет — соединение просто
        /// повисает до таймаута. Клиент картинок ходил без заголовка, и обложки в ленте
        /// пропадали примерно на трети запросов.
        /// </para>
        /// </summary>
        public const string UserAgent = "ChillHub/1.0 (+https://chillhub.local)";

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

        /// <summary>
        /// Проставляет клиенту опознавательные заголовки лаунчера.
        /// Вынесено наружу, чтобы клиент, заведённый в обход этого провайдера
        /// (например, отдельный клиент под картинки), не остался безымянным.
        /// </summary>
        /// <param name="http">Клиент, которому нужны заголовки.</param>
        public static void ApplyIdentity(HttpClient http) {
            if (http == null) {
                return;
            }

            http.DefaultRequestHeaders.UserAgent.Clear();

            // Строка с комментарием по RFC 7231; если разбор не удался — минимальный вариант.
            try {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            }
            catch (FormatException) {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ChillHub/1.0");
            }
        }

        private static HttpClient Create(TimeSpan timeout) {
            var handler = new HttpClientHandler {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false,
            };
            var http = new HttpClient(handler, disposeHandler: true) {
                Timeout = timeout,
            };
            ApplyIdentity(http);
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
            return http;
        }
    }
}
