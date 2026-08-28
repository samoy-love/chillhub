// <copyright file="ImageDiskCacheTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Кеш картинок на диске: значки, обложки новостей и обложки витрины качаются один
    /// раз за всё время, а не заново при каждом запуске лаунчера.
    /// <para>
    /// Проверяется именно то, ради чего он заведён: тело картинки приходит по сети ровно
    /// однажды, при повторном запросе сервер отвечает «не менялось» и байты берутся с
    /// диска. И обратное: если картинку на сервере заменили, приезжает новая — кеш не
    /// должен превращаться в способ показывать вчерашнее.
    /// </para>
    /// </summary>
    [Collection(ImageLoaderCollection.Name)]
    public class ImageDiskCacheTests : IDisposable {
        private const string Url = "https://images.invalid/icon.png";

        private readonly string dir = Path.Combine(Path.GetTempPath(), "chillhub-imgcache-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable scope;

        public ImageDiskCacheTests() {
            ImageLoader.ResetForTests();
            this.scope = ImageDiskCache.OverrideDirForTests(this.dir);
            ImageDiskCache.Enabled = true;
        }

        public void Dispose() {
            ImageDiskCache.Enabled = false;
            this.scope.Dispose();
            ImageLoader.ResetForTests();
            try {
                if (Directory.Exists(this.dir)) {
                    Directory.Delete(this.dir, recursive: true);
                }
            }
            catch (IOException) {
                // Временный каталог — не повод валить прогон.
            }
        }

        /// <summary>Первый запрос кладёт картинку на диск вместе с метками сверки.</summary>
        [Fact]
        public async Task ПерваяЗагрузкаЛожитсяНаДиск() {
            var handler = Responder(_ => Ok("картинка", etag: "\"v1\""));
            ImageLoader.Http = handler.Client();

            var bytes = await ImageLoader.FetchBytesAsync(Url);

            Assert.Equal("картинка", Encoding.UTF8.GetString(bytes));
            var cached = ImageDiskCache.Read(Url);
            Assert.NotNull(cached);
            Assert.Equal("\"v1\"", cached!.ETag);
        }

        /// <summary>
        /// Повторный запуск: запрос уходит с меткой прошлого ответа, сервер отвечает
        /// «не менялось» — и тело картинки по сети больше не едет.
        /// </summary>
        [Fact]
        public async Task ПовторныйЗапросБеретБайтыСДиска() {
            ImageDiskCache.Save(Url, Encoding.UTF8.GetBytes("старая"), "\"v1\"", null);

            string? sentEtag = null;
            var handler = Responder(req => {
                sentEtag = req.Headers.TryGetValues("If-None-Match", out var v) ? v.FirstOrDefault() : null;
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
            ImageLoader.Http = handler.Client();

            var bytes = await ImageLoader.FetchBytesAsync(Url);

            Assert.Equal("\"v1\"", sentEtag);
            Assert.Equal("старая", Encoding.UTF8.GetString(bytes));
        }

        /// <summary>Картинку на сервере заменили — приезжает новая, а не та, что в кеше.</summary>
        [Fact]
        public async Task ИзменённаяНаСервереКартинкаПриезжаетЗаново() {
            ImageDiskCache.Save(Url, Encoding.UTF8.GetBytes("старая"), "\"v1\"", null);
            ImageLoader.Http = Responder(_ => Ok("новая", etag: "\"v2\"")).Client();

            var bytes = await ImageLoader.FetchBytesAsync(Url);

            Assert.Equal("новая", Encoding.UTF8.GetString(bytes));
            Assert.Equal("\"v2\"", ImageDiskCache.Read(Url)!.ETag);
        }

        /// <summary>
        /// Сети нет — показываем то, что лежит на диске: пустой список игр вместо значков
        /// хуже, чем вчерашние значки.
        /// </summary>
        [Fact]
        public async Task БезСетиКартинкаБерётсяСДиска() {
            ImageDiskCache.Save(Url, Encoding.UTF8.GetBytes("вчерашняя"), null, null);
            ImageLoader.Http = FakeImageHandler.Broken().Client();

            var bytes = await ImageLoader.FetchBytesAsync(Url);

            Assert.Equal("вчерашняя", Encoding.UTF8.GetString(bytes));
        }

        /// <summary>Сети нет и в кеше пусто — честная ошибка, а не пустая картинка.</summary>
        [Fact]
        public async Task БезСетиИБезКешаОшибкаОстаётсяОшибкой() {
            ImageLoader.Http = FakeImageHandler.Broken().Client();

            await Assert.ThrowsAnyAsync<Exception>(() => ImageLoader.FetchBytesAsync(Url));
        }

        /// <summary>Кеш не растёт без предела: сверх потолка вытесняются самые давние.</summary>
        [Fact]
        public void СверхПотолкаКешВытесняетДавние() {
            var big = new byte[10 * 1024 * 1024];
            for (var i = 0; i < 12; i++) {
                ImageDiskCache.Save($"https://images.invalid/{i}.png", big, null, null);
            }

            Assert.True(
                ImageDiskCache.TotalBytes() <= ImageDiskCache.MaxTotalBytes,
                $"кеш занял {ImageDiskCache.TotalBytes()} Б при потолке {ImageDiskCache.MaxTotalBytes} Б");
        }

        /// <summary>Выключенный кеш ничего не пишет и ничего не отдаёт.</summary>
        [Fact]
        public void ВыключенныйКешМолчит() {
            ImageDiskCache.Enabled = false;
            try {
                ImageDiskCache.Save(Url, Encoding.UTF8.GetBytes("что-то"), null, null);

                Assert.Null(ImageDiskCache.Read(Url));
                Assert.Equal(0, ImageDiskCache.TotalBytes());
            }
            finally {
                ImageDiskCache.Enabled = true;
            }
        }

        private static HttpResponseMessage Ok(string body, string? etag) {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            };
            if (etag != null) {
                resp.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return resp;
        }

        private static FakeImageHandler Responder(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => new((req, _) => Task.FromResult(respond(req)));
    }
}
