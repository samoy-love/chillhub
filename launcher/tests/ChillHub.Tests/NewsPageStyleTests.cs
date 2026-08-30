// <copyright file="NewsPageStyleTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Оформление страницы новости.
    /// <para>
    /// Markdig разбирает таблицы, цитаты и списки, и тесты разбора это стерегут.
    /// Но разобрать мало: в шаблоне страницы стилей на них не было, и таблица
    /// состава сборки приезжала слипшимся текстом без рамок, а цитата — обычным
    /// абзацем. Проверяется, что правила на месте и что они берут цвета из темы,
    /// а не зашиты в шаблон.
    /// </para>
    /// </summary>
    public class NewsPageStyleTests {
        private const string Table = "| мод | что делает |\n| --- | --- |\n| `Shy` | прячет |";

        private static NewsPalette Palette() => new NewsPalette(
            Background: "#0F1116",
            Text: "#E5E5E5",
            CodeBackground: "#111111",
            Link: "#8B5CF6",
            LinkHover: "#A78BFA",
            HorizontalRule: "#2A2A2A",
            Surface: "#151821",
            ScrollThumb: "#333333",
            ScrollThumbHover: "#444444");

        /// <summary>Таблица должна получить рамки и отступы, иначе состав сборки нечитаем.</summary>
        [Fact]
        public void ТаблицаПолучаетРамкиИОтступы() {
            var page = NewsPageRenderer.RenderPage(Table, "https://example.test/n.md", Palette());

            Assert.Contains("<table", page, StringComparison.Ordinal);
            Assert.Contains("border-collapse:collapse", page, StringComparison.Ordinal);
            Assert.Contains("th,td{border-bottom:1px solid #2A2A2A", page, StringComparison.Ordinal);
        }

        /// <summary>Широкая таблица прокручивается внутри себя, а не растягивает страницу.</summary>
        [Fact]
        public void ШирокаяТаблицаПрокручиваетсяВнутриСебя() {
            var page = NewsPageRenderer.RenderPage(Table, "https://example.test/n.md", Palette());

            Assert.Contains("table{border-collapse:collapse; width:100%; margin:16px 0; font-size:16px; display:block; overflow-x:auto;}", page, StringComparison.Ordinal);
        }

        /// <summary>Цитата — врезка с полосой акцентного цвета, а не обычный абзац.</summary>
        [Fact]
        public void ЦитатаОформленаВрезкойСАкцентнойПолосой() {
            var page = NewsPageRenderer.RenderPage("> главное в двух строках", "https://example.test/n.md", Palette());

            Assert.Contains("<blockquote>", page, StringComparison.Ordinal);
            Assert.Contains("border-left:3px solid #8B5CF6", page, StringComparison.Ordinal);
        }

        /// <summary>Списками набрано почти всё в новостях — им нужен отступ и воздух между пунктами.</summary>
        [Fact]
        public void СпискиПолучаютОтступИВоздух() {
            var page = NewsPageRenderer.RenderPage("- раз\n- два", "https://example.test/n.md", Palette());

            Assert.Contains("ul,ol{padding-left:22px;}", page, StringComparison.Ordinal);
            Assert.Contains("li{margin:4px 0;}", page, StringComparison.Ordinal);
        }

        /// <summary>Четвёртый уровень заголовков используется в составах сборок по разделам.</summary>
        [Fact]
        public void ЗаголовокЧетвёртогоУровняИмеетСвойРазмер() {
            var page = NewsPageRenderer.RenderPage("#### Раздел", "https://example.test/n.md", Palette());

            Assert.Contains("<h4", page, StringComparison.Ordinal);
            Assert.Contains("h4{font-size:17px", page, StringComparison.Ordinal);
        }
    }
}
