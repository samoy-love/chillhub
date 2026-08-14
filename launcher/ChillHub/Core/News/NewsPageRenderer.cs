// <copyright file="NewsPageRenderer.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System;

    using Markdig;

    /// <summary>
    /// Цвета страницы новости. Собираются из кистей темы вызывающим кодом: сам рендер
    /// про <c>Application.Current.Resources</c> ничего не знает и проверяется без окна.
    /// </summary>
    /// <param name="Background">Фон окна.</param>
    /// <param name="Text">Основной текст.</param>
    /// <param name="CodeBackground">Фон блоков кода.</param>
    /// <param name="Link">Ссылка.</param>
    /// <param name="LinkHover">Ссылка под курсором.</param>
    /// <param name="HorizontalRule">Разделительная черта.</param>
    /// <param name="Surface">Фон карточки с текстом.</param>
    /// <param name="ScrollThumb">Ползунок полосы прокрутки.</param>
    /// <param name="ScrollThumbHover">Ползунок под курсором.</param>
    internal readonly record struct NewsPalette(
        string Background,
        string Text,
        string CodeBackground,
        string Link,
        string LinkHover,
        string HorizontalRule,
        string Surface,
        string ScrollThumb,
        string ScrollThumbHover);

    /// <summary>
    /// Сборка html-страницы новости из markdown с сервера. Конвейер Markdig и шаблон
    /// страницы — чистые функции: именно они ломаются при смене версии библиотеки
    /// или при правке разметки, и проверять их надо без WebView2.
    /// </summary>
    internal static class NewsPageRenderer {
        /// <summary>Конвейер разбора markdown. Один на процесс: он неизменяемый и потокобезопасный.</summary>
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        /// <summary>
        /// Происхождение адреса новости — то, что подставляется в <c>&lt;base&gt;</c>.
        /// Без него абсолютные пути картинок вида «/assets/…» в NavigateToString никуда не ведут.
        /// </summary>
        /// <param name="markdownUrl">Адрес markdown-файла новости.</param>
        /// <returns>Схема и хост адреса.</returns>
        internal static string OriginOf(string markdownUrl) => new Uri(markdownUrl).GetLeftPart(UriPartial.Authority);

        /// <summary>Превращает markdown в html тем же конвейером, что и страница новости.</summary>
        /// <param name="markdown">Текст новости.</param>
        /// <returns>Html-фрагмент.</returns>
        internal static string ToHtml(string markdown) => Markdown.ToHtml(markdown, Pipeline);

        /// <summary>Собирает страницу новости целиком.</summary>
        /// <param name="markdown">Текст новости с сервера.</param>
        /// <param name="markdownUrl">Адрес, откуда он взят: из него берётся база для картинок.</param>
        /// <param name="palette">Цвета темы.</param>
        /// <returns>Готовый html для NavigateToString.</returns>
        /// <param name="title">Заголовок, который лаунчер уже показал в шапке страницы (может быть пустым).</param>
        internal static string RenderPage(string markdown, string markdownUrl, NewsPalette palette, string? title = null) {
            var html = ToHtml(StripLeadingTitle(markdown, title));

            // Ensure absolute URLs like "/assets/..." resolve correctly in WebView2 when using NavigateToString
            // We inject a <base> tag with the origin derived from the markdown URL.
            var origin = OriginOf(markdownUrl);

            return $@"<html><head><meta charset='utf-8'><base href='{origin}/'>
<style>
  html,body{{height:100%; overflow-x:hidden;}}
  body{{font-family:Segoe UI,Segoe UI Emoji,Arial; margin:0; color:{palette.Text}; background:{palette.Background}; overflow-x:hidden;}}
  .wrap{{width:min(100vw - 24px, 860px); margin:16px auto 28px auto; padding:16px; font-size:17px; line-height:1.7; background:{palette.Surface}; border-radius:8px;}}
  img{{max-width:100%; height:auto; max-height:360px; display:inline-block; margin:16px 0; border-radius:8px;}}
  pre,code{{background:{palette.CodeBackground}; border-radius:6px;}}
  pre{{padding:12px; overflow:auto;}}
  a{{color:{palette.Link}; text-decoration:none;}}
  a:hover{{color:{palette.LinkHover}; text-decoration:underline;}}
  h1{{font-size:24px; margin:20px 0 10px 0;}}
  h2{{font-size:21px; margin:18px 0 8px 0;}}
  h3{{font-size:19px; margin:16px 0 6px 0;}}
  hr{{border:none; border-top:1px solid {palette.HorizontalRule}; margin:20px 0;}}
  /* Themed scrollbars for WebView2 (Chromium) */
  ::-webkit-scrollbar{{ width:8px; height:8px; }}
  ::-webkit-scrollbar-track{{ background: transparent; }}
  ::-webkit-scrollbar-thumb{{ background: {palette.ScrollThumb}; border-radius:8px; }}
  ::-webkit-scrollbar-thumb:hover{{ background: {palette.ScrollThumbHover}; }}
</style></head><body><div class='wrap'>{html}</div>
<script>
  // Signal when everything is loaded (including images)
  window.addEventListener('load', function(){{
    try {{ chrome.webview.postMessage('loaded'); }} catch(e) {{}}
  }});
</script>
</body></html>";
        }

        /// <summary>
        /// Убирает первый заголовок первого уровня, если он дословно повторяет название,
        /// уже показанное в шапке экрана.
        /// <para>
        /// Название новости лаунчер рисует сам, а редакторы почти всегда начинают текст
        /// тем же заголовком — и в открытой новости он стоял дважды подряд. Убираем
        /// ровно совпадающий и ровно первый: заголовок, отличающийся от названия, — это
        /// осознанный выбор автора, и трогать его нельзя.
        /// </para>
        /// </summary>
        /// <param name="markdown">Исходный текст новости.</param>
        /// <param name="title">Название из шапки.</param>
        /// <returns>Текст без дублирующего заголовка.</returns>
        internal static string StripLeadingTitle(string markdown, string? title) {
            if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(title)) {
                return markdown;
            }

            var text = markdown.TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (!text.StartsWith("# ", StringComparison.Ordinal)) {
                return markdown;
            }

            var lineEnd = text.IndexOf('\n');
            var firstLine = lineEnd < 0 ? text : text[..lineEnd];
            var heading = firstLine[2..].Trim().TrimEnd('#').Trim();
            if (!string.Equals(heading, title!.Trim(), StringComparison.OrdinalIgnoreCase)) {
                return markdown;
            }

            return lineEnd < 0 ? string.Empty : text[(lineEnd + 1)..].TrimStart('\r', '\n');
        }

        /// <summary>
        /// Страница с сообщением об ошибке. Текст исключения экранируется: он приходит
        /// из ответа сервера и может содержать разметку.
        /// </summary>
        /// <param name="message">Сообщение об ошибке.</param>
        /// <param name="palette">Цвета темы.</param>
        /// <returns>Готовый html для NavigateToString.</returns>
        internal static string RenderError(string message, NewsPalette palette)
            => $"<html><body style='background:{palette.Background};color:{palette.Text};font-family:Segoe UI,Arial'><p>Не удалось загрузить новость: {System.Net.WebUtility.HtmlEncode(message)}</p></body></html>";
    }
}
