// <copyright file="WebViewNewsRawHtmlTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Сырой html из текста новости.
    /// <para>
    /// Markdown по спецификации пропускает html насквозь, а страница новости
    /// показывается в окне лаунчера. Скрипт, фрейм или форма из текста новости
    /// оказались бы там на правах части лаунчера, поэтому конвейер новости html не
    /// исполняет, а выводит как текст — ровно как предпросмотр в админке.
    /// </para>
    /// </summary>
    public class WebViewNewsRawHtmlTests {
        /// <summary>Разметка из текста новости доезжает до страницы текстом, а не тегом.</summary>
        /// <param name="markdown">Текст новости.</param>
        /// <param name="tag">Тег, которого на странице быть не должно.</param>
        [Theory]
        [InlineData("<script>alert(1)</script>", "<script")]
        [InlineData("<iframe src=\"https://example.test\"></iframe>", "<iframe")]
        [InlineData("<form action=\"https://example.test\"><input></form>", "<form")]
        [InlineData("<style>body{display:none}</style>", "<style")]
        [InlineData("текст <img src=x onerror=alert(1)> дальше", "<img src=x")]
        public void СыройHtmlНеИсполняется(string markdown, string tag) {
            var html = NewsPageRenderer.ToHtml(markdown);

            Assert.DoesNotContain(tag, html, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Экранированный html виден игроку как написано, а не пропадает.</summary>
        [Fact]
        public void ЭкранированныйHtmlОстаётсяВидимым() {
            var html = NewsPageRenderer.ToHtml("<b>жирный</b>");

            Assert.Contains("&lt;b&gt;жирный&lt;/b&gt;", html, StringComparison.Ordinal);
        }

        /// <summary>Обычная разметка новости от этого не страдает.</summary>
        /// <param name="markdown">Текст новости.</param>
        /// <param name="expected">Тег, который должен появиться.</param>
        [Theory]
        [InlineData("**жирный**", "<strong>")]
        [InlineData("- пункт", "<ul>")]
        [InlineData("[ссылка](https://example.test)", "<a href=")]
        [InlineData("| а | б |\n| --- | --- |\n| 1 | 2 |", "<table")]
        public void ОбычнаяРазметкаРаботаетКакИРаботала(string markdown, string expected) {
            var html = NewsPageRenderer.ToHtml(markdown);

            Assert.Contains(expected, html, StringComparison.Ordinal);
        }
    }
}
