// <copyright file="GameCatalogTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.Linq;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Порядок игр в списке и выбор игры при старте.
    /// <para>
    /// Список перед глазами пользователя не имеет права перетасовываться сам собой:
    /// раньше правил сортировки было два, и после установки или удаления игра уезжала
    /// из-под курсора. А выделение задаёт игру, к которой относятся кнопка действия,
    /// оценка объёма загрузки и новости, — промах здесь ведёт к действию не над той игрой.
    /// </para>
    /// </summary>
    public class GameCatalogTests {
        /// <summary>Установленные игры идут первыми, даже если API прислал их последними.</summary>
        [Fact]
        public void УстановленныеИгрыИдутСверху() {
            var catalog = new GameCatalog();
            var games = new List<GameInfo> {
                Game("a", "Альфа", installed: false),
                Game("b", "Бета", installed: true),
            };
            catalog.RememberApiOrder(games);

            var sorted = catalog.Sort(games);

            Assert.Equal(new[] { "b", "a" }, sorted.Select(g => g.GameId));
        }

        /// <summary>
        /// Внутри группы держится порядок, пришедший от API: он задан владельцем каталога,
        /// и переставлять игры по алфавиту значит спорить с сервером.
        /// </summary>
        [Fact]
        public void ВнутриГруппыДержитсяПорядокApi() {
            var catalog = new GameCatalog();
            var games = new List<GameInfo> {
                Game("я-первая", "Яблоко", installed: false),
                Game("а-вторая", "Абрикос", installed: false),
            };
            catalog.RememberApiOrder(games);

            var sorted = catalog.Sort(games);

            Assert.Equal(new[] { "я-первая", "а-вторая" }, sorted.Select(g => g.GameId));
        }

        /// <summary>
        /// Игра, которой не было в ответе API, встаёт в конец своей группы и сортируется
        /// по названию: иначе её позиция зависела бы от мусора в словаре порядка.
        /// </summary>
        [Fact]
        public void НеизвестнаяApiИграУходитВКонецГруппы() {
            var catalog = new GameCatalog();
            catalog.RememberApiOrder(new List<GameInfo> { Game("known", "Известная", installed: false) });

            var sorted = catalog.Sort(new List<GameInfo> {
                Game("unknown", "Аноним", installed: false),
                Game("known", "Известная", installed: false),
            });

            Assert.Equal(new[] { "known", "unknown" }, sorted.Select(g => g.GameId));
        }

        /// <summary>Повторный ответ API полностью заменяет прежний порядок, а не дополняет его.</summary>
        [Fact]
        public void ПовторныйОтветApiПерезаписываетПорядок() {
            var catalog = new GameCatalog();
            catalog.RememberApiOrder(new List<GameInfo> { Game("a", "А"), Game("b", "Б") });
            catalog.RememberApiOrder(new List<GameInfo> { Game("b", "Б"), Game("a", "А") });

            var sorted = catalog.Sort(new List<GameInfo> { Game("a", "А"), Game("b", "Б") });

            Assert.Equal(new[] { "b", "a" }, sorted.Select(g => g.GameId));
        }

        /// <summary>Сортировка отдаёт новый список и не трогает исходный: его ещё показывает UI.</summary>
        [Fact]
        public void СортировкаНеМеняетИсходныйСписок() {
            var catalog = new GameCatalog();
            var games = new List<GameInfo> { Game("a", "А", installed: false), Game("b", "Б", installed: true) };
            catalog.RememberApiOrder(games);

            var sorted = catalog.Sort(games);

            Assert.NotSame(games, sorted);
            Assert.Equal("a", games[0].GameId);
        }

        /// <summary>При старте выделяется последняя запущенная игра, а не первая в списке.</summary>
        [Fact]
        public void ПриСтартеВыделяетсяПоследняяЗапущенная() {
            var games = new List<GameInfo> { Game("a", "А"), Game("b", "Б"), Game("c", "В") };

            Assert.Equal(2, GameCatalog.SelectStartupIndex(games, "c"));
        }

        /// <summary>
        /// Идентификатор из конфига сравнивается без учёта регистра: файл настроек правят
        /// руками, и «Lethal-Company» не должен означать «игра не найдена».
        /// </summary>
        [Fact]
        public void ПоследняяЗапущеннаяИщетсяБезУчётаРегистра() {
            var games = new List<GameInfo> { Game("a", "А"), Game("lethal-company", "Б") };

            Assert.Equal(1, GameCatalog.SelectStartupIndex(games, "Lethal-Company"));
        }

        /// <summary>
        /// Последняя запущенная игра исчезла из каталога — выделяем первую установленную:
        /// пользователю нужнее та, в которую он может играть прямо сейчас.
        /// </summary>
        [Fact]
        public void БезПоследнейЗапущеннойВыделяетсяПерваяУстановленная() {
            var games = new List<GameInfo> {
                Game("a", "А", installed: false),
                Game("b", "Б", installed: true),
            };

            Assert.Equal(1, GameCatalog.SelectStartupIndex(games, "исчезнувшая"));
        }

        /// <summary>Ни одна игра не установлена — выделяем первую, чтобы экран не остался без выбора.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void БезУстановленныхВыделяетсяПервая(string? lastGameId) {
            var games = new List<GameInfo> { Game("a", "А", installed: false), Game("b", "Б", installed: false) };

            Assert.Equal(0, GameCatalog.SelectStartupIndex(games, lastGameId));
        }

        /// <summary>Пустой список — выделять нечего, и это не исключение на главном экране.</summary>
        [Fact]
        public void ПустойСписокНеДаётВыделения() {
            Assert.Equal(-1, GameCatalog.SelectStartupIndex(new List<GameInfo>(), "a"));
        }

        /// <summary>Поиск позиции по точному совпадению — так восстанавливают выделение после пересортировки.</summary>
        [Fact]
        public void ПозицияИщетсяПоТочномуСовпадению() {
            var games = new List<GameInfo> { Game("a", "А"), Game("b", "Б") };

            Assert.Equal(1, GameCatalog.IndexOf(games, "b"));
            Assert.Equal(-1, GameCatalog.IndexOf(games, "B"));
            Assert.Equal(-1, GameCatalog.IndexOf(games, null));
        }

        /// <summary>
        /// После повторной загрузки списка с сервера выделение восстанавливается без учёта
        /// регистра: сравнивается идентификатор из прежнего ответа с новым.
        /// </summary>
        [Fact]
        public void ПозицияПослеПерезагрузкиИщетсяБезУчётаРегистра() {
            var games = new List<GameInfo> { Game("a", "А"), Game("Lethal-Company", "Б") };

            Assert.Equal(1, GameCatalog.IndexOfIgnoreCase(games, "lethal-company"));
            Assert.Equal(-1, GameCatalog.IndexOfIgnoreCase(games, "нет-такой"));
        }

        private static GameInfo Game(string id, string title, bool installed = false) =>
            new GameInfo { GameId = id, Title = title, IsInstalled = installed };
    }
}
