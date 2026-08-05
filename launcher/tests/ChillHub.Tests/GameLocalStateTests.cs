// <copyright file="GameLocalStateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Локальное состояние игры на диске: маркер `.version`, следы оборванного обновления,
    /// наличие полезных файлов.
    /// <para>
    /// По этим трём вопросам главный экран решает, что написать на кнопке: «Играть»,
    /// «Обновить» или «Установить». Ошибка здесь стоит пользователю повторной закачки
    /// сборки на десятки гигабайт, поэтому «не установлено» должно означать именно это,
    /// а не «маркер не прочитался».
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class GameLocalStateTests {
        /// <summary>Записанная версия читается обратно ровно в том же виде.</summary>
        [Fact]
        public void ЗаписаннаяВерсияЧитаетсяОбратно() {
            using var games = new GamesPathScope();

            Assert.True(GameLocalState.WriteLocalVersion("lethal-company", "1.4.2"));
            Assert.Equal("1.4.2", GameLocalState.ReadLocalVersion("lethal-company"));
        }

        /// <summary>Маркер кладётся в папку игры, а не рядом с ней.</summary>
        [Fact]
        public void МаркерЛежитВПапкеИгры() {
            using var games = new GamesPathScope();

            GameLocalState.WriteLocalVersion("lethal-company", "1.0.0");

            var marker = Path.Combine(games.Root, "lethal-company", IntegrityChecker.VersionMarkerFileName);
            Assert.True(File.Exists(marker), $"маркер не найден: {marker}");
        }

        /// <summary>
        /// Краевые пробелы и перевод строки в маркере отбрасываются: файл правят руками
        /// и переносят между машинами, а версия «1.0.0\r\n» не совпала бы с «1.0.0»
        /// и вызвала бы переустановку на ровном месте.
        /// </summary>
        [Fact]
        public void КраевыеПробелыВМаркереНеСчитаютсяДругойВерсией() {
            using var games = new GamesPathScope();
            var root = Path.Combine(games.Root, "game");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, IntegrityChecker.VersionMarkerFileName), "  1.2.3 \r\n");

            Assert.Equal("1.2.3", GameLocalState.ReadLocalVersion("game"));
        }

        /// <summary>Игра без маркера — не установлена. Пустая строка, а не исключение.</summary>
        [Fact]
        public void БезМаркераВерсияПустая() {
            using var games = new GamesPathScope();

            Assert.Equal(string.Empty, GameLocalState.ReadLocalVersion("никогда-не-ставили"));
        }

        /// <summary>Пустой идентификатор игры не роняет чтение и не пишет маркер в корень папки игр.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойИдентификаторБезопасен(string? gameId) {
            using var games = new GamesPathScope();

            Assert.Equal(string.Empty, GameLocalState.ReadLocalVersion(gameId));
            Assert.False(GameLocalState.WriteLocalVersion(gameId, "1.0.0"));
            Assert.False(GameLocalState.HasUnfinishedUpdate(gameId));
        }

        /// <summary>Пустая версия записывается как пустая строка — это «версия неизвестна», а не сбой.</summary>
        [Fact]
        public void ПустаяВерсияЗаписываетсяКакПустая() {
            using var games = new GamesPathScope();

            Assert.True(GameLocalState.WriteLocalVersion("game", null));
            Assert.Equal(string.Empty, GameLocalState.ReadLocalVersion("game"));
        }

        /// <summary>Маркер незавершённого обновления виден по папке игры.</summary>
        [Fact]
        public void СледОборванногоОбновленияВиден() {
            using var games = new GamesPathScope();
            var root = Path.Combine(games.Root, "game");
            Directory.CreateDirectory(root);

            Assert.False(GameLocalState.HasUnfinishedUpdate("game"));

            File.WriteAllText(Path.Combine(root, SimpleSyncService.UpdateMarkerFileName), "version=1.0.0");
            Assert.True(GameLocalState.HasUnfinishedUpdate("game"));
        }

        /// <summary>
        /// Служебные файлы не делают папку «установленной игрой»: иначе после неудачной
        /// первой установки лаунчер предложил бы «Играть» над пустым каталогом.
        /// </summary>
        [Fact]
        public void ТолькоСлужебныеФайлыЭтоНеУстановленнаяИгра() {
            using var games = new GamesPathScope();
            var root = Path.Combine(games.Root, "game");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, IntegrityChecker.VersionMarkerFileName), "1.0.0");
            File.WriteAllText(Path.Combine(root, SimpleSyncService.UpdateMarkerFileName), "version=1.0.0");

            Assert.False(GameLocalState.HasAnyLocalGameFiles(root));

            File.WriteAllText(Path.Combine(root, "game.exe"), "MZ");
            Assert.True(GameLocalState.HasAnyLocalGameFiles(root));
        }

        /// <summary>Путь к папке игры строится от папки игр из конфига.</summary>
        [Fact]
        public void ПутьКИгреСтроитсяОтПапкиИгрИзКонфига() {
            using var games = new GamesPathScope();

            Assert.Equal(
                Path.Combine(games.Root, "lethal-company"),
                GameLocalState.GameLocalRoot("lethal-company"));
        }

        /// <summary>
        /// Свободное место либо реальное число, либо ноль. Отрицательных значений быть
        /// не может: их показали бы пользователю как «доступно -3 ГБ».
        /// </summary>
        [Fact]
        public void СвободноеМестоНеБываетОтрицательным() {
            using var games = new GamesPathScope();

            Assert.True(GameLocalState.GetAvailableFreeSpaceFor("game") >= 0);
        }

        /// <summary>Несуществующий диск — это ноль, а не исключение на главном экране.</summary>
        [Fact]
        public void НедоступныйДискДаётНоль() {
            using var games = new GamesPathScope(@"Q:\нет-такого-диска\games");

            Assert.Equal(0, GameLocalState.GetAvailableFreeSpaceFor("game"));
        }
    }

    /// <summary>
    /// Тесты, подменяющие папку игр в конфиге, идут в одной коллекции: конфиг —
    /// глобальное состояние процесса, а классы xUnit по умолчанию выполняются параллельно.
    /// </summary>
    [CollectionDefinition(Name)]
    public class GamesPathCollection {
        internal const string Name = "games-path";
    }

    /// <summary>
    /// Временно подменяет папку игр в конфиге. Конфиг правится ТОЛЬКО в памяти:
    /// ConfigService.Save записал бы подставной путь в настоящий config.json разработчика.
    /// </summary>
    internal sealed class GamesPathScope : IDisposable {
        private readonly string previous;

        internal GamesPathScope(string? root = null) {
            this.Dir = root == null ? new TempDir() : null;
            this.Root = root ?? this.Dir!.Root;
            this.previous = ConfigService.Current.GamesPath;
            ConfigService.Current.GamesPath = this.Root;
        }

        /// <summary>Подставленная папка игр.</summary>
        internal string Root { get; }

        private TempDir? Dir { get; }

        public void Dispose() {
            ConfigService.Current.GamesPath = this.previous;
            this.Dir?.Dispose();
        }
    }
}
