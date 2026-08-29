// <copyright file="GameMenuItemsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Что стоит в контекстном меню строки списка игр.
    /// <para>
    /// ПУНКТ, КОТОРЫЙ НИЧЕГО НЕ СДЕЛАЕТ, ХУЖЕ ОТСУТСТВУЮЩЕГО. У установленной и свежей
    /// игры качать нечего, и «Добавить в очередь загрузок» молча отвечал отказом.
    /// </para>
    /// </summary>
    public class GameMenuItemsTests {
        /// <summary>Установленной и свежей игре — проверка файлов вместо очереди загрузок.</summary>
        [Fact]
        public void УстановленнойИСвежейИгреПредлагаютПроверку() {
            var game = Game(installed: true, needsUpdate: false);

            Assert.True(Look(GameMenuItems.Verify, game).Visible);
            Assert.False(Look(GameMenuItems.Enqueue, game).Visible);
        }

        /// <summary>
        /// Неустановленной и требующей обновления — наоборот: проверять ещё нечего,
        /// а качать есть что.
        /// </summary>
        [Theory]
        [InlineData(false, false)] // не установлена
        [InlineData(true, true)]   // установлена, но нужно обновление
        public void ОстальнымИграмПредлагаютОчередьЗагрузок(bool installed, bool needsUpdate) {
            var game = Game(installed, needsUpdate);

            Assert.True(Look(GameMenuItems.Enqueue, game).Visible);
            Assert.False(Look(GameMenuItems.Verify, game).Visible);
        }

        /// <summary>Про игру, которой нет, сказать нечего — проверку не предлагаем.</summary>
        [Fact]
        public void БезИгрыПроверкуНеПредлагают() {
            Assert.False(GameMenuItems.ShowsVerify(null));
            Assert.True(Look(GameMenuItems.Enqueue, null).Visible);
        }

        /// <summary>Остальные пункты меню видны всегда: их видимость от состояния не зависит.</summary>
        [Fact]
        public void ПрочиеПунктыВидныВсегда() {
            Assert.True(Look("OpenFolderMenuItem", Game(installed: true, needsUpdate: false)).Visible);
            Assert.True(Look(null, Game(installed: false, needsUpdate: false)).Visible);
        }

        /// <summary>
        /// Работа с файлами доступна, только когда файлы есть. А первый пункт —
        /// «Подробнее об игре» — живой всегда: страница игры полезна и до установки.
        /// </summary>
        [Fact]
        public void ПунктыПоФайламЖивутТолькоПриФайлахКромеПервого() {
            var game = Game(installed: false, needsUpdate: false);

            Assert.False(GameMenuItems.For("OpenFolderMenuItem", isFirst: false, game, hasFiles: false).Enabled);
            Assert.True(GameMenuItems.For("OpenFolderMenuItem", isFirst: false, game, hasFiles: true).Enabled);
            Assert.True(GameMenuItems.For("DetailsMenuItem", isFirst: true, game, hasFiles: false).Enabled);
        }

        /// <summary>
        /// Всё меню целиком: у установленной и свежей игры видна проверка, у остальных —
        /// очередь загрузок, а первый пункт живой всегда.
        /// </summary>
        [Fact]
        public void МенюОдеваетсяЦеликом() {
            UiThread.Run(() => {
                var menu = Menu();

                GameMenuItems.Apply(menu.Items, Game(installed: true, needsUpdate: false), hasFiles: false);

                Assert.Equal(Visibility.Visible, Item(menu, 0).Visibility);
                Assert.True(Item(menu, 0).IsEnabled, "первый пункт живой всегда");

                Assert.Equal(Visibility.Collapsed, Item(menu, 1).Visibility); // очередь
                Assert.Equal(Visibility.Visible, Item(menu, 2).Visibility);   // проверка

                // Файлов на диске нет — работа с ними недоступна.
                Assert.False(Item(menu, 2).IsEnabled);
                Assert.False(Item(menu, 3).IsEnabled);
            });
        }

        /// <summary>Игре, которую надо качать, видна очередь загрузок, а не проверка.</summary>
        [Fact]
        public void НеустановленнойИгреМенюПоказываетОчередь() {
            UiThread.Run(() => {
                var menu = Menu();

                GameMenuItems.Apply(menu.Items, Game(installed: false, needsUpdate: false), hasFiles: true);

                Assert.Equal(Visibility.Visible, Item(menu, 1).Visibility);
                Assert.Equal(Visibility.Collapsed, Item(menu, 2).Visibility);
                Assert.True(Item(menu, 3).IsEnabled, "файлы есть — работа с ними доступна");
            });
        }

        /// <summary>Меню ещё не собрано — одевать нечего, и падать не из-за чего.</summary>
        [Fact]
        public void БезМенюОдеваниеМолчит() {
            GameMenuItems.Apply(null, Game(installed: true, needsUpdate: false), hasFiles: true);
        }

        /// <summary>
        /// Игра берётся из CommandParameter, а нет его — из контекста строки. Порядок
        /// важен: параметр задан в разметке явно, а контекст может оказаться чужим.
        /// </summary>
        [Fact]
        public void ИграБерётсяИзПараметраПотомИзКонтекста() {
            UiThread.Run(() => {
                var game = Game(installed: true, needsUpdate: false);
                var other = new GameInfo { GameId = "peak", Title = "PEAK" };

                var byParameter = new MenuItem { CommandParameter = game, DataContext = other };
                Assert.Same(game, GameMenuItems.GameOf(byParameter));

                var byContext = new MenuItem { DataContext = game };
                Assert.Same(game, GameMenuItems.GameOf(byContext));

                Assert.Null(GameMenuItems.GameOf(new MenuItem()));
                Assert.Null(GameMenuItems.GameOf(null));
            });
        }

        /// <summary>
        /// Отказ очереди беззвучен — его называют словами. Причина у проверки и у
        /// закачки разная, и обе стоит назвать: «не удалось» не подсказывает, что делать.
        /// </summary>
        [Fact]
        public void ОтказОчередиНазываетПричину() {
            Assert.Equal(
                "«R.E.P.O.» уже проверяется или ещё не установлена.",
                ChillHub.Core.Game.QueueRefusal.For(ChillHub.Core.Game.QueueTaskKind.Verify, "R.E.P.O."));
            Assert.Equal(
                "«R.E.P.O.» уже установлена или уже в очереди.",
                ChillHub.Core.Game.QueueRefusal.For(ChillHub.Core.Game.QueueTaskKind.Download, "R.E.P.O."));

            // Игра без названия бывает: список пришёл, а заголовка в нём нет.
            Assert.StartsWith(
                "Игра ",
                ChillHub.Core.Game.QueueRefusal.For(ChillHub.Core.Game.QueueTaskKind.Verify, null));
        }

        /// <summary>Меню строки списка: тот же порядок пунктов, что и в разметке.</summary>
        private static ContextMenu Menu() {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Name = "DetailsMenuItem" });
            menu.Items.Add(new MenuItem { Name = GameMenuItems.Enqueue });
            menu.Items.Add(new MenuItem { Name = GameMenuItems.Verify });
            menu.Items.Add(new MenuItem { Name = "OpenFolderMenuItem" });

            // Разделитель между пунктами — не MenuItem: одевание обязано его пропустить.
            menu.Items.Add(new Separator());
            return menu;
        }

        private static MenuItem Item(ContextMenu menu, int index) => (MenuItem)menu.Items[index]!;

        private static GameMenuItemLook Look(string? name, GameInfo? game)
            => GameMenuItems.For(name, isFirst: false, game, hasFiles: true);

        private static GameInfo Game(bool installed, bool needsUpdate) => new GameInfo {
            GameId = "repo",
            Title = "R.E.P.O.",
            IsInstalled = installed,
            NeedsUpdate = needsUpdate,
        };
    }
}
