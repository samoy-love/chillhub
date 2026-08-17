// <copyright file="HomeFeed.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;

    /// <summary>
    /// Адреса данных главного экрана и приведение полученного к пригодному для показа виду.
    /// Сети здесь нет: запрос выполняет вызывающий код, здесь — только строки и списки.
    /// </summary>
    internal static class HomeFeed {
        /// <summary>
        /// Ответ «такого нет» — это не сбой. У игры может не быть ни ленты новостей, ни
        /// списка сборок: сервер отвечает 404, и правильная реакция — пустой раздел, а не
        /// красная строка «не удалось загрузить» с отчётом на сервер. До этой проверки
        /// каждое открытие игры без новостей писало ошибку в лог и слало авто-отчёт.
        /// </summary>
        /// <param name="ex">Пойманное исключение.</param>
        /// <returns>true, если сервер ответил 404.</returns>
        internal static bool IsNotFound(Exception? ex) {
            for (var current = ex; current != null; current = current.InnerException) {
                if (current is HttpRequestException http && http.StatusCode == HttpStatusCode.NotFound) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Адрес списка игр.</summary>
        /// <param name="baseApi">База адреса сервера.</param>
        /// <returns>Полный адрес.</returns>
        internal static string GamesUrl(string baseApi) => $"{baseApi}/api/games";

        /// <summary>Адрес списка сборок игры.</summary>
        /// <param name="baseApi">База адреса сервера.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Полный адрес.</returns>
        internal static string BuildsUrl(string baseApi, string gameId) => $"{baseApi}/api/games/{gameId}/builds";

        /// <summary>Адрес новостей лаунчера.</summary>
        /// <param name="baseApi">База адреса сервера.</param>
        /// <returns>Полный адрес.</returns>
        internal static string LauncherNewsUrl(string baseApi) => $"{baseApi}/news/index.json";

        /// <summary>Адрес новостей игры.</summary>
        /// <param name="baseApi">База адреса сервера.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Полный адрес.</returns>
        internal static string GameNewsUrl(string baseApi, string gameId) => $"{baseApi}/news/games/{gameId}/index.json";

        /// <summary>Адрес текста новости лаунчера.</summary>
        /// <param name="baseApi">База адреса сервера.</param>
        /// <param name="slug">Идентификатор новости.</param>
        /// <returns>Полный адрес.</returns>
        internal static string LauncherNewsItemUrl(string baseApi, string? slug) => $"{baseApi}/news/{slug}.md";

        /// <summary>Адрес текста новости игры.</summary>
        /// <param name="baseApi">База адреса сервера.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="slug">Идентификатор новости.</param>
        /// <returns>Полный адрес.</returns>
        internal static string GameNewsItemUrl(string baseApi, string gameId, string? slug) => $"{baseApi}/news/games/{gameId}/{slug}.md";

        /// <summary>Достраивает корнеотносительные обложки новостей до полного адреса.</summary>
        /// <param name="items">Новости.</param>
        /// <param name="baseApi">База адреса сервера.</param>
        internal static void NormalizeCoverUrls(IEnumerable<NewsItem> items, string baseApi) {
            foreach (var it in items) {
                if (!string.IsNullOrWhiteSpace(it.CoverUrl) && it.CoverUrl.StartsWith("/")) {
                    it.CoverUrl = baseApi + it.CoverUrl;
                }
            }
        }

        /// <summary>
        /// Упорядочивает сборки от новой к старой.
        /// <para>
        /// Сервер отдаёт сборки в произвольном порядке, а код ниже берёт из списка
        /// «последнюю версию». Сортируем сами: на проде первым элементом приходила
        /// 1.0.2 при доступной 1.1.10.
        /// </para>
        /// </summary>
        /// <param name="items">Сборки как их вернул сервер.</param>
        /// <returns>Новый список, отсортированный по убыванию версии.</returns>
        internal static List<string> SortBuilds(IEnumerable<string>? items) =>
            (items ?? new List<string>())
                .OrderByDescending(v => v, Comparer<string>.Create(VersionOrder.Compare))
                .ToList();

        /// <summary>
        /// Версия, которую лаунчер поставит: всегда latest из списка игр, а если сервер
        /// его не назвал — максимальная из списка сборок. Пустая строка означает,
        /// что ставить нечего.
        /// </summary>
        /// <param name="game">Игра из списка (может отсутствовать).</param>
        /// <param name="builds">Список сборок игры.</param>
        /// <returns>Выбранная версия или пустая строка.</returns>
        internal static string? SelectVersion(GameInfo? game, IEnumerable<string> builds) {
            var version = game?.LatestVersion;
            if (string.IsNullOrWhiteSpace(version)) {
                // Фолбэк: максимальная версия из списка сборок, если latest неизвестен
                version = VersionOrder.SelectLatest(builds);
            }

            return version;
        }
    }
}
