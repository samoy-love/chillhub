// <copyright file="NewsCacheAndLinksTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;

    using ChillHub.Core.Mods;
    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Новости: картинки внутри страницы, текст на диске — и ссылка на модпак.
    /// </summary>
    public class NewsCacheAndLinksTests : IDisposable {
        private const string Url = "https://news.invalid/story.md";

        private readonly string dir = Path.Combine(Path.GetTempPath(), "chillhub-news-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable scope;

        public NewsCacheAndLinksTests() {
            Directory.CreateDirectory(this.dir);
            this.scope = NewsContentCache.OverrideDirForTests(this.dir);
        }

        public void Dispose() {
            this.scope.Dispose();
            try {
                Directory.Delete(this.dir, recursive: true);
            }
            catch (IOException) {
                // Временный каталог — не повод валить прогон.
            }
        }

        /// <summary>
        /// ГЛАВНОЕ ПРО КАРТИНКИ. Страница отдаётся в WebView2 строкой, и подгрузить по
        /// ссылке картинку ей не дают — на экране оставались пустые места. Теперь
        /// картинка лежит внутри страницы.
        /// </summary>
        [Fact]
        public async Task КартинкиВкладываютсяВСтраницу() {
            var html = "<p><img src=\"/assets/one.png\" alt=\"\"/></p>";
            var bytes = new byte[] { 1, 2, 3 };

            var inlined = await NewsImages.InlineAsync(html, "https://news.invalid", _ => Task.FromResult(bytes));

            Assert.Contains("data:image/png;base64," + Convert.ToBase64String(bytes), inlined, StringComparison.Ordinal);
            Assert.DoesNotContain("/assets/one.png", inlined, StringComparison.Ordinal);
        }

        /// <summary>Один адрес — одна загрузка, сколько бы раз он ни встретился в тексте.</summary>
        [Fact]
        public async Task ОдинАдресКачаетсяОдинРаз() {
            var html = "<img src=\"/a.png\"/><img src=\"/a.png\"/><img src=\"/b.png\"/>";
            var asked = new List<string>();

            await NewsImages.InlineAsync(html, "https://news.invalid", url => {
                lock (asked) {
                    asked.Add(url);
                }

                return Task.FromResult(new byte[] { 7 });
            });

            Assert.Equal(2, asked.Count);
        }

        /// <summary>
        /// Не достали картинку — ссылка остаётся как была: пустое место хуже, чем
        /// картинка, которая, может быть, ещё загрузится.
        /// </summary>
        [Fact]
        public async Task НедоступнаяКартинкаОстаётсяСсылкой() {
            var html = "<img src=\"https://news.invalid/x.png\"/>";

            var inlined = await NewsImages.InlineAsync(
                html, "https://news.invalid", _ => Task.FromException<byte[]>(new IOException("нет сети")));

            Assert.Equal(html, inlined);
        }

        /// <summary>Уже вложенную картинку второй раз не трогаем.</summary>
        [Fact]
        public async Task УжеВложеннуюКартинкуНеТрогают() {
            var html = "<img src=\"data:image/png;base64,AAAA\"/>";

            var inlined = await NewsImages.InlineAsync(html, "https://news.invalid", _ => Task.FromResult(new byte[] { 9 }));

            Assert.Equal(html, inlined);
        }

        /// <summary>
        /// ГЛАВНОЕ ПРО КЕШ. Сохранённая новость читается с диска — вместе с метками
        /// сверки, по которым следующий запрос спросит сервер «а не менялось ли».
        /// </summary>
        [Fact]
        public void НовостьЛожитсяНаДискИЧитаетсяОттуда() {
            NewsContentCache.Save("https://news.invalid/a.md", "# Заголовок", "\"tag-1\"", "Mon, 01 Jan 2026 00:00:00 GMT");

            var cached = NewsContentCache.Read("https://news.invalid/a.md");

            Assert.NotNull(cached);
            Assert.Equal("# Заголовок", cached!.Text);
            Assert.Equal("\"tag-1\"", cached.ETag);
            Assert.Equal("Mon, 01 Jan 2026 00:00:00 GMT", cached.LastModified);
        }

        /// <summary>Про новость, которой в кеше нет, сказать нечего.</summary>
        [Fact]
        public void НесохранённаяНовостьВКешеНеЧислится() {
            Assert.Null(NewsContentCache.Read("https://news.invalid/never.md"));
        }

        /// <summary>
        /// «Не менялось» продлевает метки, не трогая текст: тело такого ответа сервер
        /// не присылает, и перезаписать текст было бы нечем.
        /// </summary>
        [Fact]
        public void ОтветНеМенялосьПродлеваетМеткиИНеТеряетТекст() {
            NewsContentCache.Save("https://news.invalid/a.md", "текст", "\"old\"", null);

            NewsContentCache.Touch("https://news.invalid/a.md", "\"new\"", "Tue, 02 Jan 2026 00:00:00 GMT");

            var cached = NewsContentCache.Read("https://news.invalid/a.md");
            Assert.Equal("текст", cached!.Text);
            Assert.Equal("\"new\"", cached.ETag);
        }

        /// <summary>Пустой текст в кеш не кладём: пустая новость неотличима от сломанной.</summary>
        [Fact]
        public void ПустаяНовостьВКешНеПопадает() {
            NewsContentCache.Save("https://news.invalid/empty.md", string.Empty, null, null);

            Assert.Null(NewsContentCache.Read("https://news.invalid/empty.md"));
        }

        /// <summary>
        /// Ссылка на модпак собирается из имени версии и слага сообщества. Дефисы
        /// бывают внутри и команды, и пакета — режем от краёв.
        /// </summary>
        [Theory]
        [InlineData("repo", "vcMoo-Moo_Modpack-1.9.9", "https://thunderstore.io/c/repo/p/vcMoo/Moo_Modpack/")]
        [InlineData("peak", "Lart_Iste-PeakFriendsEdition-1.8.13", "https://thunderstore.io/c/peak/p/Lart_Iste/PeakFriendsEdition/")]
        public void СсылкаНаМодпакСобираетсяИзИмениВерсии(string community, string version, string expected) {
            Assert.Equal(expected, ModsLink.PackagePage(Mods(community, version)));
        }

        /// <summary>
        /// Слага сообщества нет — ссылки нет. По нашему идентификатору игры его не
        /// вывести («risk-of-rain-2» там зовётся «riskofrain2»), а угаданная ссылка
        /// ведёт в никуда: это хуже, чем её отсутствие.
        /// </summary>
        [Fact]
        public void БезСлагаСообществаСсылкиНет() {
            Assert.Empty(ModsLink.PackagePage(Mods(string.Empty, "vcMoo-Moo_Modpack-1.9.9")));
            Assert.Empty(ModsLink.PackagePage(null));
        }

        /// <summary>Имя версии не той формы ссылку не даёт — вести по ней некуда.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("простоимя")]
        [InlineData("Team-1.0.0")]
        public void НепонятноеИмяВерсииСсылкиНеДаёт(string version) {
            Assert.Empty(ModsLink.PackagePage(Mods("repo", version)));
        }

        /// <summary>Строка «Модпак» на странице игры: имя и ссылка.</summary>
        [Fact]
        public void СтрокаМодпакаНесётИмяИСсылку() {
            var row = ModsLink.RowFor(Mods("repo", "vcMoo-Moo_Modpack-1.9.9"));

            Assert.True(row.Visible);
            Assert.Equal("https://thunderstore.io/c/repo/p/vcMoo/Moo_Modpack/", row.Url);
            Assert.NotEmpty(row.Name);
        }

        /// <summary>
        /// У игры без модпака строки нет вовсе: пустое «Модпак: —» не рассказывает о
        /// ней ничего.
        /// </summary>
        [Fact]
        public void БезМодпакаСтрокиНет() {
            Assert.False(ModsLink.RowFor(null).Visible);
            Assert.False(ModsLink.RowFor(new ModsInfo { HasLatest = false }).Visible);
        }

        /// <summary>Нет слага — имя остаётся, ссылки нет: вести в никуда мы не будем.</summary>
        [Fact]
        public void БезСлагаИмяОстаётсяБезСсылки() {
            var row = ModsLink.RowFor(Mods(string.Empty, "vcMoo-Moo_Modpack-1.9.9"));

            Assert.True(row.Visible);
            Assert.NotEmpty(row.Name);
            Assert.Empty(row.Url);
        }

        /// <summary>Тип картинки берётся из расширения — он нужен браузеру в самой строке.</summary>
        [Theory]
        [InlineData("/a.png", "image/png")]
        [InlineData("/a.gif", "image/gif")]
        [InlineData("/a.webp", "image/webp")]
        [InlineData("/a.svg", "image/svg+xml")]
        [InlineData("/a.jpg", "image/jpeg")]
        [InlineData("/a.png?v=2", "image/png")]
        public async Task ТипКартинкиБерётсяИзРасширения(string src, string mime) {
            var inlined = await NewsImages.InlineAsync(
                $"<img src=\"{src}\"/>", "https://news.invalid", _ => Task.FromResult(new byte[] { 1 }));

            Assert.Contains("data:" + mime + ";base64,", inlined, StringComparison.Ordinal);
        }

        /// <summary>
        /// Слишком тяжёлая картинка остаётся ссылкой: страница уезжает в WebView2 одной
        /// строкой, и вложенная в неё громадина сделала бы неподъёмной всю страницу.
        /// </summary>
        [Fact]
        public async Task СлишкомТяжёлаяКартинкаОстаётсяСсылкой() {
            var html = "<img src=\"/big.png\"/>";
            var heavy = new byte[NewsImages.MaxInlineBytes + 1];

            var inlined = await NewsImages.InlineAsync(html, "https://news.invalid", _ => Task.FromResult(heavy));

            Assert.Equal(html, inlined);
        }

        /// <summary>Пустой странице и странице без картинок подстановка не вредит.</summary>
        [Fact]
        public async Task СтраницаБезКартинокОстаётсяПрежней() {
            Assert.Equal(string.Empty, await NewsImages.InlineAsync(string.Empty, "https://news.invalid", _ => Task.FromResult(new byte[] { 1 })));

            var plain = "<p>текст без картинок</p>";
            Assert.Equal(plain, await NewsImages.InlineAsync(plain, "https://news.invalid", _ => Task.FromResult(new byte[] { 1 })));
        }

        /// <summary>Выключенный кеш новостей ничего не пишет и ничего не отдаёт.</summary>
        [Fact]
        public void ВыключенныйКешМолчит() {
            NewsContentCache.Enabled = false;
            try {
                NewsContentCache.Save("https://news.invalid/off.md", "текст", null, null);
                Assert.Null(NewsContentCache.Read("https://news.invalid/off.md"));
            }
            finally {
                NewsContentCache.Enabled = true;
            }
        }

        /// <summary>
        /// Старые новости вытесняются: столько никто не открывает, а каталог рос бы без
        /// границы.
        /// </summary>
        [Fact]
        public void СтарыеНовостиВытесняются() {
            for (var i = 0; i < NewsContentCache.MaxEntries + 5; i++) {
                NewsContentCache.Save($"https://news.invalid/{i}.md", "текст " + i, null, null);
            }

            Assert.True(
                Directory.GetFiles(this.dir, "*.json").Length <= NewsContentCache.MaxEntries,
                "в кеше осталось больше записей, чем разрешено");
        }

        /// <summary>Продлевать метки нечему, если новости в кеше нет.</summary>
        [Fact]
        public void ПродлениеМетокБезЗаписиНичегоНеСоздаёт() {
            NewsContentCache.Touch("https://news.invalid/absent.md", "\"tag\"", null);

            Assert.Null(NewsContentCache.Read("https://news.invalid/absent.md"));
        }

        /// <summary>
        /// ГЛАВНОЕ ПРО СЕТЬ. Второй заход спрашивает сервер условным запросом, а на
        /// ответ «не менялось» отдаёт сохранённое: тела у такого ответа нет.
        /// </summary>
        [Fact]
        public async Task ВторойЗаходСпрашиваетУсловноИБерётСохранённое() {
            var asked = new List<HttpRequestMessage>();
            var net = new ScriptedNews(asked, req =>
                asked.Count == 1
                    ? Ok("# первый", "\"tag-1\"")
                    : new HttpResponseMessage(HttpStatusCode.NotModified));
            var client = new NewsContentClient(new HttpClient(net));

            Assert.Equal("# первый", await client.FetchAsync(Url));
            Assert.Equal("# первый", await client.FetchAsync(Url));

            Assert.Equal(2, asked.Count);
            Assert.Contains("\"tag-1\"", asked[1].Headers.IfNoneMatch.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Сети нет — открытая однажды новость всё равно открывается. Отказ вместо уже
        /// сохранённого текста не даёт ничего.
        /// </summary>
        [Fact]
        public async Task БезСетиНовостьБерётсяСДиска() {
            var asked = new List<HttpRequestMessage>();
            var fine = new NewsContentClient(new HttpClient(new ScriptedNews(asked, _ => Ok("# текст", null))));
            Assert.Equal("# текст", await fine.FetchAsync(Url));

            var broken = new NewsContentClient(new HttpClient(new DeadNews()));
            Assert.Equal("# текст", await broken.FetchAsync(Url));
        }

        /// <summary>
        /// Новости в кеше нет и сети нет — отказ выходит наружу: показать вместо текста
        /// нечего, и молчать об этом нельзя.
        /// </summary>
        [Fact]
        public async Task БезКешаИБезСетиОтказВыходитНаружу() {
            var client = new NewsContentClient(new HttpClient(new DeadNews()));

            await Assert.ThrowsAnyAsync<Exception>(() => client.FetchAsync(Url));
        }

        /// <summary>Сервер без ETag сверяется по дате изменения.</summary>
        [Fact]
        public async Task БезETagСверкаИдётПоДате() {
            var asked = new List<HttpRequestMessage>();
            var net = new ScriptedNews(asked, _ => {
                var ok = Ok("# текст", null);
                ok.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                return ok;
            });
            var client = new NewsContentClient(new HttpClient(net));

            await client.FetchAsync(Url);
            await client.FetchAsync(Url);

            Assert.NotNull(asked[1].Headers.IfModifiedSince);
        }

        private static HttpResponseMessage Ok(string text, string? etag) {
            var response = new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(text, Encoding.UTF8),
            };
            if (etag != null) {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return response;
        }

        /// <summary>Сеть, которая отвечает по сценарию и запоминает запросы.</summary>
        private sealed class ScriptedNews : HttpMessageHandler {
            private readonly List<HttpRequestMessage> asked;
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

            internal ScriptedNews(List<HttpRequestMessage> asked, Func<HttpRequestMessage, HttpResponseMessage> respond) {
                this.asked = asked;
                this.respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                this.asked.Add(request);
                return Task.FromResult(this.respond(request));
            }
        }

        /// <summary>Сеть, которой нет.</summary>
        private sealed class DeadNews : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(new HttpRequestException("сеть недоступна"));
        }

        private static ModsInfo Mods(string community, string version) => new ModsInfo {
            HasLatest = true,
            Community = community,
            Version = version,
        };
    }
}
