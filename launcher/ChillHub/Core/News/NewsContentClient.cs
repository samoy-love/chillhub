// <copyright file="NewsContentClient.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System.Net.Http;
    using System.Threading.Tasks;

    /// <summary>
    /// Забирает текст новости с сервера. Отдельным типом — чтобы страница проверялась
    /// на «нет сети», «404» и «пустой ответ» без живого WebView2: клиент подставляемый.
    /// </summary>
    internal sealed class NewsContentClient {
        private readonly HttpClient http;

        /// <summary>Initializes a new instance of the <see cref="NewsContentClient"/> class.</summary>
        /// <param name="http">Клиент, которым забирается markdown новости.</param>
        internal NewsContentClient(HttpClient http) => this.http = http;

        /// <summary>Забирает markdown новости. Отказ сервера и обрыв сети выходят исключением.</summary>
        /// <param name="markdownUrl">Адрес markdown-файла новости.</param>
        /// <returns>Текст новости.</returns>
        internal Task<string> FetchAsync(string markdownUrl) => this.http.GetStringAsync(markdownUrl);
    }
}
