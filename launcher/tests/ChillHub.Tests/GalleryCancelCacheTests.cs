// <copyright file="GalleryCancelCacheTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Что попадает в кеш галереи, а что нет.
    /// <para>
    /// Игрок листает список игр стрелками; каждый шаг отменяет предыдущий запрос —
    /// это штатный ход, а не сбой. Пока отмена гасилась в пустой список наравне с
    /// сетевой ошибкой, этот пустой список уезжал в кеш как «галереи нет», и обложка
    /// витрины пропадала до перезапуска лаунчера.
    /// </para>
    /// </summary>
    public class GalleryCancelCacheTests {
        private const string Base = "https://example.test";
        private const string Manifest = @"{""cover"": ""hero.jpg"", ""items"": [{""file"": ""hero.jpg""}]}";

        /// <summary>Отмена пробрасывается наружу и не оставляет следа в кеше.</summary>
        [Fact]
        public async Task ОтменаНеЗапоминаетсяКакОтсутствиеГалереи() {
            var handler = new Handler(_ => Json(Manifest));
            var client = new GalleryClient(new HttpClient(handler));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetGalleryAsync(Base, "moon-lander", cts.Token));

            // Игрок вернулся к той же игре — обложка обязана появиться, а не остаться пустой.
            var images = await client.GetGalleryAsync(Base, "moon-lander");
            Assert.Single(images);
            Assert.Equal(Base + "/content/moon-lander/gallery/hero.jpg", images[0].ImageUrl);
        }

        /// <summary>
        /// Сеть отвалилась (или вышел таймаут) — про игру мы ничего не узнали. Витрина
        /// на этот раз обойдётся без обложки, но следующий заход обязан сходить на сервер.
        /// </summary>
        [Fact]
        public async Task СетеваяОшибкаНеЗапоминаетсяКакПустаяГалерея() {
            var calls = 0;
            var handler = new Handler(_ => ++calls == 1
                ? throw new HttpRequestException("сеть недоступна")
                : Json(Manifest));
            var client = new GalleryClient(new HttpClient(handler));

            Assert.Empty(await client.GetGalleryAsync(Base, "moon-lander"));

            Assert.Single(await client.GetGalleryAsync(Base, "moon-lander"));
            Assert.Equal(2, calls);
        }

        /// <summary>
        /// А 404 — это ответ: галереи у игры нет. Его запоминаем, иначе за отсутствующим
        /// файлом лаунчер пойдёт на каждое наведение на игру в списке.
        /// </summary>
        [Fact]
        public async Task ОтсутствиеГалереиНаСервереЗапоминается() {
            var calls = 0;
            var handler = new Handler(_ => {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var client = new GalleryClient(new HttpClient(handler));

            Assert.Empty(await client.GetGalleryAsync(Base, "no-gallery"));
            Assert.Empty(await client.GetGalleryAsync(Base, "no-gallery"));

            Assert.Equal(1, calls);
        }

        private static HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        /// <summary>Обработчик, который честно уважает отмену: иначе тест проверял бы не то.</summary>
        private sealed class Handler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;

            internal Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) => this.reply = reply;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(this.reply(request));
            }
        }
    }
}
