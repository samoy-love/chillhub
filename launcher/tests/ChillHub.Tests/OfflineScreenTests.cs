// <copyright file="OfflineScreenTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Home;
    using ChillHub.Core.Net;
    using ChillHub.Core.SelfUpdate;

    using Xunit;

    /// <summary>
    /// Главный экран, когда показывать нечего.
    /// <para>
    /// Проверяется машиной по той же причине, что <see cref="BottomBarLook"/>: ошибка
    /// здесь не падает исключением, а молча оставляет игроку экран, выглядящий сломанным.
    /// Ровно так и было — пропавший интернет гасил список игр, а витрина продолжала звать
    /// «Выберите игру» и держала кнопки к игре, которой на экране нет.
    /// </para>
    /// </summary>
    public class OfflineScreenTests {
        private static readonly OfflineText Reason = OfflineMessage.Describe(OfflineKind.NoInternet);

        /// <summary>Связь в порядке — экран живёт обычной жизнью, причину звать неоткуда.</summary>
        [Fact]
        public void СоСвязьюВитринаОстаётсяОбычной() {
            var view = OfflineScreen.Decide(null, gameSelected: false);

            Assert.False(view.HeroExplains);
            Assert.True(view.ActionsVisible);
            Assert.Equal(OfflineScreen.NoNewsTitle, view.GameNews.Title);
            Assert.Equal(OfflineScreen.NoLauncherNewsHint, view.LauncherNews.Hint);
        }

        /// <summary>Без связи и без выбранной игры витрина говорит за себя.</summary>
        [Fact]
        public void БезСвязиВитринаНазываетПричину() {
            var view = OfflineScreen.Decide(Reason, gameSelected: false);

            Assert.True(view.HeroExplains);
            Assert.Equal(Reason.Title, view.HeroTitle);
            Assert.Equal(Reason.Hint, view.HeroHint);
        }

        /// <summary>
        /// Кнопки запуска и «Об игре» при этом уходят: «Об игре» открывала пустую
        /// страницу, а «Повторить» стояла второй такой же рядом с настоящей в списке слева.
        /// </summary>
        [Fact]
        public void БезСвязиКнопокКНесуществующейИгреНет() {
            Assert.False(OfflineScreen.Decide(Reason, gameSelected: false).ActionsVisible);
        }

        /// <summary>
        /// Выбранная игра важнее любой аварии: её витрина и кнопка запуска остаются на
        /// месте, даже если новости в этот момент не пришли. Установленную игру можно
        /// запускать без всякого сервера, и прятать «Играть» из-за упавшей ленты нельзя.
        /// </summary>
        [Fact]
        public void ВыбраннаяИграОстаётсяСоСвоейКнопкой() {
            var view = OfflineScreen.Decide(Reason, gameSelected: true);

            Assert.False(view.HeroExplains);
            Assert.True(view.ActionsVisible);
        }

        /// <summary>
        /// Причина у пустоты одна на весь экран: список игр и обе ленты называют её
        /// одинаково. Разные слова про одно и то же читаются как разные поломки.
        /// </summary>
        [Fact]
        public void ПричинаОдинаковаВоВсехУглахЭкрана() {
            var view = OfflineScreen.Decide(Reason, gameSelected: false);

            Assert.Equal(Reason.Title, view.Games.Title);
            Assert.Equal(Reason.Title, view.GameNews.Title);
            Assert.Equal(Reason.Title, view.LauncherNews.Title);
            Assert.Equal(Reason.Hint, view.Games.Hint);
            Assert.Equal(Reason.Hint, view.GameNews.Hint);
            Assert.Equal(Reason.Hint, view.LauncherNews.Hint);
        }

        /// <summary>
        /// А пустая лента при работающей связи — это «новостей пока нет», а не авария.
        /// Подписи у лент разные: у игры это её объявления, у лаунчера — его собственные.
        /// </summary>
        [Fact]
        public void ПустаяЛентаСоСвязьюНеВыглядитПоломкой() {
            var game = OfflineScreen.GameNewsCaption(null);
            var launcher = OfflineScreen.LauncherNewsCaption(null);

            Assert.Equal(OfflineScreen.NoNewsTitle, game.Title);
            Assert.Equal(OfflineScreen.NoNewsTitle, launcher.Title);
            Assert.NotEqual(game.Hint, launcher.Hint);
            Assert.DoesNotContain("интернет", game.Hint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("сервер", launcher.Hint, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Пустой список игр со связью тоже не жалуется на связь.</summary>
        [Fact]
        public void ПустойСписокИгрСоСвязьюНеЖалуетсяНаСеть() {
            var caption = OfflineScreen.GamesCaption(null);

            Assert.Equal(OfflineScreen.NoGamesTitle, caption.Title);
            Assert.DoesNotContain("связ", caption.Hint, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Полоса в окне самообновления показывает работу, а не занимает место: пустая
        /// дорожка под сообщением об ошибке читается как загрузка, застрявшая на нуле.
        /// </summary>
        [Theory]
        [InlineData(false, 0, false)]
        [InlineData(true, 0, true)]
        [InlineData(false, 42, true)]
        [InlineData(false, 100, true)]
        public void ПолосаПрогрессаВиднаТолькоЗаРаботой(bool indeterminate, double value, bool visible) {
            Assert.Equal(visible, SelfUpdateProgressBar.Visible(indeterminate, value));
        }
    }
}
