// <copyright file="GalleryClientTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Разбор `gallery.json` и построение абсолютных адресов картинок для витрины.
    /// </summary>
    public class GalleryClientTests {
        /// <summary>Локальная фикстура с тремя картинками — тот же вид, что пишет админка.</summary>
        private const string Fixture = @"{
            ""cover"": ""moon-titan.jpg"",
            ""items"": [
                {""file"": ""moon-titan.jpg"", ""caption"": ""луна «Titan»""},
                {""file"": ""foggy-landing.jpg"", ""caption"": ""высадка в тумане""},
                {""file"": ""base-camp.jpg"", ""caption"": """"}
            ]
        }";

        /// <summary>Обложка встаёт первым элементом, остальные — в порядке `items`, адреса абсолютные.</summary>
        [Fact]
        public async Task ОбложкаПерваяОстальныеПоПорядку() {
            var client = new GalleryClient(Json(Fixture));

            var images = await client.GetGalleryAsync("https://example.test", "moon-lander");

            Assert.Equal(3, images.Count);
            Assert.True(images[0].IsCover);
            Assert.Equal("https://example.test/content/moon-lander/gallery/moon-titan.jpg", images[0].ImageUrl);
            Assert.Equal("луна «Titan»", images[0].Caption);
            Assert.False(images[1].IsCover);
            Assert.Equal("https://example.test/content/moon-lander/gallery/foggy-landing.jpg", images[1].ImageUrl);
            Assert.Equal(string.Empty, images[2].Caption);
        }

        /// <summary>
        /// Обложка без `items` — галерея из одной картинки. Так выглядят манифесты,
        /// записанные админкой до того, как SetCover стал регистрировать файл в
        /// `items`: раньше витрина у таких игр оставалась пустой.
        /// </summary>
        [Fact]
        public async Task ОбложкаБезItemsВсёРавноПоказывается() {
            var client = new GalleryClient(Json(@"{""cover"": ""hero.jpg"", ""items"": []}"));

            var images = await client.GetGalleryAsync("https://example.test", "moon-lander");

            var image = Assert.Single(images);
            Assert.True(image.IsCover);
            Assert.Equal("https://example.test/content/moon-lander/gallery/hero.jpg", image.ImageUrl);
        }

        /// <summary>Обложка в подпапке даёт адрес с подпапкой, а не склеенное имя.</summary>
        [Fact]
        public async Task ОбложкаИзПодпапкиДаётВерныйАдрес() {
            var client = new GalleryClient(Json(@"{""cover"": ""shots/moon.jpg"", ""items"": []}"));

            var images = await client.GetGalleryAsync("https://example.test", "moon-lander");

            Assert.Equal(
                "https://example.test/content/moon-lander/gallery/shots/moon.jpg",
                Assert.Single(images).ImageUrl);
        }

        /// <summary>Названной обложки нет среди `items` — она всё равно идёт первой.</summary>
        [Fact]
        public async Task ОбложкаВнеItemsСтановитсяПервой() {
            var client = new GalleryClient(Json(@"{
                ""cover"": ""hero.jpg"",
                ""items"": [{""file"": ""other.jpg"", ""caption"": ""другой кадр""}]
            }"));

            var images = await client.GetGalleryAsync("https://example.test", "moon-lander");

            Assert.Equal(2, images.Count);
            Assert.True(images[0].IsCover);
            Assert.Equal("https://example.test/content/moon-lander/gallery/hero.jpg", images[0].ImageUrl);
            Assert.Equal("другой кадр", images[1].Caption);
        }

        /// <summary>Ни обложки, ни картинок — пустая галерея, а не выдуманный адрес.</summary>
        [Fact]
        public async Task ПустойМанифестДаётПустуюГалерею() {
            var client = new GalleryClient(Json(@"{""cover"": """", ""items"": []}"));

            Assert.Empty(await client.GetGalleryAsync("https://example.test", "moon-lander"));
        }

        /// <summary>Запрос идёт ровно по контракту: `<baseApi>/content/<gameId>/gallery/gallery.json`.</summary>
        [Fact]
        public async Task ЗапросИдётПоКонтрактномуАдресу() {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(Fixture, Encoding.UTF8, "application/json"),
            });
            var client = new GalleryClient(new HttpClient(handler));

            await client.GetGalleryAsync("https://example.test/", "moon-lander");

            var requested = Assert.Single(handler.Requests);
            Assert.Equal("https://example.test/content/moon-lander/gallery/gallery.json", requested.ToString());
        }

        /// <summary>Второй вызов для той же игры не бьёт по сети — результат кеширован в памяти.</summary>
        [Fact]
        public async Task ВторойВызовБерётсяИзКеша() {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(Fixture, Encoding.UTF8, "application/json"),
            });
            var client = new GalleryClient(new HttpClient(handler));

            await client.GetGalleryAsync("https://example.test", "moon-lander");
            await client.GetGalleryAsync("https://example.test", "moon-lander");

            Assert.Single(handler.Requests);
        }

        /// <summary>
        /// После сброса кеша тот же вызов снова идёт на сервер: обложку заменили в
        /// админке по прежнему адресу, и «Обновить список игр» обязан её увидеть.
        /// </summary>
        [Fact]
        public async Task СбросКешаЗаставляетПерезапроситьГалерею() {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(Fixture, Encoding.UTF8, "application/json"),
            });
            var client = new GalleryClient(new HttpClient(handler));

            await client.GetGalleryAsync("https://example.test", "moon-lander");
            client.InvalidateAll();
            await client.GetGalleryAsync("https://example.test", "moon-lander");

            Assert.Equal(2, handler.Requests.Count);
        }

        /// <summary>404 (галерея ещё не заведена) — пустой список, а не исключение.</summary>
        [Fact]
        public async Task ОтсутствиеГалереиДаётПустойСписок() {
            var client = new GalleryClient(Status(HttpStatusCode.NotFound));

            var images = await client.GetGalleryAsync("https://example.test", "no-gallery");

            Assert.Empty(images);
        }

        /// <summary>Сетевая ошибка тоже гасится в пустой список — витрина не должна падать без галереи.</summary>
        [Fact]
        public async Task СетеваяОшибкаДаётПустойСписок() {
            var client = new GalleryClient(Fails(new HttpRequestException("сеть недоступна")));

            var images = await client.GetGalleryAsync("https://example.test", "moon-lander");

            Assert.Empty(images);
        }

        private static HttpClient Json(string body) => new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

        private static HttpClient Status(HttpStatusCode code) => new HttpClient(new FakeHandler(_ => new HttpResponseMessage(code)));

        private static HttpClient Fails(Exception ex) => new HttpClient(new FakeHandler(_ => throw ex));

        private sealed class FakeHandler : HttpMessageHandler {
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
