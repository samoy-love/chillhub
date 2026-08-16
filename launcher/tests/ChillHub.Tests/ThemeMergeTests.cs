// <copyright file="ThemeMergeTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Подключение словаря темы при сохранении конфига.
    /// <para>
    /// ApplyTheme зовётся на каждом сохранении — то есть на каждом шаге ползунка потоков в
    /// настройках. Пересборка словаря ресурсов перестраивает шаблоны всех элементов окна:
    /// у ползунка появлялся новый бегунок посреди перетаскивания, и мышь его теряла —
    /// дальше одного деления сдвинуть не получалось. Поэтому уже подключённую тему
    /// трогать нельзя.
    /// </para>
    /// </summary>
    public class ThemeMergeTests {
        private const string Dark = "/ChillHub;component/" + ConfigService.ThemePath;

        /// <summary>Тема уже подключена — ничего не убираем и не добавляем.</summary>
        [Fact]
        public void ПодключённаяТемаНеПереподключается() {
            var (remove, add) = ConfigService.PlanThemeMerge(new[] { "/ChillHub;component/Styles/Common.xaml", Dark });

            Assert.Empty(remove);
            Assert.False(add);
        }

        /// <summary>Темы ещё нет — добавляем, остальные словари не трогаем.</summary>
        [Fact]
        public void БезТемыОнаДобавляется() {
            var (remove, add) = ConfigService.PlanThemeMerge(new[] { "/ChillHub;component/Styles/Common.xaml" });

            Assert.Empty(remove);
            Assert.True(add);
        }

        /// <summary>Чужая тема из старой версии убирается, наша добавляется; индексы — по убыванию, чтобы удалять с конца.</summary>
        [Fact]
        public void ЧужиеТемыУбираютсяСКонца() {
            var (remove, add) = ConfigService.PlanThemeMerge(new[] {
                "/ChillHub;component/Themes/Theme.Light.xaml",
                "/ChillHub;component/Styles/Common.xaml",
                "/ChillHub;component/themes/Theme.Old.xaml",
            });

            Assert.Equal(new[] { 2, 0 }, remove);
            Assert.True(add);
        }

        /// <summary>Чужая тема рядом с нашей — убирается только чужая.</summary>
        [Fact]
        public void РядомСНашейТемойЧужаяУбираетсяАНашаОстаётся() {
            var (remove, add) = ConfigService.PlanThemeMerge(new[] { Dark, "/ChillHub;component/Themes/Theme.Light.xaml", null! });

            Assert.Equal(new[] { 1 }, remove);
            Assert.False(add);
        }
    }
}
