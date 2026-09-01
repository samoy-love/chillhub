// <copyright file="OfflineScreen.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using ChillHub.Core.Net;

    /// <summary>
    /// Подпись пустого раздела.
    /// </summary>
    /// <param name="Title">Заголовок — одна строка, без точки.</param>
    /// <param name="Hint">Строка под заголовком: почему пусто и что делать.</param>
    internal readonly record struct EmptyCaption(string Title, string Hint);

    /// <summary>
    /// Что показывает главный экран, пока показывать нечего.
    /// </summary>
    /// <param name="Games">Подпись пустого списка игр.</param>
    /// <param name="GameNews">Подпись пустой ленты новостей игры.</param>
    /// <param name="LauncherNews">Подпись пустой ленты новостей лаунчера.</param>
    /// <param name="HeroExplains">Витрина называет причину вместо «Выберите игру».</param>
    /// <param name="HeroTitle">Заголовок витрины, когда она называет причину.</param>
    /// <param name="HeroHint">Строка под заголовком витрины.</param>
    /// <param name="ActionsVisible">Показывать кнопки запуска и установки.</param>
    internal readonly record struct OfflineScreenView(
        EmptyCaption Games,
        EmptyCaption GameNews,
        EmptyCaption LauncherNews,
        bool HeroExplains,
        string HeroTitle,
        string HeroHint,
        bool ActionsVisible);

    /// <summary>
    /// Решение «что стоит на экране, когда связи нет», отделённое от самого экрана.
    /// <para>
    /// Причина та же, что у <see cref="ActionButtonState"/> и <see cref="BottomBarLook"/>:
    /// внутри страницы WPF это код, который проверяется только глазами, а ошибка здесь не
    /// падает исключением — она молча оставляет игроку экран, выглядящий сломанным.
    /// Ровно так и было: пропавший интернет гасил список игр, но витрина продолжала звать
    /// «Выберите игру» и держала кнопки к игре, которой на экране нет, а обе ленты новостей
    /// оставались чёрной пустотой без единого слова.
    /// </para>
    /// <para>
    /// Решение одно на весь экран: причина у пустоты общая, и называться в разных углах
    /// она обязана одинаково. Разные слова про одно и то же читаются как разные поломки.
    /// </para>
    /// </summary>
    internal static class OfflineScreen {
        /// <summary>Заголовок ленты, в которой новостей просто нет.</summary>
        internal const string NoNewsTitle = "Пока новостей нет";

        /// <summary>Строка под ним в ленте игры.</summary>
        internal const string NoGameNewsHint = "Здесь появятся объявления и события этой игры.";

        /// <summary>Строка под ним в ленте лаунчера.</summary>
        internal const string NoLauncherNewsHint = "Здесь появятся новости о самом лаунчере.";

        /// <summary>Заголовок пустого списка игр, когда связь есть, а игр нет.</summary>
        internal const string NoGamesTitle = "Игр пока нет";

        /// <summary>Строка под ним.</summary>
        internal const string NoGamesHint = "Здесь появятся игры, доступные для установки.";

        /// <summary>
        /// Считает, что показать вместо игр и новостей.
        /// </summary>
        /// <param name="offline">Причина, если связи нет; null — связь в порядке.</param>
        /// <param name="gameSelected">В списке выбрана игра.</param>
        /// <returns>Подписи и видимость.</returns>
        internal static OfflineScreenView Decide(OfflineText? offline, bool gameSelected) {
            // Витрина говорит за себя только когда ей нечего показать: выбранная игра
            // важнее любой аварии — её состояние и кнопка запуска остаются на месте,
            // даже если новости в этот момент не пришли.
            var explains = !gameSelected && offline is not null;
            var text = offline ?? default;

            // Кнопка, которая ничего не сделает, хуже её отсутствия: «Повторить» в витрине
            // стояла второй такой же рядом с настоящей в списке слева, а «Об игре»
            // открывала пустую страницу.
            return new OfflineScreenView(
                Games: GamesCaption(offline),
                GameNews: GameNewsCaption(offline),
                LauncherNews: LauncherNewsCaption(offline),
                HeroExplains: explains,
                HeroTitle: explains ? text.Title : string.Empty,
                HeroHint: explains ? text.Hint : string.Empty,
                ActionsVisible: !explains);
        }

        /// <summary>Подпись пустого списка игр.</summary>
        /// <param name="offline">Причина, если связи нет.</param>
        /// <returns>Заголовок и строка под ним.</returns>
        internal static EmptyCaption GamesCaption(OfflineText? offline) =>
            offline is OfflineText text
                ? new EmptyCaption(text.Title, text.Hint)
                : new EmptyCaption(NoGamesTitle, NoGamesHint);

        /// <summary>Подпись пустой ленты новостей игры.</summary>
        /// <param name="offline">Причина, если связи нет.</param>
        /// <returns>Заголовок и строка под ним.</returns>
        internal static EmptyCaption GameNewsCaption(OfflineText? offline) =>
            offline is OfflineText text
                ? new EmptyCaption(text.Title, text.Hint)
                : new EmptyCaption(NoNewsTitle, NoGameNewsHint);

        /// <summary>Подпись пустой ленты новостей лаунчера.</summary>
        /// <param name="offline">Причина, если связи нет.</param>
        /// <returns>Заголовок и строка под ним.</returns>
        internal static EmptyCaption LauncherNewsCaption(OfflineText? offline) =>
            offline is OfflineText text
                ? new EmptyCaption(text.Title, text.Hint)
                : new EmptyCaption(NoNewsTitle, NoLauncherNewsHint);
    }
}
