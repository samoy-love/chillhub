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

        /// <summary>
        /// ОБНОВЛЕНИЕ СПИСКА НЕ ПОДМЕНЯЕТ ОБЪЕКТЫ ИГР. Объект игры — это её строка на
        /// экране: замени его новым, и WPF пересоздаст строку со значком, потеряет
        /// выделение и заново загрузит всю правую половину экрана. Ради данных, которые
        /// обычно те же самые.
        /// </summary>
        [Fact]
        public void ОбновлениеСпискаСохраняетПрежниеОбъекты() {
            var shown = new List<GameInfo> { Game("a", "А"), Game("b", "Б") };
            var fromServer = new List<GameInfo> { Game("a", "А — новое имя"), Game("b", "Б") };

            var merged = GameCatalog.Merge(shown, fromServer);

            Assert.Same(shown[0], merged[0]);
            Assert.Same(shown[1], merged[1]);
            Assert.Equal("А — новое имя", merged[0].Title);
        }

        /// <summary>
        /// Состояние диска сервер не знает и знать не может: «установлена», версия на
        /// диске и метка очереди переживают обновление списка. Иначе качающаяся игра на
        /// секунду становилась бы неустановленной.
        /// </summary>
        [Fact]
        public void ОбновлениеСпискаНеСтираетСостояниеНаДиске() {
            var shown = new List<GameInfo> {
                new GameInfo {
                    GameId = "a",
                    Title = "А",
                    IsInstalled = true,
                    InstalledVersion = "1.0.9",
                    NeedsUpdate = true,
                    QueueLabel = "Скачивание · 38%",
                },
            };

            var merged = GameCatalog.Merge(shown, new List<GameInfo> { Game("a", "А") });

            Assert.True(merged[0].IsInstalled);
            Assert.Equal("1.0.9", merged[0].InstalledVersion);
            Assert.True(merged[0].NeedsUpdate);
            Assert.Equal("Скачивание · 38%", merged[0].QueueLabel);
        }

        /// <summary>Новая игра приходит как есть, исчезнувшая — уходит из списка.</summary>
        [Fact]
        public void ПоявившиесяИИсчезнувшиеИгрыУчитываются() {
            var shown = new List<GameInfo> { Game("a", "А"), Game("b", "Б") };
            var fromServer = new List<GameInfo> { Game("b", "Б"), Game("c", "В") };

            var merged = GameCatalog.Merge(shown, fromServer);

            Assert.Equal(new[] { "b", "c" }, merged.Select(g => g.GameId));
            Assert.Same(shown[1], merged[0]);
        }

        /// <summary>Первое заполнение списка сливать не с чем.</summary>
        [Fact]
        public void ПервоеЗаполнениеБерётСписокКакЕсть() {
            var fromServer = new List<GameInfo> { Game("a", "А") };

            Assert.Same(fromServer[0], GameCatalog.Merge(null, fromServer)[0]);
            Assert.Empty(GameCatalog.Merge(new List<GameInfo>(), null));
        }

        /// <summary>
        /// Порядок сравнивается по составу и последовательности: только его смена
        /// оправдывает подмену источника списка со всеми её последствиями.
        /// </summary>
        [Fact]
        public void ОдинаковыйПорядокУзнаётся() {
            var a = new List<GameInfo> { Game("a", "А"), Game("b", "Б") };
            var same = new List<GameInfo> { Game("A", "другое имя"), Game("b", "Б") };
            var other = new List<GameInfo> { Game("b", "Б"), Game("a", "А") };

            Assert.True(GameCatalog.SameOrder(a, same));
            Assert.False(GameCatalog.SameOrder(a, other));
            Assert.False(GameCatalog.SameOrder(a, new List<GameInfo> { Game("a", "А") }));
            Assert.False(GameCatalog.SameOrder(a, null));
        }

        /// <summary>
        /// Подмена источника решается по ПОКАЗАННОМУ списку, а не по полю страницы.
        /// <para>
        /// Ровно здесь пряталась игра, удалённая в админке: слияние ответа сервера отдаёт
        /// НОВЫЙ список, поле страницы уже указывает на него, а на экране висит прежний.
        /// Сравнение поля с самим собой всегда отвечало «то же самое», источник не менялся,
        /// и удалённая игра оставалась строкой в списке — пропадал только её значок.
        /// </para>
        /// </summary>
        [Fact]
        public void ПодменаИсточникаРешаетсяПоПоказанномуСписку() {
            var a = Game("a", "А");
            var b = Game("b", "Б");
            var shown = new List<GameInfo> { a, b };

            // Тот же состав в том же порядке — трогать список незачем, даже если это
            // другой объект: именно на этом держится отсутствие мерцания.
            Assert.False(GameCatalog.NeedsRebind(shown, new List<GameInfo> { a, b }));

            // Игру удалили в админке — показанный список обязан смениться.
            Assert.True(GameCatalog.NeedsRebind(shown, new List<GameInfo> { a }));

            // Появилась новая, поменялся порядок — тоже смена.
            Assert.True(GameCatalog.NeedsRebind(shown, new List<GameInfo> { a, b, Game("c", "В") }));
            Assert.True(GameCatalog.NeedsRebind(shown, new List<GameInfo> { b, a }));

            // Списку ещё ничего не привязано, либо привязано чужое — показывать нечего.
            Assert.True(GameCatalog.NeedsRebind(null, shown));
            Assert.True(GameCatalog.NeedsRebind("не список", shown));
        }

        private static GameInfo Game(string id, string title, bool installed = false) =>
            new GameInfo { GameId = id, Title = title, IsInstalled = installed };
    }
}
