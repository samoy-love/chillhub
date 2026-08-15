// <copyright file="NewsDetailPageTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Страница новости: загрузка текста с сервера и сборка страницы для WebView2.
    /// <para>
    /// Сам конвейер Markdig уже проверен в <see cref="MarkdownRenderTests"/> — здесь
    /// проверяется всё, что вокруг него: откуда берётся база для адресов картинок (без
    /// неё иллюстрации новости пропадают, потому что NavigateToString отдаёт страницу
    /// без собственного адреса), как выглядят отказы сервера и не превращается ли текст
    /// ошибки в исполняемую разметку.
    /// </para>
    /// </summary>
    public class NewsDetailPageTests {
        /// <summary>
        /// В страницу подставляется база адресов, снятая с адреса самой новости: иначе
        /// «/assets/…» в NavigateToString никуда не ведёт и все картинки пропадают.
        /// </summary>
        [Fact]
        public void АдресаКартинокПолучаютБазуИзАдресаНовости() {
            var page = NewsPageRenderer.RenderPage(
                "![кот](/assets/cat.png)",
                "https://launcher.example.test/news/games/game/patch.md",
                Palette());

            Assert.Contains("<base href='https://launcher.example.test/'>", page, StringComparison.Ordinal);
            Assert.Contains("/assets/cat.png", page, StringComparison.Ordinal);
        }

        /// <summary>
        /// В базу уходит только схема и хост, без пути новости: с путём относительные
        /// адреса ушли бы внутрь папки конкретной статьи.
        /// </summary>
        [Theory]
        [InlineData("https://example.test/news/a.md", "https://example.test")]
        [InlineData("http://localhost:8080/news/games/g/b.md", "http://localhost:8080")]
        public void БазаАдресовЭтоТолькоСхемаИХост(string url, string expected) {
            Assert.Equal(expected, NewsPageRenderer.OriginOf(url));
        }

        /// <summary>Разметка новости попадает в страницу уже разобранной, а не сырым markdown.</summary>
        [Fact]
        public void РазметкаПопадаетВСтраницуРазобранной() {
            var page = NewsPageRenderer.RenderPage("# Заголовок\n\nтекст", "https://example.test/n.md", Palette());

            Assert.Contains("<h1", page, StringComparison.Ordinal);
            Assert.DoesNotContain("# Заголовок", page, StringComparison.Ordinal);
        }

        /// <summary>Цвета темы доезжают до страницы: иначе новость откроется белым листом поверх тёмного окна.</summary>
        [Fact]
        public void ЦветаТемыПопадаютВСтраницу() {
            var page = NewsPageRenderer.RenderPage("текст", "https://example.test/n.md", Palette());

            Assert.Contains("background:#0F1116", page, StringComparison.Ordinal);
            Assert.Contains("color:#E5E5E5", page, StringComparison.Ordinal);
            Assert.Contains("#111111", page, StringComparison.Ordinal);
        }

        /// <summary>Кодировка объявлена явно: без неё кириллица в WebView2 превращается в мусор.</summary>
        [Fact]
        public void КодировкаОбъявленаЯвно() {
            var page = NewsPageRenderer.RenderPage("Обновление вышло", "https://example.test/n.md", Palette());

            Assert.Contains("<meta charset='utf-8'>", page, StringComparison.Ordinal);
            Assert.Contains("Обновление вышло", page, StringComparison.Ordinal);
        }

        /// <summary>Пустая новость даёт пустую, но целую страницу — а не исключение при открытии.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n\n")]
        public void ПустаяНовостьДаётЦелуюСтраницу(string markdown) {
            var page = NewsPageRenderer.RenderPage(markdown, "https://example.test/n.md", Palette());

            Assert.Contains("<div class='wrap'>", page, StringComparison.Ordinal);
            Assert.Contains("</html>", page, StringComparison.Ordinal);
        }

        /// <summary>Адрес без схемы — это отказ сборки страницы, а не страница с пустой базой.</summary>
        [Fact]
        public void АдресБезСхемыОтвергается() {
            Assert.Throws<UriFormatException>(() => NewsPageRenderer.OriginOf("не адрес вовсе"));
        }

        /// <summary>
        /// Сообщение об ошибке экранируется. Текст приходит из ответа сервера, и разметка
        /// в нём не должна становиться частью страницы.
        /// </summary>
        [Fact]
        public void ТекстОшибкиЭкранируется() {
            var page = NewsPageRenderer.RenderError("<script>alert(1)</script>", Palette());

            Assert.DoesNotContain("<script>alert(1)</script>", page, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", page, StringComparison.Ordinal);
        }

        /// <summary>Страница ошибки объясняет, что произошло, и остаётся в цветах темы.</summary>
        [Fact]
        public void СтраницаОшибкиОстаётсяВЦветахТемы() {
            var page = NewsPageRenderer.RenderError("сеть недоступна", Palette());

            Assert.Contains("Не удалось загрузить новость: сеть недоступна", page, StringComparison.Ordinal);
            Assert.Contains("background:#0F1116", page, StringComparison.Ordinal);
        }

        /// <summary>Текст новости забирается по тому адресу, который дала страница списка.</summary>
        [Fact]
        public async Task ТекстНовостиЗабираетсяПоЗаданномуАдресу() {
            var handler = new FakeHandler(_ => Ok("# Патч"));
            var client = new NewsContentClient(new HttpClient(handler));

            Assert.Equal("# Патч", await client.FetchAsync("https://example.test/news/patch.md"));
            Assert.Equal("https://example.test/news/patch.md", Assert.Single(handler.Requests).ToString());
        }

        /// <summary>Нет сети — отказ виден вызывающему, а не подменяется пустой новостью.</summary>
        [Fact]
        public async Task БезСетиЗагрузкаНовостиПадаетИсключением() {
            var client = new NewsContentClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("сеть недоступна"))));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync("https://example.test/news/patch.md"));
        }

        /// <summary>Удалённая новость (404) — тоже отказ: пустая страница выглядела бы как пустая новость.</summary>
        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task ОтказСервераПадаетИсключением(HttpStatusCode code) {
            var client = new NewsContentClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(code))));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync("https://example.test/news/patch.md"));
        }

        /// <summary>
        /// Сервер ответил не markdown, а html-заглушкой прокси — страница всё равно
        /// собирается: разбирать чужой ответ как разметку безопаснее, чем падать.
        /// </summary>
        [Fact]
        public async Task ЧужойОтветНеРоняетСборкуСтраницы() {
            var client = new NewsContentClient(new HttpClient(new FakeHandler(_ => Ok("<html><body>502 Bad Gateway</body></html>"))));

            var body = await client.FetchAsync("https://example.test/news/patch.md");
            var page = NewsPageRenderer.RenderPage(body, "https://example.test/news/patch.md", Palette());

            Assert.Contains("502 Bad Gateway", page, StringComparison.Ordinal);
        }

        /// <summary>
        /// Каталог данных WebView2 лежит в роуминге, а не рядом с exe: самообновление
        /// сносит из папки установки всё, чего нет в манифесте, вместе с этим каталогом.
        /// </summary>
        [Fact]
        public void КаталогДанныхЛежитВРоуминге() {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = NewsWebViewStorage.GetUserDataFolder();

            Assert.StartsWith(roaming, folder, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("WebView2", Path.GetFileName(folder));
        }

        /// <summary>
        /// Убираются оба варианта старого каталога: по имени текущего exe и по историческому
        /// «ChillHub.exe» — переименованная сборка иначе оставила бы чужой каталог навсегда.
        /// </summary>
        [Fact]
        public void СтарыеКаталогиИщутсяПоОбоимИменам() {
            Assert.Equal(
                new[] { "Launcher.exe.WebView2", "ChillHub.exe.WebView2" },
                NewsWebViewStorage.LegacyFolderNames("Launcher.exe"));
        }

        /// <summary>Имя процесса определить не удалось — остаётся историческое имя, а не пустая «.WebView2».</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void БезИмениПроцессаИспользуетсяИсторическоеИмя(string? exeName) {
            Assert.Equal(
                new[] { "ChillHub.exe.WebView2", "ChillHub.exe.WebView2" },
                NewsWebViewStorage.LegacyFolderNames(exeName));
        }

        /// <summary>
        /// Подпись из markdown видна под картинкой. Редакторы пишут «![Было](/assets/old.jpg)»
        /// и ждут подпись на экране, а она уезжала в alt и не показывалась никогда.
        /// </summary>
        [Fact]
        public void ПодписьКартинкиВиднаПодНей() {
            var html = NewsPageRenderer.ToHtml("![Было](/assets/old_launcher.jpg)");

            Assert.Contains("<figcaption>Было</figcaption>", html, StringComparison.Ordinal);
            Assert.Contains("<figure><img", html, StringComparison.Ordinal);
        }

        /// <summary>Две картинки подряд — две подписи, а не одна на обе.</summary>
        [Fact]
        public void КаждаяКартинкаПолучаетСвоюПодпись() {
            var html = NewsPageRenderer.ToHtml("![Было](/a.jpg)\n\n![Стало](/b.jpg)");

            Assert.Contains("<figcaption>Было</figcaption>", html, StringComparison.Ordinal);
            Assert.Contains("<figcaption>Стало</figcaption>", html, StringComparison.Ordinal);
        }

        /// <summary>Картинка без подписи остаётся картинкой, а не пустой полосой под ней.</summary>
        [Fact]
        public void БезПодписиНичегоНеДобавляется() {
            var html = NewsPageRenderer.ToHtml("![](/a.jpg)");

            Assert.DoesNotContain("<figcaption", html, StringComparison.Ordinal);
        }

        /// <summary>
        /// Картинка внутри строки текста — иконка или бейдж, а не иллюстрация:
        /// подпись под ней разорвала бы фразу.
        /// </summary>
        [Fact]
        public void КартинкаВнутриТекстаПодписьНеПолучает() {
            var html = NewsPageRenderer.ToHtml("Смотрите ![значок](/i.png) вот тут");

            Assert.DoesNotContain("<figcaption", html, StringComparison.Ordinal);
        }

        /// <summary>Подпись экранируется: текст новости приходит из админки.</summary>
        [Fact]
        public void ПодписьЭкранируется() {
            var html = NewsPageRenderer.ToHtml("![<script>alert(1)</script>](/a.jpg)");

            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        }

        /// <summary>Стили подписи едут вместе со страницей, иначе figcaption останется без оформления.</summary>
        [Fact]
        public void СтраницаНесётСтилиПодписи() {
            var page = NewsPageRenderer.RenderPage("![Было](/a.jpg)", "https://example.test/n.md", Palette());

            Assert.Contains("figcaption{", page, StringComparison.Ordinal);
        }

        private static NewsPalette Palette() => new NewsPalette(
            Background: "#0F1116",
            Text: "#E5E5E5",
            CodeBackground: "#171B24",
            Link: "#EF4444",
            LinkHover: "#DC2626",
            HorizontalRule: "#262626",
            Surface: "#111111",
            ScrollThumb: "#2E2E2E",
            ScrollThumbHover: "#474747");

        private static HttpResponseMessage Ok(string body) => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "text/markdown"),
        };

        /// <summary>Подставной транспорт: отвечает по заданному правилу и запоминает адреса запросов.</summary>
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

        /// <summary>
        /// Название новости лаунчер уже показал в шапке экрана — тот же заголовок первой
        /// строкой текста стоял под ним вторым, вплотную.
        /// </summary>
        [Fact]
        public void ПовторЗаголовкаИзШапкиУбираетсяИзТекста() {
            var page = NewsPageRenderer.RenderPage(
                "# Лаунчер стартовал\n\nПолный подбор игр", "https://example.test/n.md", Palette(), "Лаунчер стартовал");

            Assert.DoesNotContain("<h1", page, StringComparison.Ordinal);
            Assert.Contains("Полный подбор игр", page, StringComparison.Ordinal);
        }

        /// <summary>Заголовок, отличающийся от названия, — выбор автора, его не трогаем.</summary>
        [Fact]
        public void ДругойЗаголовокОстаётсяВТексте() {
            var page = NewsPageRenderer.RenderPage(
                "# Что нового\n\nтекст", "https://example.test/n.md", Palette(), "Лаунчер стартовал");

            Assert.Contains("Что нового", page, StringComparison.Ordinal);
            Assert.Contains("<h1", page, StringComparison.Ordinal);
        }

        /// <summary>Убирается ровно первый заголовок, а не все совпадающие ниже по тексту.</summary>
        [Fact]
        public void УбираетсяТолькоПервыйЗаголовок() {
            var page = NewsPageRenderer.RenderPage(
                "# Итоги\n\nтекст\n\n# Итоги\n\nещё", "https://example.test/n.md", Palette(), "Итоги");

            Assert.Contains("<h1", page, StringComparison.Ordinal);
            Assert.Contains("ещё", page, StringComparison.Ordinal);
        }

    }
}
