// <copyright file="ImageDownloadLimitTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Предел размера обложки и таймаут её загрузки.
    /// <para>
    /// Адрес обложки приходит в новости с сервера — это внешний вход, а не наша константа.
    /// Без предела ответ читается в память целиком, сколько бы его ни отдали, и одна
    /// новость с подменённым адресом кладёт лаунчер. Без таймаута элемент списка стоит
    /// пустым до общего таймаута клиента в сто секунд.
    /// </para>
    /// </summary>
    [Collection("ImageLoader")]
    public class ImageDownloadLimitTests : IDisposable {
        public void Dispose() => ImageLoader.ResetForTests();

        /// <summary>
        /// Ответ, заявивший размер больше предела, отвергается ДО чтения тела:
        /// иначе проверка предела стоила бы ровно той памяти, от которой защищает.
        /// </summary>
        [Fact]
        public async Task ЗаявленныйРазмерБольшеПределаОтвергается() {
            var read = 0;
            ImageLoader.Http = new HttpClient(new StubHandler(_ => {
                var content = new CountingContent(ImageLoader.MaxImageBytes + 1, () => read++);
                content.Headers.ContentLength = ImageLoader.MaxImageBytes + 1;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => ImageLoader.FetchBytesAsync("https://example.test/huge.png"));
            Assert.Equal(0, read);
        }

        /// <summary>
        /// Заголовку длины верить нельзя: его может не быть вовсе. Предел обязан
        /// срабатывать и по факту прочитанного, иначе он обходится одной строкой заголовка.
        /// </summary>
        [Fact]
        public async Task БезЗаголовкаДлиныПределВсёРавноСрабатывает() {
            ImageLoader.Http = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new CountingContent(ImageLoader.MaxImageBytes + 4096, null),
                }));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => ImageLoader.FetchBytesAsync("https://example.test/lying.png"));
        }

        /// <summary>Картинка обычного размера проходит целиком и без потерь.</summary>
        [Fact]
        public async Task ОбычнаяКартинкаПроходитЦеликом() {
            var payload = new byte[64 * 1024];
            for (var i = 0; i < payload.Length; i++) {
                payload[i] = (byte)(i % 251);
            }

            ImageLoader.Http = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

            Assert.Equal(payload, await ImageLoader.FetchBytesAsync("https://example.test/cover.png"));
        }

        /// <summary>
        /// Молчащий сервер не держит элемент списка вечно: у загрузки свой таймаут,
        /// заметно короче общего клиентского.
        /// </summary>
        [Fact]
        public void УЗагрузкиЕстьСобственныйТаймаут() {
            Assert.True(
                ImageLoader.DownloadTimeout > TimeSpan.Zero,
                "таймаут не задан — картинку будут ждать сто секунд");
            Assert.True(
                ImageLoader.DownloadTimeout < TimeSpan.FromSeconds(100),
                $"таймаут {ImageLoader.DownloadTimeout} не короче общего клиентского");
        }

        /// <summary>Отмена по таймауту доходит до вызывающего, а не виснет молча.</summary>
        [Fact]
        public async Task ОтменаЗагрузкиДоходитДоВызывающего() {
            ImageLoader.Http = new HttpClient(new StubHandler(_ => throw new OperationCanceledException()));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ImageLoader.FetchBytesAsync("https://example.test/slow.png"));
        }

        /// <summary>Транспорт, отвечающий по заданному правилу.</summary>
        private sealed class StubHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;

            internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => this.reply = reply;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(this.reply(request));
        }

        /// <summary>
        /// Тело заданной длины, которое считает обращения к себе. Байты выдаются потоком
        /// и в памяти целиком не лежат — иначе тест сам съел бы столько же, сколько
        /// проверяет.
        /// </summary>
        private sealed class CountingContent : HttpContent {
            private readonly long length;
            private readonly Action? onRead;

            internal CountingContent(long length, Action? onRead) {
                this.length = length;
                this.onRead = onRead;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) {
                this.onRead?.Invoke();
                var chunk = new byte[81920];
                long left = this.length;
                while (left > 0) {
                    var take = (int)Math.Min(chunk.Length, left);
                    await stream.WriteAsync(chunk.AsMemory(0, take)).ConfigureAwait(false);
                    left -= take;
                }
            }

            protected override bool TryComputeLength(out long computed) {
                computed = this.length;
                return false;
            }
        }
    }
}
