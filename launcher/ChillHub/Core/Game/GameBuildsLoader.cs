// <copyright file="GameBuildsLoader.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// Список сборок игры с сервера: загрузка, порядок «от новых к старым» и выбор
    /// версии, которая подставляется в выпадающий список по умолчанию.
    /// Сеть подставляемая, про контролы класс не знает.
    /// </summary>
    internal sealed class GameBuildsLoader {
        private readonly HttpClient http;

        /// <summary>Initializes a new instance of the <see cref="GameBuildsLoader"/> class.</summary>
        /// <param name="http">Клиент, которым ходят за списком сборок.</param>
        internal GameBuildsLoader(HttpClient http) => this.http = http;

        /// <summary>Адрес списка сборок игры.</summary>
        /// <param name="baseApi">База API из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Полный адрес.</returns>
        internal static string BuildsUrl(string baseApi, string gameId) => $"{baseApi}/api/games/{gameId}/builds";

        /// <summary>
        /// Упорядочивает сборки от новых к старым: сервер порядок не гарантирует, а выпадающий
        /// список и выбор «по умолчанию» опираются на него.
        /// </summary>
        /// <param name="items">Сборки в том виде, в каком их прислал сервер.</param>
        /// <returns>Тот же набор, отсортированный по смыслу версий.</returns>
        internal static List<string> Order(IEnumerable<string>? items)
            => (items ?? new List<string>())
                .OrderByDescending(v => v, Comparer<string>.Create(VersionOrder.Compare))
                .ToList();

        /// <summary>
        /// Индекс версии, которую нужно показать выбранной. По умолчанию подставляется
        /// установленная версия, иначе последняя; если ни одна не нашлась — первая из списка.
        /// </summary>
        /// <param name="builds">Упорядоченный список сборок.</param>
        /// <param name="preselect">Версия, которую хотелось бы выбрать.</param>
        /// <returns>Индекс для выпадающего списка либо -1, если выбирать не из чего.</returns>
        internal static int SelectIndex(List<string> builds, string? preselect) {
            var idx = builds.FindIndex(b => string.Equals(b?.Trim(), (preselect ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : (builds.Count > 0 ? 0 : -1);
        }

        /// <summary>Забирает список сборок с сервера и приводит его к нужному порядку.</summary>
        /// <param name="baseApi">База API из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Сборки от новых к старым.</returns>
        internal async Task<List<string>> LoadAsync(string baseApi, string gameId) {
            var url = BuildsUrl(baseApi, gameId);
            var resp = await this.http.GetFromJsonAsync<BuildsResponse>(url).ConfigureAwait(true);
            return Order(resp?.Items);
        }
    }
}
