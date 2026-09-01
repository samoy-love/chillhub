// <copyright file="NewsImagesCaseTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;

    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Регистр в адресах картинок новости.
    /// <para>
    /// Ассеты лежат на Linux, где <c>Before.png</c> и <c>before.png</c> — два разных
    /// файла. Пара «было/стало», набранная с разным регистром, показывала одну и ту же
    /// картинку дважды: вторая считалась уже загруженной, и на её место вставали байты
    /// первой.
    /// </para>
    /// </summary>
    public class NewsImagesCaseTests {
        /// <summary>Каждая картинка получает своё содержимое, а не соседнее.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task АдресаРазногоРегистраЭтоРазныеКартинки() {
            const string html = "<p><img src=\"/assets/Before.png\" alt=\"было\" /></p>"
                + "<p><img src=\"/assets/before.png\" alt=\"стало\" /></p>";

            var asked = new List<string>();
            var page = await NewsImages.InlineAsync(html, "https://example.test", url => {
                asked.Add(url);
                // Байты разные, чтобы подмену было видно в самой странице.
                return Task.FromResult(Encoding.UTF8.GetBytes(url.EndsWith("/Before.png", StringComparison.Ordinal) ? "БОЛЬШАЯ" : "малая"));
            });

            Assert.Equal(2, asked.Count);
            Assert.Contains("https://example.test/assets/Before.png", asked);
            Assert.Contains("https://example.test/assets/before.png", asked);

            var big = "base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("БОЛЬШАЯ"));
            var small = "base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("малая"));
            Assert.Contains(big, page, StringComparison.Ordinal);
            Assert.Contains(small, page, StringComparison.Ordinal);
            Assert.DoesNotContain("/assets/Before.png", page, StringComparison.Ordinal);
            Assert.DoesNotContain("/assets/before.png", page, StringComparison.Ordinal);
        }

        /// <summary>Один и тот же адрес по-прежнему скачивается один раз.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПовторЭтогоЖеАдресаНеСкачиваетсяДважды() {
            const string html = "<img src=\"/assets/cat.png\" /><img src=\"/assets/cat.png\" />";

            var asked = 0;
            var page = await NewsImages.InlineAsync(html, "https://example.test", _ => {
                asked++;
                return Task.FromResult(Encoding.UTF8.GetBytes("кот"));
            });

            Assert.Equal(1, asked);
            Assert.DoesNotContain("/assets/cat.png", page, StringComparison.Ordinal);
        }
    }
}
