// <copyright file="HttpClientProvider.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Net {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;

    public static class HttpClientProvider {
        private static readonly Lazy<HttpClient> shared = new Lazy<HttpClient>(Create);

        public static HttpClient Shared => shared.Value;

        private static HttpClient Create() {
            var handler = new HttpClientHandler {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false,
            };
            var http = new HttpClient(handler, disposeHandler: true) {
                Timeout = TimeSpan.FromSeconds(100),
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
