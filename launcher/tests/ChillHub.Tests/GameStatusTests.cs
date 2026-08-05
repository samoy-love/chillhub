// <copyright file="GameStatusTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Состояние игры в списке главного экрана: установлена ли, требуется ли докачка.
    /// <para>
    /// Ошибка здесь видна пользователю не как ошибка, а как неверная надпись на кнопке.
    /// «Установить» для уже стоящей игры означает лишнюю закачку сборки целиком,
    /// «Играть» для наполовину обновлённой — запуск смеси двух версий.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class GameStatusTests {
        /// <summary>Игра с маркером версии считается установленной, версия попадает в список.</summary>
        [Fact]
        public void МаркерВерсииДелаетИгруУстановленной() {
            using var games = new GamesPathScope();
            GameLocalState.WriteLocalVersion("game", "1.0.0");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", LatestVersion = "1.0.0" } };

            GameStatus.NormalizeIconsAndLocalState(list, "https://example.test");

            Assert.True(list[0].IsInstalled);
            Assert.Equal("1.0.0", list[0].InstalledVersion);
            Assert.False(list[0].NeedsUpdate);
        }

        /// <summary>Расхождение версий — это «нужно обновление», а не «нужно установить».</summary>
        [Fact]
        public void ОтличиеВерсийТребуетОбновления() {
            using var games = new GamesPathScope();
            GameLocalState.WriteLocalVersion("game", "1.0.0");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", LatestVersion = "1.1.0" } };

            GameStatus.NormalizeIconsAndLocalState(list, "https://example.test");

            Assert.True(list[0].IsInstalled);
            Assert.True(list[0].NeedsUpdate);
        }

        /// <summary>Без маркера игра не установлена и обновлять нечего.</summary>
        [Fact]
        public void БезМаркераИграНеУстановлена() {
            using var games = new GamesPathScope();
            var list = new List<GameInfo> { new GameInfo { GameId = "game", LatestVersion = "1.0.0" } };

            GameStatus.NormalizeIconsAndLocalState(list, "https://example.test");

            Assert.False(list[0].IsInstalled);
            Assert.False(list[0].NeedsUpdate);
        }

        /// <summary>
        /// След оборванного обновления перевешивает совпадение версий: маркер версии
        /// уже переписан, а файлы — смесь двух сборок, и играть в такое нельзя (C2).
        /// </summary>
        [Fact]
        public void ОборванноеОбновлениеТребуетВосстановления() {
            using var games = new GamesPathScope();
            GameLocalState.WriteLocalVersion("game", "1.0.0");
            var root = Path.Combine(games.Root, "game");
            File.WriteAllText(Path.Combine(root, SimpleSyncService.UpdateMarkerFileName), "version=1.0.0");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", LatestVersion = "1.0.0" } };

            GameStatus.NormalizeIconsAndLocalState(list, "https://example.test");

            Assert.True(list[0].NeedsUpdate);
        }

        /// <summary>
        /// Корнеотносительная иконка достраивается до полного адреса, абсолютную не трогаем:
        /// иначе к чужому https приклеился бы адрес нашего сервера и картинка не загрузилась бы.
        /// </summary>
        [Fact]
        public void КорнеотносительнаяИконкаДостраивается() {
            using var games = new GamesPathScope();
            var list = new List<GameInfo> {
                new GameInfo { GameId = "a", IconUrl = "/icons/a.png" },
                new GameInfo { GameId = "b", IconUrl = "https://cdn.test/b.png" },
                new GameInfo { GameId = "c", IconUrl = string.Empty },
            };

            GameStatus.NormalizeIconsAndLocalState(list, "https://example.test");

            Assert.Equal("https://example.test/icons/a.png", list[0].IconUrl);
            Assert.Equal("https://cdn.test/b.png", list[1].IconUrl);
            Assert.Equal(string.Empty, list[2].IconUrl);
        }

        /// <summary>
        /// Краевые пробелы в версии от сервера отбрасываются: «1.0.0 » не должно считаться
        /// другой версией и вызывать переустановку на ровном месте.
        /// </summary>
        [Fact]
        public void КраевыеПробелыВВерсииНеСчитаютсяРасхождением() {
            using var games = new GamesPathScope();
            GameLocalState.WriteLocalVersion("game", "1.0.0");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", LatestVersion = "  1.0.0  " } };

            GameStatus.NormalizeIconsAndLocalState(list, "https://example.test");

            Assert.Equal("1.0.0", list[0].LatestVersion);
            Assert.False(list[0].NeedsUpdate);
        }

        /// <summary>Пустой список и null не роняют нормализацию — она зовётся и до первой загрузки.</summary>
        [Fact]
        public void ПустойСписокБезопасен() {
            using var games = new GamesPathScope();

            GameStatus.NormalizeIconsAndLocalState(new List<GameInfo>(), "https://example.test");
            GameStatus.NormalizeIconsAndLocalState(null!, "https://example.test");
        }

        /// <summary>После установки игра помечена установленной в применённой версии.</summary>
        [Fact]
        public void ПослеУстановкиИграСчитаетсяСвежей() {
            var g = new GameInfo { GameId = "game", LatestVersion = "1.2.0" };

            GameStatus.MarkInstalled(g, " 1.2.0 ");

            Assert.True(g.IsInstalled);
            Assert.Equal("1.2.0", g.InstalledVersion);
            Assert.False(g.NeedsUpdate);
        }

        /// <summary>
        /// Пока шла установка, сервер мог выпустить новую сборку: тогда игра сразу
        /// помечается требующей обновления, а не «свежей до следующей проверки».
        /// </summary>
        [Fact]
        public void УстановкаУстаревшейВерсииСразуТребуетОбновления() {
            var g = new GameInfo { GameId = "game", LatestVersion = "1.3.0" };

            GameStatus.MarkInstalled(g, "1.2.0");

            Assert.True(g.IsInstalled);
            Assert.True(g.NeedsUpdate);
        }

        /// <summary>Эталон неизвестен — сравнивать не с чем, обновление не навязываем.</summary>
        [Fact]
        public void БезЭталонаОбновлениеНеТребуется() {
            var g = new GameInfo { GameId = "game", LatestVersion = string.Empty };

            GameStatus.MarkInstalled(g, "1.2.0");

            Assert.True(g.IsInstalled);
            Assert.False(g.NeedsUpdate);
        }

        /// <summary>
        /// После удаления игра не установлена и не требует обновления: иначе кнопка
        /// предложила бы «Обновить» над пустой папкой.
        /// </summary>
        [Fact]
        public void ПослеУдаленияИграЧистая() {
            var g = new GameInfo { GameId = "game", IsInstalled = true, InstalledVersion = "1.0.0", NeedsUpdate = true };

            GameStatus.MarkUninstalled(g);

            Assert.False(g.IsInstalled);
            Assert.Equal(string.Empty, g.InstalledVersion);
            Assert.False(g.NeedsUpdate);
        }

        /// <summary>Игры уже нет в списке — отметки не роняют экран.</summary>
        [Fact]
        public void ОтсутствующаяИграНеРоняетОтметки() {
            GameStatus.MarkInstalled(null, "1.0.0");
            GameStatus.MarkUninstalled(null);
            Assert.Equal(string.Empty, GameStatus.ApplyLocalVersion(null, null));
        }

        /// <summary>Версия с диска приводится к нормальному виду и попадает в список.</summary>
        [Theory]
        [InlineData(" 1.4.2 \r\n", "1.4.2", true)]
        [InlineData("", "", false)]
        [InlineData(null, "", false)]
        [InlineData("   ", "", false)]
        public void ВерсияСДискаОпределяетУстановленность(string? disk, string expected, bool installed) {
            var g = new GameInfo { GameId = "game" };

            var trimmed = GameStatus.ApplyLocalVersion(g, disk);

            Assert.Equal(expected, trimmed);
            Assert.Equal(expected, g.InstalledVersion);
            Assert.Equal(installed, g.IsInstalled);
        }
    }
}
