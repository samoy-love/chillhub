// <copyright file="GameMenuEnqueueTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.Maintenance;
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// «Добавить в очередь загрузок» в контекстном меню строки списка игр.
    /// <para>
    /// Правило «работа с файлами доступна только при файлах» стояло на всём меню сразу, и
    /// у игры, которую ещё ни разу не ставили, пункт был виден, но сер: единственный путь
    /// установки из списка игр не работал. Постановка в очередь файлы создаёт.
    /// </para>
    /// </summary>
    public class GameMenuEnqueueTests {
        /// <summary>У игры без единого файла на диске пункт обязан нажиматься.</summary>
        [Fact]
        public void ОчередьЗагрузокЖивётИБезФайловНаДиске() {
            var look = GameMenuItems.For(
                GameMenuItems.Enqueue, isFirst: false, NotInstalled(), hasFiles: false);

            Assert.True(look.Visible);
            Assert.True(look.Enabled);
        }

        /// <summary>Пункты, которые с файлами работают, без файлов по-прежнему выключены.</summary>
        [Fact]
        public void РаботаСФайламиБезФайловПоПрежнемуВыключена() {
            Assert.False(GameMenuItems.For(GameMenuItems.Verify, isFirst: false, NotInstalled(), hasFiles: false).Enabled);
            Assert.False(GameMenuItems.For(null, isFirst: false, NotInstalled(), hasFiles: false).Enabled);
        }

        /// <summary>
        /// То же на собранном меню: у неустановленной игры второй пункт («Добавить в
        /// очередь загрузок») виден и нажимается, а «Открыть расположение» — нет.
        /// </summary>
        [Fact]
        public void ВСобранномМенюОчередьНажимаетсяАПапкаНет() {
            UiThread.Run(() => {
                var menu = new ContextMenu();
                menu.Items.Add(new MenuItem { Name = "DetailsMenuItem" });
                menu.Items.Add(new MenuItem { Name = GameMenuItems.Enqueue });
                menu.Items.Add(new MenuItem { Name = GameMenuItems.Verify });
                menu.Items.Add(new MenuItem());

                GameMenuItems.Apply(menu.Items, NotInstalled(), hasFiles: false);

                Assert.True(((MenuItem)menu.Items[1]!).IsEnabled, "ставить в очередь можно и без файлов");
                Assert.False(((MenuItem)menu.Items[3]!).IsEnabled, "открывать нечего — файлов нет");
            });
        }

        /// <summary>
        /// Игры, которую качать неоткуда (живёт только копией из Steam), в очереди быть
        /// не может: <c>DownloadQueue.Enqueue</c> её отвергает. Серый пункт честнее, чем
        /// пункт, отвечающий молчаливым отказом.
        /// </summary>
        [Fact]
        public void БезСборкиНаСервереОчередьВыключена() {
            var steamOnly = NotInstalled();
            steamOnly.LatestVersion = string.Empty;

            Assert.False(GameMenuItems.For(GameMenuItems.Enqueue, isFirst: false, steamOnly, hasFiles: false).Enabled);
        }

        /// <summary>
        /// Во время технических работ меню не должно быть обходным путём к установке:
        /// кнопка действия на странице в этом режиме гаснет, и пункт обязан гаснуть тоже.
        /// </summary>
        [Fact]
        public void ВоВремяТехработУстановкаИзМенюНедоступна() {
            var blocked = new MaintenanceState {
                Enabled = true,
                Blocks = new MaintenanceBlocks { Install = true, Update = true, Launch = false },
            };

            Assert.False(
                GameMenuItems.For(GameMenuItems.Enqueue, isFirst: false, NotInstalled(), hasFiles: false, blocked).Enabled);
        }

        /// <summary>А когда техработы запрещают только запуск, ставить в очередь можно.</summary>
        [Fact]
        public void ТехработыЗапрещающиеТолькоЗапускОчередьНеТрогают() {
            var playOnly = new MaintenanceState {
                Enabled = true,
                Blocks = new MaintenanceBlocks { Install = false, Update = false, Launch = true },
            };

            Assert.True(
                GameMenuItems.For(GameMenuItems.Enqueue, isFirst: false, NotInstalled(), hasFiles: false, playOnly).Enabled);
        }

        private static GameInfo NotInstalled() => new GameInfo {
            GameId = "peak",
            Title = "PEAK",
            IsInstalled = false,
            NeedsUpdate = false,
            HasLatest = true,
            LatestVersion = "1.0.0",
        };
    }
}
