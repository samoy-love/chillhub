// <copyright file="NewsCacheAndLinksTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Mods;
    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Новости: картинки внутри страницы, текст на диске — и ссылка на модпак.
    /// </summary>
    public class NewsCacheAndLinksTests : IDisposable {
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

        private static ModsInfo Mods(string community, string version) => new ModsInfo {
            HasLatest = true,
            Community = community,
            Version = version,
        };
    }
}
