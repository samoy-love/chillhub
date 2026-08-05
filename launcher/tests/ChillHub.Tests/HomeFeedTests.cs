// <copyright file="HomeFeedTests.cs" company="PlaceholderCompany">
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
    /// Адреса данных главного экрана и разбор того, что вернул сервер.
    /// <para>
    /// Ошибка в адресе выглядит для пользователя как «сервер недоступен», а ошибка в выборе
    /// версии — как установка не той сборки: на проде первым элементом списка приходила
    /// 1.0.2 при доступной 1.1.10, и лаунчер ставил именно её.
    /// </para>
    /// </summary>
    public class HomeFeedTests {
        /// <summary>Адреса собираются ровно так, как их ждёт сервер.</summary>
        [Fact]
        public void АдресаСобираютсяОтБазыApi() {
            const string api = "https://chillhub.test";

            Assert.Equal("https://chillhub.test/api/games", HomeFeed.GamesUrl(api));
            Assert.Equal("https://chillhub.test/api/games/lethal/builds", HomeFeed.BuildsUrl(api, "lethal"));
            Assert.Equal("https://chillhub.test/news/index.json", HomeFeed.LauncherNewsUrl(api));
            Assert.Equal("https://chillhub.test/news/games/lethal/index.json", HomeFeed.GameNewsUrl(api, "lethal"));
            Assert.Equal("https://chillhub.test/news/patch-1.md", HomeFeed.LauncherNewsItemUrl(api, "patch-1"));
            Assert.Equal("https://chillhub.test/news/games/lethal/patch-1.md", HomeFeed.GameNewsItemUrl(api, "lethal", "patch-1"));
        }

        /// <summary>
        /// Корнеотносительная обложка достраивается до полного адреса, абсолютную не трогаем:
        /// иначе к чужому https приклеился бы наш адрес и картинка не загрузилась бы.
        /// </summary>
        [Fact]
        public void ОбложкиДостраиваютсяТолькоКорнеотносительные() {
            var items = new List<NewsItem> {
                new NewsItem { CoverUrl = "/covers/a.png" },
                new NewsItem { CoverUrl = "https://cdn.test/b.png" },
                new NewsItem { CoverUrl = string.Empty },
            };

            HomeFeed.NormalizeCoverUrls(items, "https://chillhub.test");

            Assert.Equal("https://chillhub.test/covers/a.png", items[0].CoverUrl);
            Assert.Equal("https://cdn.test/b.png", items[1].CoverUrl);
            Assert.Equal(string.Empty, items[2].CoverUrl);
        }

        /// <summary>Пустой список новостей — обычное дело: у новой игры их ещё нет.</summary>
        [Fact]
        public void ПустойСписокНовостейБезопасен() {
            HomeFeed.NormalizeCoverUrls(new List<NewsItem>(), "https://chillhub.test");
        }

        /// <summary>
        /// Сборки сортируются по номеру версии, а не по строке: «1.1.10» новее «1.0.2»
        /// и новее «1.1.9», хотя лексикографически всё наоборот.
        /// </summary>
        [Fact]
        public void СборкиСортируютсяПоНомеруВерсии() {
            var sorted = HomeFeed.SortBuilds(new[] { "1.0.2", "1.1.10", "1.1.9" });

            Assert.Equal(new[] { "1.1.10", "1.1.9", "1.0.2" }, sorted);
        }

        /// <summary>Сервер не прислал сборок — получаем пустой список, а не null.</summary>
        [Fact]
        public void ОтсутствиеСборокДаётПустойСписок() {
            Assert.Empty(HomeFeed.SortBuilds(null));
            Assert.Empty(HomeFeed.SortBuilds(new List<string>()));
        }

        /// <summary>Ставим ту версию, которую сервер назвал последней, а не первую из списка сборок.</summary>
        [Fact]
        public void ВерсияБерётсяИзLatest() {
            var game = new GameInfo { GameId = "game", LatestVersion = "2.0.0" };

            Assert.Equal("2.0.0", HomeFeed.SelectVersion(game, new List<string> { "1.0.0", "9.9.9" }));
        }

        /// <summary>
        /// Сервер не назвал последнюю версию — берём максимальную из сборок.
        /// Без этого фолбэка кнопка «Установить» не делала бы ничего.
        /// </summary>
        [Fact]
        public void БезLatestБерётсяМаксимальнаяСборка() {
            var game = new GameInfo { GameId = "game", LatestVersion = string.Empty };

            Assert.Equal("1.1.10", HomeFeed.SelectVersion(game, new List<string> { "1.0.2", "1.1.10", "1.1.9" }));
        }

        /// <summary>Игры нет в списке и сборок нет — ставить нечего, и это не исключение.</summary>
        [Fact]
        public void БезИгрыИСборокВерсииНет() {
            Assert.True(string.IsNullOrWhiteSpace(HomeFeed.SelectVersion(null, new List<string>())));
        }
    }

    /// <summary>
    /// Решение о строке «сколько нужно скачать».
    /// <para>
    /// Строку видно рядом с кнопкой действия, и она отвечает на главный вопрос перед
    /// нажатием: хватит ли места. Вмешательство в неё во время активной закачки стирает
    /// живой прогресс, а «Нужно: …» у актуальной игры пугает несуществующей закачкой.
    /// </para>
    /// </summary>
    public class SpaceHintDecisionTests {
        /// <summary>Во время установки строку не трогаем: там идёт живой прогресс.</summary>
        [Fact]
        public void ВоВремяУстановкиСтрокуНеТрогаем() {
            var game = new GameInfo { GameId = "game", IsInstalled = false };

            Assert.Equal(SpaceHintAction.Skip, SpaceHint.Decide(isUpdating: true, game, "game"));
        }

        /// <summary>Актуально установленной игре качать нечего — так и пишем.</summary>
        [Fact]
        public void АктуальнойИгреПоказываемЧтоВсёНаМесте() {
            var game = new GameInfo { GameId = "game", IsInstalled = true, NeedsUpdate = false };

            Assert.Equal(SpaceHintAction.ShowUpToDate, SpaceHint.Decide(isUpdating: false, game, "game"));
            Assert.Equal("Последняя версия игры уже установлена", SpaceHint.UpToDateText);
        }

        /// <summary>Игре с расхождением объём считаем — пользователю нужно знать, хватит ли места.</summary>
        [Fact]
        public void УстаревшейИгреСчитаемОбъём() {
            var game = new GameInfo { GameId = "game", IsInstalled = true, NeedsUpdate = true };

            Assert.Equal(SpaceHintAction.Compute, SpaceHint.Decide(isUpdating: false, game, "game"));
        }

        /// <summary>Неустановленной игре тоже считаем: перед установкой цифра важнее всего.</summary>
        [Fact]
        public void НеустановленнойИгреСчитаемОбъём() {
            var game = new GameInfo { GameId = "game", IsInstalled = false };

            Assert.Equal(SpaceHintAction.Compute, SpaceHint.Decide(isUpdating: false, game, "game"));
        }

        /// <summary>Игры не выбрано — считать нечего.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void БезВыбраннойИгрыНичегоНеСчитаем(string? gameId) {
            Assert.Equal(SpaceHintAction.Skip, SpaceHint.Decide(isUpdating: false, null, gameId));
        }

        /// <summary>Игры ещё нет в списке (идёт первая загрузка) — объём считаем по идентификатору.</summary>
        [Fact]
        public void ОтсутствиеИгрыВСпискеНеМешаетСчитать() {
            Assert.Equal(SpaceHintAction.Compute, SpaceHint.Decide(isUpdating: false, null, "game"));
        }
    }
}
