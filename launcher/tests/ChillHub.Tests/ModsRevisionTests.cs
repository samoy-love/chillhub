// <copyright file="ModsRevisionTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.IO;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Замечает ли лаунчер ПЕРЕСОБРАННЫЙ модпак.
    /// <para>
    /// Версия модпака — имя пакета на Thunderstore («Автор-Пак-9.5.0»), а не номер
    /// нашей сборки. Админка умеет разложить тот же пакет заново — например, после
    /// правки конвейера, — и тогда под ТЕМ ЖЕ именем на сервере лежит другое дерево.
    /// Сравнивая одни имена, лаунчер такую пересборку не замечал вовсе: исправление
    /// оставалось на сервере, а у игрока лежала прежняя папка, и понять это по
    /// карточке было нельзя.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class ModsRevisionTests {
        /// <summary>Пересобранный под тем же именем модпак зовёт обновиться.</summary>
        [Fact]
        public void ДругойОтпечатокПриТойЖеВерсииТребуетОбновления() {
            using var games = new GamesPathScope();
            Install(games.Root, version: "Team-Pack-1.0.0", revision: "aaaa1111");

            Assert.True(GameStatus.ModsOutOfDate(Game("Team-Pack-1.0.0", "bbbb2222")));
        }

        /// <summary>Тот же отпечаток — обновляться незачем.</summary>
        [Fact]
        public void ТотЖеОтпечатокОставляетИгруВПокое() {
            using var games = new GamesPathScope();
            Install(games.Root, version: "Team-Pack-1.0.0", revision: "aaaa1111");

            Assert.False(GameStatus.ModsOutOfDate(Game("Team-Pack-1.0.0", "aaaa1111")));
        }

        /// <summary>
        /// Старый сервер отпечатка не присылает — сравниваются одни версии, как
        /// раньше. Иначе обновление лаунчера впереди обновления сервера подняло бы
        /// «нужно обновить» у всех сразу и ни на что не указало.
        /// </summary>
        [Fact]
        public void БезОтпечаткаНаСервереСравниваютсяТолькоВерсии() {
            using var games = new GamesPathScope();
            Install(games.Root, version: "Team-Pack-1.0.0", revision: "aaaa1111");

            Assert.False(GameStatus.ModsOutOfDate(Game("Team-Pack-1.0.0", string.Empty)));
            Assert.True(GameStatus.ModsOutOfDate(Game("Team-Pack-2.0.0", string.Empty)));
        }

        /// <summary>
        /// Модпак ставил лаунчер, отпечатков ещё не писавший: маркера на диске нет.
        /// Один раз сверимся — это ровно те установки, до которых исправленная
        /// раскладка иначе не доедет никогда.
        /// </summary>
        [Fact]
        public void УстановкаБезМаркераСверяетсяОдинРаз() {
            using var games = new GamesPathScope();
            Install(games.Root, version: "Team-Pack-1.0.0", revision: null);

            Assert.True(GameStatus.ModsOutOfDate(Game("Team-Pack-1.0.0", "bbbb2222")));
        }

        /// <summary>Модпака на сервере нет — звать некуда, что бы ни лежало на диске.</summary>
        [Fact]
        public void БезМодпакаНаСервереНичегоНеТребуется() {
            using var games = new GamesPathScope();
            Install(games.Root, version: "Team-Pack-1.0.0", revision: "aaaa1111");

            Assert.False(GameStatus.ModsOutOfDate(new GameInfo { GameId = "game" }));
            Assert.False(GameStatus.ModsOutOfDate(null));
        }

        /// <summary>Кладёт маркеры установленного модпака в папку игры.</summary>
        /// <param name="root">Папка игр.</param>
        /// <param name="version">Версия модпака.</param>
        /// <param name="revision">Отпечаток; null — маркера нет вовсе.</param>
        private static void Install(string root, string version, string? revision) {
            var dir = Path.Combine(root, "game");
            Directory.CreateDirectory(dir);
            GameLocalState.WriteModsVersionAt(dir, version);
            if (revision != null) {
                GameLocalState.WriteModsRevisionAt(dir, revision);
            }
        }

        /// <summary>Игра с опубликованным модпаком.</summary>
        /// <param name="version">Версия на сервере.</param>
        /// <param name="revision">Отпечаток на сервере.</param>
        /// <returns>Игра из каталога.</returns>
        private static GameInfo Game(string version, string revision) => new() {
            GameId = "game",
            Mods = new ModsInfo { HasLatest = true, Version = version, Revision = revision },
        };
    }
}
