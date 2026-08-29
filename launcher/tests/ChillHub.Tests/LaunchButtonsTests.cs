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
            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, All(), remembered: null);

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

            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, options, remembered: null);

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

            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, options, remembered: null);

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

            var button = Assert.Single(LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, options, null).Buttons);

            Assert.Equal("установить моды", button.Subtitle);
            Assert.Contains("установить моды", button.Tooltip, System.StringComparison.Ordinal);
        }

        /// <summary>Последний запущенный вариант красится акцентом — и только он.</summary>
        [Fact]
        public void АкцентТолькоУПрошлогоЗапуска() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, All(), LaunchTarget.LocalModded);

            Assert.Equal(LaunchTarget.LocalModded, Assert.Single(view.Buttons, b => b.Accent).Target);
        }

        /// <summary>
        /// Запомнили запуск без модов — акцента на витрине нет: обе кнопки запускают
        /// не то, что игрок выбирал в прошлый раз, и подсвечивать одну из них значило
        /// бы соврать.
        /// </summary>
        [Fact]
        public void ЗапомненныйВариантБезМодовАкцентаНеДаёт() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, All(), LaunchTarget.SteamVanilla);

            Assert.DoesNotContain(view.Buttons, b => b.Accent);
        }

        /// <summary>
        /// Рядом с «Установить» кнопка запуска акцента не носит: залитых кнопок в ряду
        /// должно быть не больше одной, иначе неясно, какая из них главная.
        /// </summary>
        [Fact]
        public void РядомСКнопкойДействияАкцентаНет() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Play),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Play),
                Option(LaunchTarget.LocalModded, LaunchAction.InstallGame, "установить игру с модами"),
            };

            var view = LaunchButtons.Compute(
                Pack(), playMode: false, steamAllowed: true, options, LaunchTarget.SteamModded);

            Assert.True(view.ActionVisible);
            Assert.DoesNotContain(view.Buttons, b => b.Accent);
            Assert.Equal("Style.LaunchButton.Ghost", Assert.Single(view.Buttons).StyleKey);
        }

        /// <summary>У игры без модов витрина остаётся прежней: одна кнопка действия.</summary>
        [Fact]
        public void БезМодовВитринаПрежняя() {
            Assert.True(LaunchButtons.Compute(null, playMode: true, steamAllowed: false, All(), null).ActionVisible);
            Assert.False(LaunchButtons.Compute(new ModsInfo(), playMode: true, steamAllowed: false, All(), null).MenuVisible);
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

            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, options, remembered: null);

            Assert.Empty(view.Buttons);
            Assert.True(view.ActionVisible);
            Assert.True(view.MenuVisible);
            Assert.Equal(2, LaunchButtons.MenuOptions(options, view.Buttons).Count);
        }

        /// <summary>Подсказка стрелки перечисляет то, что под ней лежит.</summary>
        [Fact]
        public void ПодсказкаСтрелкиПеречисляетСпрятанное() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, All(), remembered: null);

            Assert.Contains("Steam · без модов", view.MenuTooltip, System.StringComparison.Ordinal);
            Assert.Contains("Пиратка · без модов", view.MenuTooltip, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// СБОРКА С СЕРВЕРА НЕ УСЛОВИЕ ДЛЯ МОДОВ В STEAM. Игра ещё не скачана, на
        /// витрине «Установить» — и рядом с ней стоит «Steam · с модами»: моды лягут
        /// в чужую папку Steam, десять гигабайт сборки для этого не нужны.
        /// </summary>
        [Fact]
        public void БезСборкиНаДискеSteamВсёРавноПредлагается() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.InstallMods, "установить моды"),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Play),
                Option(LaunchTarget.LocalModded, LaunchAction.InstallGame, "установить игру с модами"),
                Option(LaunchTarget.LocalVanilla, LaunchAction.InstallGame, "установить игру"),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: false, steamAllowed: true, options, null);

            var button = Assert.Single(view.Buttons);
            Assert.Equal(LaunchTarget.SteamModded, button.Target);
            Assert.Equal("установить моды", button.Subtitle);

            // «Установить» остаётся на месте: она про сборку, а не про моды.
            Assert.True(view.ActionVisible);
            Assert.True(view.MenuVisible);
        }

        /// <summary>
        /// Вне режима «Играть» сборки с сервера на витрине нет: её кнопка — это
        /// «Установить»/«Обновить» слева, и второй такой же рядом быть не должно.
        /// </summary>
        [Fact]
        public void ВнеРежимаИгратьПираткаКнопкойНеСтановится() {
            var view = LaunchButtons.Compute(Pack(), playMode: false, steamAllowed: true, All(), null);

            Assert.Equal(LaunchTarget.SteamModded, Assert.Single(view.Buttons).Target);
            Assert.Contains(
                LaunchButtons.MenuOptions(All(), view.Buttons),
                o => o.Target == LaunchTarget.LocalModded);
        }

        /// <summary>
        /// Копии в Steam нет — вне режима «Играть» витрина не меняется вовсе: одна
        /// кнопка действия, и никаких обещаний.
        /// </summary>
        [Fact]
        public void БезКопииВSteamВнеИгрыВитринаНеМеняется() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Unavailable, "Steam не установлен"),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Unavailable, "Steam не установлен"),
                Option(LaunchTarget.LocalModded, LaunchAction.InstallGame, "установить игру с модами"),
                Option(LaunchTarget.LocalVanilla, LaunchAction.InstallGame, "установить игру"),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: false, steamAllowed: true, options, null);

            Assert.Empty(view.Buttons);
            Assert.True(view.ActionVisible);
        }

        /// <summary>
        /// Идёт закачка, удаление или проверка — кнопок запуска нет ни одной, даже
        /// Steam: в этот момент лаунчер занят игрой, а не её запуском.
        /// </summary>
        [Fact]
        public void ПокаИдётРаботаКнопокЗапускаНет() {
            var view = LaunchButtons.Compute(Pack(), playMode: false, steamAllowed: false, All(), null);

            Assert.Empty(view.Buttons);
            Assert.True(view.ActionVisible);
            Assert.False(view.MenuVisible);
        }

        /// <summary>Акцент и «стекло» — разные стили; решает это модель, а не разметка.</summary>
        [Fact]
        public void СтильКнопкиИдётЗаАкцентом() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, All(), LaunchTarget.SteamModded);

            Assert.Equal("Style.LaunchButton.Accent", view.Buttons[0].StyleKey);
            Assert.Equal("Style.LaunchButton.Ghost", view.Buttons[1].StyleKey);
        }

        /// <summary>Нажали кнопку, вариант всё ещё доступен — запускаем его.</summary>
        [Fact]
        public void ДоступныйВариантЗапускаетсяПоНажатию() {
            var chosen = LaunchButtons.Chosen(All(), LaunchTarget.LocalModded);

            Assert.NotNull(chosen.Option);
            Assert.Equal(LaunchTarget.LocalModded, chosen.Option!.Target);
            Assert.Empty(chosen.Message);
        }

        /// <summary>
        /// ИГРУ УДАЛИЛИ ИЗ STEAM МЕЖДУ ОТРИСОВКОЙ КНОПКИ И ЩЕЛЧКОМ. Запускать вместо неё
        /// что-то другое нельзя, молчать — тоже: нажатие обязано объясниться словами.
        /// </summary>
        [Fact]
        public void ПропавшийВариантОбъясняетсяСловами() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Unavailable, "Steam не установлен"),
            };

            var chosen = LaunchButtons.Chosen(options, LaunchTarget.SteamModded);

            Assert.Null(chosen.Option);
            Assert.Equal("Steam не установлен", chosen.Message);
        }

        /// <summary>Причины нет — сообщение всё равно есть: пустая строка ничего не объясняет.</summary>
        [Fact]
        public void ИсчезнувшийВовсеВариантТожеОбъясняется() {
            var chosen = LaunchButtons.Chosen(new List<LaunchOption>(), LaunchTarget.SteamModded);

            Assert.Null(chosen.Option);
            Assert.NotEmpty(chosen.Message);
            Assert.NotEmpty(LaunchButtons.Chosen(null, LaunchTarget.SteamModded).Message);
        }

        /// <summary>
        /// ИГРА ТОЛЬКО ИЗ STEAM, И STEAM НА МЕСТЕ: витрину держит кнопка запуска, а
        /// выключенная «Нужна копия в Steam» рядом объясняла бы то, что уже решено
        /// соседом.
        /// </summary>
        [Fact]
        public void УИгрыБезСборкиВитринуДержитКнопкаЗапуска() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.InstallMods, "установить моды"),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Play),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: false, steamAllowed: true, options, null);

            Assert.Equal(LaunchTarget.SteamModded, Assert.Single(view.Buttons).Target);
            Assert.False(view.ActionVisible);
        }

        /// <summary>
        /// НИ STEAM, НИ СБОРКИ — ВИТРИНА ОБЯЗАНА ОБЪЯСНИТЬСЯ. Кнопок запуска нет, и
        /// кнопка действия остаётся единственным местом, где сказано, чего не хватает:
        /// пустой ряд читался бы как сломанный экран.
        /// </summary>
        [Fact]
        public void БезSteamИБезСборкиОстаётсяКнопкаДействия() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Unavailable, "Steam не установлен"),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Unavailable, "Steam не установлен"),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: false, steamAllowed: true, options, null);

            Assert.Empty(view.Buttons);
            Assert.True(view.ActionVisible);

            // Причина не пропадает: она ждёт под стрелкой, а не только в журнале.
            Assert.True(view.MenuVisible);
            Assert.Contains(
                LaunchButtons.MenuOptions(options, view.Buttons),
                o => o.Note == "Steam не установлен");
        }

        /// <summary>
        /// Сборка на сервере есть — «Установить» остаётся на месте: у неё своё дело, и
        /// кнопка запуска Steam-копии его не заменяет.
        /// </summary>
        [Fact]
        public void СоСборкойНаСервереКнопкаДействияОстаётся() {
            var options = new List<LaunchOption> {
                Option(LaunchTarget.SteamModded, LaunchAction.Play),
                Option(LaunchTarget.SteamVanilla, LaunchAction.Play),
                Option(LaunchTarget.LocalModded, LaunchAction.InstallGame, "установить игру с модами"),
                Option(LaunchTarget.LocalVanilla, LaunchAction.InstallGame, "установить игру"),
            };

            var view = LaunchButtons.Compute(Pack(), playMode: false, steamAllowed: true, options, null);

            Assert.Single(view.Buttons);
            Assert.True(view.ActionVisible);
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
