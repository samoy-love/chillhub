// <copyright file="MarkdownRenderTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using Markdig;

    using Xunit;

    /// <summary>
    /// Рендеринг новостей.
    /// <para>
    /// Страница новости прогоняет markdown с сервера через Markdig и показывает
    /// результат в WebView2. Саму страницу без окна не проверить, но конвейер —
    /// это чистый статический вызов, и именно он ломается при смене версии
    /// библиотеки. Markdig поднимался с 0.31 сразу до 1.3, поэтому набор
    /// проверяется явно: у новостей нет ни одного другого теста, и поломка
    /// вылезла бы только у пользователя, увидевшего пустую или сырую страницу.
    /// </para>
    /// <para>
    /// Конвейер здесь строится ТОЧНО так же, как в NewsDetailPage: любое
    /// расхождение обесценит эти проверки.
    /// </para>
    /// </summary>
    public class MarkdownRenderTests {
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        /// <summary>Базовая разметка превращается в HTML, а не остаётся текстом.</summary>
        [Theory]
        [InlineData("# Заголовок", "<h1")]
        [InlineData("**жирный**", "<strong>")]
        [InlineData("*курсив*", "<em>")]
        [InlineData("- пункт", "<ul>")]
        [InlineData("1. пункт", "<ol>")]
        [InlineData("[ссылка](https://example.com)", "<a href=")]
        [InlineData("![кот](cat.png)", "<img")]
        [InlineData("> цитата", "<blockquote>")]
        [InlineData("`код`", "<code>")]
        [InlineData("---", "<hr")]
        public void РазметкаПревращаетсяВHtml(string md, string expectedFragment) {
            var html = Markdown.ToHtml(md, Pipeline);
            Assert.Contains(expectedFragment, html, StringComparison.Ordinal);
        }

        /// <summary>
        /// Таблицы — это расширение, включаемое UseAdvancedExtensions. Если конвейер
        /// соберут без него, таблица тихо превратится в строку с палками.
        /// </summary>
        [Fact]
        public void ТаблицыРендерятсяРасширением() {
            const string md = "| а | б |\n| --- | --- |\n| 1 | 2 |";
            var html = Markdown.ToHtml(md, Pipeline);
            Assert.Contains("<table", html, StringComparison.Ordinal);
        }

        /// <summary>Кириллица не должна превращаться в мусор или в html-сущности.</summary>
        [Fact]
        public void КириллицаСохраняется() {
            var html = Markdown.ToHtml("Обновление лаунчера вышло", Pipeline);
            Assert.Contains("Обновление лаунчера вышло", html, StringComparison.Ordinal);
        }

        /// <summary>Пустая новость даёт пустой результат, а не исключение.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n\n")]
        public void ПустойВводНеРоняетРендеринг(string md) {
            Assert.NotNull(Markdown.ToHtml(md, Pipeline));
        }

        /// <summary>
        /// Текст новости приходит из админки. Сырой HTML в нём markdown пропускает по
        /// спецификации, поэтому единственная защита — WebView2, который открывает
        /// страницу в изолированном контексте без доступа к приложению. Тест
        /// фиксирует это как ЗНАЕМОЕ поведение, а не как дыру, которую забыли:
        /// если однажды сюда добавят санитайзер, тест напомнит проверить, что
        /// он не сломал легитимную разметку.
        /// </summary>
        [Fact]
        public void СыройHtmlПроходитНасквозьЭтоОжидаемо() {
            var html = Markdown.ToHtml("<b>жирный</b>", Pipeline);
            Assert.Contains("<b>жирный</b>", html, StringComparison.Ordinal);
        }
    }
}
