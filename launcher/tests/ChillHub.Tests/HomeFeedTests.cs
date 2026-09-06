// <copyright file="HomeFeedTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

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
        /// Необязательный раздел, которого нет на сервере, приходит пустым: страница
        /// показывает пустую ленту, а не «Проверьте подключение к интернету» с
        /// авто-отчётом на каждое открытие такой игры.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task РаздельОтвечающийЧетырестаЧетыреПриходитПустым() {
            using var http = new HttpClient(new StubHandler(System.Net.HttpStatusCode.NotFound, string.Empty));

            var index = await HomeFeed.GetOptionalAsync<NewsIndex>(http, "https://chillhub.test/news/games/lethal/index.json");

            Assert.Null(index);
        }

        /// <summary>Обычный ответ разбирается как раньше — молчание только про 404.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ОбычныйОтветРазбираетсяКакПрежде() {
            using var http = new HttpClient(new StubHandler(
                System.Net.HttpStatusCode.OK,
                @"{""items"":[{""slug"":""patch-1"",""title"":""Патч""}]}"));

            var index = await HomeFeed.GetOptionalAsync<NewsIndex>(http, "https://chillhub.test/news/index.json");

            Assert.NotNull(index);
            Assert.Single(index!.Items);
            Assert.Equal("patch-1", index.Items[0].Slug);
        }

        /// <summary>
        /// Всё, кроме 404, — настоящий сбой: 500 и обрыв связи обязаны долететь до
        /// вызывающего, иначе «сервер лежит» покажется пустой лентой.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task СбойСервераДолетаетДоВызывающего() {
            using var http = new HttpClient(new StubHandler(System.Net.HttpStatusCode.InternalServerError, string.Empty));

            await Assert.ThrowsAsync<HttpRequestException>(
                () => HomeFeed.GetOptionalAsync<NewsIndex>(http, "https://chillhub.test/news/index.json"));
        }

        /// <summary>
        /// «Нет ленты» и «сервер недоступен» — разные вещи. 404 у новостей или сборок
        /// означает, что у игры их просто нет: раздел должен остаться пустым молча, без
        /// красной строки пользователю и без авто-отчёта на сервер. Всё остальное —
        /// настоящий сбой загрузки.
        /// </summary>
        [Fact]
        public void ОтветНетТакогоНеСчитаетсяСбоемЗагрузки() {
            var notFound = new System.Net.Http.HttpRequestException("нет", null, System.Net.HttpStatusCode.NotFound);
            var serverError = new System.Net.Http.HttpRequestException("ой", null, System.Net.HttpStatusCode.InternalServerError);

            Assert.True(HomeFeed.IsNotFound(notFound));
            Assert.True(HomeFeed.IsNotFound(new System.InvalidOperationException("обёртка", notFound)));
            Assert.False(HomeFeed.IsNotFound(serverError));
            Assert.False(HomeFeed.IsNotFound(new System.Net.Http.HttpRequestException("нет сети")));
            Assert.False(HomeFeed.IsNotFound(null));
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

        /// <summary>
        /// Игры и новости обязаны спрашиваться ОДНОВРЕМЕННО.
        /// <para>
        /// На странице было написано «параллельная загрузка», а запросы шли один
        /// за другим: сначала дожидались игр, потом начинали новости. Это два
        /// обращения к серверу подряд на самом видном месте — старте, — и чем
        /// дальше игрок от сервера, тем дороже второе ожидание.
        /// </para>
        /// <para>
        /// Проверяется это удержанием: ответ на игры не отдаётся, пока не придёт
        /// запрос за новостями. Последовательная загрузка на таком сервере просто
        /// не закончится, а одновременная проходит.
        /// </para>
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ИгрыИНовостиСпрашиваютсяОдновременно() {
            using var handler = new PairedHandler();
            using var http = new HttpClient(handler);

            var load = HomeFeed.LoadStartAsync(http, "https://chillhub.test");
            var done = await Task.WhenAny(load, Task.Delay(5000)).ConfigureAwait(true);

            Assert.Same(load, done);
            var start = await load.ConfigureAwait(true);
            Assert.NotNull(start.Games);
            Assert.NotNull(start.News);
            Assert.Null(start.GamesError);
        }

        /// <summary>
        /// Отказ по новостям не отменяет главный экран: без ленты лаунчер работает
        /// целиком, а список игр — единственное, без чего он бесполезен.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ОтказПоНовостямНеЛишаетСпискаИгр() {
            using var handler = new ByUrlHandler(
                games: (HttpStatusCode.OK, "{\"items\":[{\"gameId\":\"lethal\"}]}"),
                news: (HttpStatusCode.InternalServerError, "beda"));
            using var http = new HttpClient(handler);

            var start = await HomeFeed.LoadStartAsync(http, "https://chillhub.test").ConfigureAwait(true);

            Assert.NotNull(start.Games);
            Assert.Null(start.News);
            Assert.Null(start.GamesError);
        }

        /// <summary>Отказ по играм возвращается вызывающему: из него складывается «сервер недоступен».</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ОтказПоИграмДоезжаетДоВызывающего() {
            using var handler = new ByUrlHandler(
                games: (HttpStatusCode.InternalServerError, "beda"),
                news: (HttpStatusCode.OK, "{\"items\":[]}"));
            using var http = new HttpClient(handler);

            var start = await HomeFeed.LoadStartAsync(http, "https://chillhub.test").ConfigureAwait(true);

            Assert.Null(start.Games);
            Assert.NotNull(start.GamesError);
        }

        /// <summary>
        /// Сервер, который отдаёт игры только после того, как спросили новости.
        /// Последовательная загрузка на нём не заканчивается вовсе.
        /// </summary>
        private sealed class PairedHandler : HttpMessageHandler {
            private readonly TaskCompletionSource newsAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                var url = request.RequestUri!.ToString();
                if (url.Contains("news", System.StringComparison.Ordinal)) {
                    this.newsAsked.TrySetResult();
                    return Json("{\"items\":[]}");
                }

                await this.newsAsked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return Json("{\"items\":[{\"gameId\":\"lethal\"}]}");
            }

            private static HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>Свой ответ на каждый из двух адресов.</summary>
        private sealed class ByUrlHandler : HttpMessageHandler {
            private readonly (HttpStatusCode Code, string Body) games;
            private readonly (HttpStatusCode Code, string Body) news;

            internal ByUrlHandler((HttpStatusCode Code, string Body) games, (HttpStatusCode Code, string Body) news) {
                this.games = games;
                this.news = news;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                var pick = request.RequestUri!.ToString().Contains("news", System.StringComparison.Ordinal) ? this.news : this.games;
                return Task.FromResult(new HttpResponseMessage(pick.Code) {
                    Content = new StringContent(pick.Body, Encoding.UTF8, "application/json"),
                });
            }
        }

        /// <summary>Ответ сервера, заданный тестом: проверяемому коду важен только он.</summary>
        private sealed class StubHandler : HttpMessageHandler {
            private readonly HttpStatusCode code;
            private readonly string body;

            internal StubHandler(HttpStatusCode code, string body) {
                this.code = code;
                this.body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(this.code) {
                    Content = new StringContent(this.body, Encoding.UTF8, "application/json"),
                });
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
