// <copyright file="GameChangelogLoaderTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Лента изменений игры. Проверяется главное различие: «ленты нет» и «сервер не
    /// отвечает» — разные исходы, и путать их нельзя. На 404 страница игры показывала
    /// «Не удалось загрузить changelog. Проверьте подключение к интернету» и слала
    /// авто-отчёт при каждом открытии игры без ленты.
    /// </summary>
    public class GameChangelogLoaderTests {
        /// <summary>Ленты у игры нет — пустой список, а не исключение.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ОтсутствующаяЛентаЭтоПустойСписок() {
            using var http = new HttpClient(new Reply(HttpStatusCode.NotFound, string.Empty));
            var loader = new GameChangelogLoader(http);

            var items = await loader.LoadAsync("https://chillhub.test", "lethal");

            Assert.Empty(items);
        }

        /// <summary>Обложки достраиваются до абсолютных, как и раньше.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ЛентаРазбираетсяИОбложкиСтановятсяАбсолютными() {
            using var http = new HttpClient(new Reply(
                HttpStatusCode.OK,
                @"{""items"":[{""slug"":""p1"",""title"":""Патч"",""coverUrl"":""/covers/p1.png""}]}"));
            var loader = new GameChangelogLoader(http);

            var items = await loader.LoadAsync("https://chillhub.test", "lethal");

            Assert.Single(items);
            Assert.Equal("https://chillhub.test/covers/p1.png", items[0].CoverUrl);
        }

        /// <summary>Сервер лежит — это ошибка, и она обязана долететь до страницы.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task СбойСервераОстаётсяОшибкой() {
            using var http = new HttpClient(new Reply(HttpStatusCode.InternalServerError, string.Empty));
            var loader = new GameChangelogLoader(http);

            await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync("https://chillhub.test", "lethal"));
        }

        private sealed class Reply : HttpMessageHandler {
            private readonly HttpStatusCode code;
            private readonly string body;

            internal Reply(HttpStatusCode code, string body) {
                this.code = code;
                this.body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(this.code) {
                    Content = new StringContent(this.body, Encoding.UTF8, "application/json"),
                });
        }
    }
}
