// <copyright file="GamePageDataTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Данные страницы игры: список сборок и changelog.
    /// <para>
    /// Оба списка приходят с сервера, и оба раньше проверялись только глазами. Список
    /// сборок задаёт, какую версию поставит кнопка «Установить», поэтому его порядок —
    /// не косметика: с неправильной сортировкой лаунчер ставил 1.0.2 при доступной
    /// 1.1.10. Changelog второстепенен, но его отказ не имеет права уронить страницу,
    /// с которой игру ставят.
    /// </para>
    /// </summary>
    public class GamePageDataTests {
        /// <summary>Сборки выстраиваются от новых к старым по смыслу версии, а не по алфавиту.</summary>
        [Fact]
        public async Task СборкиИдутОтНовыхКСтарым() {
            var loader = new GameBuildsLoader(Json(@"{""items"":[""1.0.2"",""1.1.10"",""1.1.9"",""0.9""]}"));

            var builds = await loader.LoadAsync("https://example.test", "game");

            Assert.Equal(new[] { "1.1.10", "1.1.9", "1.0.2", "0.9" }, builds);
        }

        /// <summary>Пустой список сборок — это пустой список, а не исключение на открытии страницы.</summary>
        [Fact]
        public async Task ПустойСписокСборокНеРоняетЗагрузку() {
            var loader = new GameBuildsLoader(Json(@"{""items"":[]}"));

            Assert.Empty(await loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>Ответ без поля items тоже даёт пустой список: сервер мог ответить заглушкой.</summary>
        [Fact]
        public async Task ОтветБезItemsДаётПустойСписок() {
            var loader = new GameBuildsLoader(Json("{}"));

            Assert.Empty(await loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>
        /// Нет сети — отказ выходит исключением, чтобы страница показала подсказку
        /// про подключение, а не молча оставила пустой выпадающий список.
        /// </summary>
        [Fact]
        public async Task БезСетиЗагрузкаСборокПадаетИсключением() {
            var loader = new GameBuildsLoader(Fails(new HttpRequestException("сеть недоступна")));

            await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>Битый JSON — тоже отказ, а не «сборок нет»: иначе пропадёт кнопка отката.</summary>
        [Fact]
        public async Task БитыйJsonСборокПадаетИсключением() {
            var loader = new GameBuildsLoader(Json("не json вовсе"));

            await Assert.ThrowsAnyAsync<Exception>(() => loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>404 от сервера не выдаётся за пустой список сборок.</summary>
        [Fact]
        public async Task ОтветЧетыреНольЧетыреПадаетИсключением() {
            var loader = new GameBuildsLoader(Status(HttpStatusCode.NotFound));

            await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>Адрес списка сборок собирается ровно так, как его отдаёт сервер.</summary>
        [Fact]
        public void АдресСписокСборокСобираетсяИзБазыИИдентификатора() {
            Assert.Equal(
                "https://example.test/api/games/lethal-company/builds",
                GameBuildsLoader.BuildsUrl("https://example.test", "lethal-company"));
        }

        /// <summary>По умолчанию выбирается установленная версия — с неё пользователь и начинает.</summary>
        [Fact]
        public void ВыбраннойСтановитсяУстановленнаяВерсия() {
            var builds = new List<string> { "1.2.0", "1.1.0", "1.0.0" };

            Assert.Equal(2, GameBuildsLoader.SelectIndex(builds, "1.0.0"));
        }

        /// <summary>Краевые пробелы и регистр не мешают найти установленную версию в списке.</summary>
        [Theory]
        [InlineData(" 1.1.0 ")]
        [InlineData("1.1.0")]
        public void КраевыеПробелыНеМешаютНайтиВерсию(string preselect) {
            var builds = new List<string> { "1.2.0", "1.1.0", "1.0.0" };

            Assert.Equal(1, GameBuildsLoader.SelectIndex(builds, preselect));
        }

        /// <summary>
        /// Версии, которой нет в списке, соответствует первая (то есть самая новая) сборка:
        /// пустой выбор оставил бы кнопку переключения бессмысленной.
        /// </summary>
        [Fact]
        public void НеизвестнаяВерсияОткатываетсяКПервойСборке() {
            var builds = new List<string> { "1.2.0", "1.1.0" };

            Assert.Equal(0, GameBuildsLoader.SelectIndex(builds, "7.7.7"));
            Assert.Equal(0, GameBuildsLoader.SelectIndex(builds, null));
        }

        /// <summary>Выбирать не из чего — индекс -1, а не 0: обращение к пустому списку упало бы.</summary>
        [Fact]
        public void ПустойСписокДаётИндексМинусОдин() {
            Assert.Equal(-1, GameBuildsLoader.SelectIndex(new List<string>(), "1.0.0"));
        }

        /// <summary>Changelog разбирается в список записей.</summary>
        [Fact]
        public async Task ChangelogРазбираетсяВЗаписи() {
            var loader = new GameChangelogLoader(Json(
                @"{""items"":[{""title"":""Патч"",""slug"":""patch-1""},{""title"":""Хотфикс"",""slug"":""hotfix""}]}"));

            var items = await loader.LoadAsync("https://example.test", "game");

            Assert.Equal(2, items.Count);
            Assert.Equal("Патч", items[0].Title);
            Assert.Equal("patch-1", items[0].Slug);
        }

        /// <summary>
        /// Обложка от корня сайта дополняется базой API: WebView и список показывают
        /// картинку по абсолютному адресу, относительный привёл бы к пустому месту.
        /// </summary>
        [Fact]
        public async Task ОтносительныйАдресОбложкиДополняетсяБазой() {
            var loader = new GameChangelogLoader(Json(
                @"{""items"":[{""title"":""Патч"",""slug"":""p"",""coverUrl"":""/news/cover.png""}]}"));

            var items = await loader.LoadAsync("https://example.test", "game");

            Assert.Equal("https://example.test/news/cover.png", items[0].CoverUrl);
        }

        /// <summary>Абсолютный адрес обложки не трогаем: иначе получится «база + чужой домен».</summary>
        [Fact]
        public void АбсолютныйАдресОбложкиОстаётсяКакЕсть() {
            var items = new List<NewsItem> {
                new NewsItem { CoverUrl = "https://cdn.example.test/cover.png" },
                new NewsItem { CoverUrl = string.Empty },
            };

            GameChangelogLoader.AbsolutizeCovers("https://example.test", items);

            Assert.Equal("https://cdn.example.test/cover.png", items[0].CoverUrl);
            Assert.Equal(string.Empty, items[1].CoverUrl);
        }

        /// <summary>Пустой changelog — пустой список: страница покажет «Записей пока нет».</summary>
        [Fact]
        public async Task ПустойChangelogДаётПустойСписок() {
            var loader = new GameChangelogLoader(Json(@"{""items"":[]}"));

            Assert.Empty(await loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>Отсутствующий changelog (404) — отказ, а не «записей нет».</summary>
        [Fact]
        public async Task ОтсутствующийChangelogПадаетИсключением() {
            var loader = new GameChangelogLoader(Status(HttpStatusCode.NotFound));

            await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>Нет сети — changelog отказывает, но не тихо: страница напишет про подключение.</summary>
        [Fact]
        public async Task БезСетиChangelogПадаетИсключением() {
            var loader = new GameChangelogLoader(Fails(new HttpRequestException("сеть недоступна")));

            await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync("https://example.test", "game"));
        }

        /// <summary>Адреса changelog собираются от базы API, без задвоенных сегментов.</summary>
        [Fact]
        public void АдресаChangelogСобираютсяОтБазы() {
            Assert.Equal(
                "https://example.test/news/games/game/index.json",
                GameChangelogLoader.IndexUrl("https://example.test", "game"));
            Assert.Equal(
                "https://example.test/news/games/game/patch-1.md",
                GameChangelogLoader.ArticleUrl("https://example.test", "game", "patch-1"));
        }

        private static HttpClient Json(string body) => new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

        private static HttpClient Status(HttpStatusCode code) => new HttpClient(new FakeHandler(_ => new HttpResponseMessage(code)));

        private static HttpClient Fails(Exception ex) => new HttpClient(new FakeHandler(_ => throw ex));

        /// <summary>Подставной транспорт: отвечает по заданному правилу и запоминает адреса запросов.</summary>
        internal sealed class FakeHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;
            private readonly ConcurrentQueue<Uri> seen = new();

            internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => this.reply = reply;

            internal IReadOnlyList<Uri> Requests => this.seen.ToArray();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
                this.seen.Enqueue(request.RequestUri!);
                return Task.FromResult(this.reply(request));
            }
        }
    }
}
