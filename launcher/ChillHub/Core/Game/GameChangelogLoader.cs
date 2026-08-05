// <copyright file="GameChangelogLoader.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// Changelog игры: список новостей из `/news/games/{gid}/index.json` с адресами
    /// обложек, приведёнными к абсолютным. Сеть подставляемая, про контролы класс не знает.
    /// </summary>
    internal sealed class GameChangelogLoader {
        private readonly HttpClient http;

        /// <summary>Initializes a new instance of the <see cref="GameChangelogLoader"/> class.</summary>
        /// <param name="http">Клиент, которым ходят за индексом новостей игры.</param>
        internal GameChangelogLoader(HttpClient http) => this.http = http;

        /// <summary>Адрес индекса новостей игры.</summary>
        /// <param name="baseApi">База API из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Полный адрес.</returns>
        internal static string IndexUrl(string baseApi, string gameId) => $"{baseApi}/news/games/{gameId}/index.json";

        /// <summary>Адрес markdown-файла одной записи changelog.</summary>
        /// <param name="baseApi">База API из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="slug">Slug записи.</param>
        /// <returns>Полный адрес.</returns>
        internal static string ArticleUrl(string baseApi, string gameId, string slug) => $"{baseApi}/news/games/{gameId}/{slug}.md";

        /// <summary>
        /// Дописывает базу API к относительным адресам обложек: сервер отдаёт их
        /// от корня, а картинку грузит уже клиент.
        /// </summary>
        /// <param name="baseApi">База API из конфига.</param>
        /// <param name="items">Записи changelog (правятся на месте).</param>
        /// <returns>Тот же список — для удобства вызова.</returns>
        internal static List<NewsItem> AbsolutizeCovers(string baseApi, List<NewsItem> items) {
            foreach (var item in items) {
                if (!string.IsNullOrWhiteSpace(item.CoverUrl) && item.CoverUrl.StartsWith("/", StringComparison.Ordinal)) {
                    item.CoverUrl = baseApi + item.CoverUrl;
                }
            }

            return items;
        }

        /// <summary>Забирает changelog игры и приводит адреса обложек к абсолютным.</summary>
        /// <param name="baseApi">База API из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Записи changelog; пустой список, если сервер прислал пусто.</returns>
        internal async Task<List<NewsItem>> LoadAsync(string baseApi, string gameId) {
            var url = IndexUrl(baseApi, gameId);
            var index = await this.http.GetFromJsonAsync<NewsIndex>(url).ConfigureAwait(true);
            var items = index?.Items ?? new List<NewsItem>();
            return AbsolutizeCovers(baseApi, items);
        }
    }
}
