// <copyright file="LaunchButtonsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.Linq;

    using ChillHub.Core;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Строка действий витрины: какие кнопки запуска на ней стоят и что уходит под
    /// стрелку.
    /// <para>
    /// Ошибка здесь молча врёт игроку о том, что запустится по нажатию: кнопка
    /// «Steam · с модами» под игрой, которой в Steam нет, — обещание, которого
    /// лаунчер не выполнит.
    /// </para>
    /// </summary>
    public class LaunchButtonsTests {
        /// <summary>Обе копии готовы — на витрине две кнопки, обычной «Играть» нет.</summary>
        [Fact]
        public void ДвеГотовыеКопииДаютДвеКнопки() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, All(), remembered: null);

            Assert.False(view.ActionVisible);
            Assert.True(view.MenuVisible);
            Assert.Equal(
                new[] { LaunchTarget.SteamModded, LaunchTarget.LocalModded },
                view.Buttons.Select(b => b.Target));
            Assert.Equal(new[] { "Steam", "Пиратка" }, view.Buttons.Select(b => b.Title));
            Assert.All(view.Buttons, b => Assert.Equal("с модами", b.Subtitle));
        }

        /// <summary>
        /// НЕДОСТУПНЫЙ ВАРИАНТ НЕ СТАНОВИТСЯ СЕРОЙ КНОПКОЙ. Копии в Steam нет — кнопки
        /// нет, а причина ждёт под стрелкой: выключенный прямоугольник не объясняет
        /// ничего, «Steam не установлен» объясняет.
        /// </summary>
        [Fact]
        public void НедоступныйВариантУходитПодСтрелкуСПричиной() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Unavailable, "Steam не установлен"),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Unavailable, "Steam не установлен"),
                Option(LaunchTarget.LocalModded, LaunchAction.Play),
                Option(LaunchTarget.LocalVanilla, LaunchAction.Play),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: true, options, remembered: null);

            Assert.Equal(LaunchTarget.LocalModded, Assert.Single(view.Buttons).Target);

            var menu = LaunchButtons.MenuOptions(options, view.Buttons);
            Assert.Equal(3, menu.Count);
            Assert.Contains(menu, o => o.Target == LaunchTarget.SteamModded && o.Note == "Steam не установлен");
        }

        /// <summary>
        /// У игры без сборки на сервере кнопка одна: обещать «Пиратку», которой негде
        /// взяться, значит обещать несуществующее.
        /// </summary>
        [Fact]
        public void БезСборкиНаСервереКнопкаОдна() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Play),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Play),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: true, options, remembered: null);

            Assert.Equal(LaunchTarget.SteamModded, Assert.Single(view.Buttons).Target);
            Assert.Single(LaunchButtons.MenuOptions(options, view.Buttons));
        }

        /// <summary>
        /// Модов в копии нет — кнопка остаётся и называет своё нажатие: «установить
        /// моды». Один щелчок доводит до игры, поэтому прятать вариант не за что.
        /// </summary>
        [Fact]
        public void КнопкаНазываетДействиеАНеСостояние() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.InstallMods, "установить моды"),
            };

            var button = Assert.Single(LaunchButtons.Compute(Pack(), playMode: true, options, null).Buttons);

            Assert.Equal("установить моды", button.Subtitle);
            Assert.Contains("установить моды", button.Tooltip, System.StringComparison.Ordinal);
        }

        /// <summary>Последний запущенный вариант красится акцентом — и только он.</summary>
        [Fact]
        public void АкцентТолькоУПрошлогоЗапуска() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, All(), LaunchTarget.LocalModded);

            Assert.Equal(LaunchTarget.LocalModded, Assert.Single(view.Buttons, b => b.Accent).Target);
        }

        /// <summary>
        /// Запомнили запуск без модов — акцента на витрине нет: обе кнопки запускают
        /// не то, что игрок выбирал в прошлый раз, и подсвечивать одну из них значило
        /// бы соврать.
        /// </summary>
        [Fact]
        public void ЗапомненныйВариантБезМодовАкцентаНеДаёт() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, All(), LaunchTarget.SteamVanilla);

            Assert.DoesNotContain(view.Buttons, b => b.Accent);
        }

        /// <summary>
        /// Вне режима «Играть» кнопок запуска нет: пока игра качается или
        /// проверяется, запускать нечего.
        /// </summary>
        [Fact]
        public void ВнеРежимаИгратьКнопокЗапускаНет() {
            var view = LaunchButtons.Compute(Pack(), playMode: false, All(), remembered: null);

            Assert.Empty(view.Buttons);
            Assert.True(view.ActionVisible);
            Assert.False(view.MenuVisible);
        }

        /// <summary>У игры без модов витрина остаётся прежней: одна кнопка действия.</summary>
        [Fact]
        public void БезМодовВитринаПрежняя() {
            Assert.True(LaunchButtons.Compute(null, playMode: true, All(), null).ActionVisible);
            Assert.False(LaunchButtons.Compute(new ModsInfo(), playMode: true, All(), null).MenuVisible);
        }

        /// <summary>
        /// Сыграть с модами прямо сейчас нельзя ни одной копией — витрина возвращается
        /// к «Играть» со стрелкой: варианты без модов и причины никуда не делись.
        /// </summary>
        [Fact]
        public void БезДоступныхМодовОстаётсяКнопкаДействия() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Unavailable, "модпак ещё не опубликован"),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Play),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: true, options, remembered: null);

            Assert.Empty(view.Buttons);
            Assert.True(view.ActionVisible);
            Assert.True(view.MenuVisible);
            Assert.Equal(2, LaunchButtons.MenuOptions(options, view.Buttons).Count);
        }

        /// <summary>Подсказка стрелки перечисляет то, что под ней лежит.</summary>
        [Fact]
        public void ПодсказкаСтрелкиПеречисляетСпрятанное() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, All(), remembered: null);

            Assert.Contains("Steam · без модов", view.MenuTooltip, System.StringComparison.Ordinal);
            Assert.Contains("Пиратка · без модов", view.MenuTooltip, System.StringComparison.Ordinal);
        }

        private static List<LaunchOption> All() => new() {
            Option(LaunchTarget.SteamModded, LaunchAction.Play),
            Option(LaunchTarget.SteamVanilla, LaunchAction.Play),
            Option(LaunchTarget.LocalModded, LaunchAction.Play),
            Option(LaunchTarget.LocalVanilla, LaunchAction.Play),
        };

        private static LaunchOption Option(LaunchTarget target, LaunchAction action, string note = "")
            => new(
                target,
                ModsLaunch.TitleOf(target, null),
                target is LaunchTarget.SteamModded or LaunchTarget.SteamVanilla ? @"C:\steam\game" : @"C:\games\g",
                target is LaunchTarget.SteamModded or LaunchTarget.LocalModded,
                action,
                note);

        private static ModsInfo Pack() => new() {
            HasLatest = true,
            SteamAppId = "1966720",
            DisplayName = "Lethal Reloaded",
            DisplayVersion = "2.2.12",
        };
    }
}
