// <copyright file="ModsLink.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;

    /// <summary>
    /// Ссылка на страницу установленного модпака.
    /// <para>
    /// Игрок видел имя сборки — «Moo Modpack 1.9.9» — и ничего больше: что в неё
    /// входит, кто её собрал и что изменилось в последней версии, приходилось искать
    /// самому. Страница пакета отвечает на всё это разом.
    /// </para>
    /// <para>
    /// Адрес собирается из имени версии (<c>vcMoo-Moo_Modpack-1.9.9</c>) и слага
    /// сообщества, который присылает сервер. Слаг именно присылается, а не выводится
    /// из идентификатора игры: у нас «risk-of-rain-2», а на Thunderstore
    /// «riskofrain2», и угаданная ссылка ведёт в никуда — это хуже, чем её отсутствие.
    /// </para>
    /// </summary>
    /// <summary>Что показать в строке «Модпак» на странице игры.</summary>
    /// <param name="Visible">Показывать ли строку вообще.</param>
    /// <param name="Name">Имя сборки для игрока.</param>
    /// <param name="Url">Адрес страницы пакета; пусто — вести некуда.</param>
    internal readonly record struct ModsRow(bool Visible, string Name, string Url);

    internal static class ModsLink {
        /// <summary>
        /// Строка «Модпак» целиком.
        /// <para>
        /// Строки нет вовсе у игры без модпака: пустое «Модпак: —» не рассказывает о
        /// ней ничего. Ссылки нет, когда сервер не прислал слаг сообщества, — имя
        /// пакета остаётся, а вести в никуда мы не будем.
        /// </para>
        /// </summary>
        /// <param name="mods">Настройки модов игры.</param>
        /// <returns>Что показать в строке.</returns>
        internal static ModsRow RowFor(ModsInfo? mods) {
            var name = mods is { HasLatest: true } ? mods.Describe() : string.Empty;
            return string.IsNullOrWhiteSpace(name)
                ? new ModsRow(false, string.Empty, string.Empty)
                : new ModsRow(true, name, PackagePage(mods));
        }

        /// <summary>
        /// Страница модпака на Thunderstore; пусто, если собрать адрес не из чего.
        /// </summary>
        /// <param name="mods">Настройки модов игры.</param>
        /// <returns>Адрес страницы или пустая строка.</returns>
        internal static string PackagePage(ModsInfo? mods) {
            var community = (mods?.Community ?? string.Empty).Trim();
            if (community.Length == 0 || Split(mods?.Version) is not (string team, string package)) {
                return string.Empty;
            }

            return $"https://thunderstore.io/c/{community}/p/{team}/{package}/";
        }

        /// <summary>
        /// Делит имя версии на команду и пакет.
        /// <para>
        /// Имя устроено как <c>Команда-Пакет-1.2.3</c>, и дефисы бывают внутри каждой
        /// части: «Lart_Iste-PeakFriendsEdition-1.8.13» — три куска, а
        /// «vcMoo-Moo_Modpack-1.9.9» — тоже три, но с подчёркиванием внутри пакета.
        /// Поэтому режем от краёв: первый дефис отделяет команду, последний — версию.
        /// </para>
        /// </summary>
        /// <param name="version">Имя версии с сервера.</param>
        /// <returns>Команда и пакет либо null.</returns>
        private static (string Team, string Package)? Split(string? version) {
            var name = (version ?? string.Empty).Trim();
            var first = name.IndexOf('-');
            var last = name.LastIndexOf('-');
            if (first <= 0 || last <= first + 1) {
                return null;
            }

            return (name.Substring(0, first), name.Substring(first + 1, last - first - 1));
        }
    }
}
