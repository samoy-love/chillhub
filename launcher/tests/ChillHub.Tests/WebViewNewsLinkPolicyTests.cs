// <copyright file="WebViewNewsLinkPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Что страница новости может сделать с лаунчером.
    /// <para>
    /// Текст новости приходит с сервера и показывается в WebView2 внутри окна
    /// лаунчера — без адресной строки и без кнопки «назад». Значит, ссылка из текста
    /// не должна ни уводить это окно на чужой сайт, ни запускать через оболочку
    /// программу: <c>file://</c>, <c>steam:</c> и <c>ms-*</c> оболочка открывает
    /// молча, не спросив игрока.
    /// </para>
    /// </summary>
    public class WebViewNewsLinkPolicyTests {
        /// <summary>Наружу отдаётся только то, что редактор и имеет в виду под ссылкой.</summary>
        /// <param name="uri">Адрес перехода.</param>
        [Theory]
        [InlineData("https://example.test/patch")]
        [InlineData("HTTPS://EXAMPLE.TEST/patch")]
        [InlineData("mailto:support@example.test")]
        public void ВБраузерУходятТолькоHttpsИMailto(string uri) {
            Assert.Equal(NewsLinkDecision.OpenInBrowser, NewsLinkPolicy.Decide(uri));
        }

        /// <summary>
        /// Остальные схемы не уезжают ни в оболочку, ни в сам WebView. Каждая строка
        /// здесь — это программа, которую текст новости мог бы запустить у игрока.
        /// </summary>
        /// <param name="uri">Адрес перехода.</param>
        [Theory]
        [InlineData("file:///C:/Windows/System32/cmd.exe")]
        [InlineData("file://server/share/setup.exe")]
        [InlineData("steam://run/570")]
        [InlineData("ms-settings:windowsupdate")]
        [InlineData("ms-msdt:/id")]
        [InlineData("javascript:alert(1)")]
        [InlineData("http://example.test/patch")]
        [InlineData("chillhub://install")]
        [InlineData("\\\\server\\share\\setup.exe")]
        [InlineData("не адрес вовсе")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ОстальноеНеУходитНикуда(string? uri) {
            Assert.Equal(NewsLinkDecision.Block, NewsLinkPolicy.Decide(uri));
        }

        /// <summary>
        /// Сама отрисованная страница переходом не считается: NavigateToString отдаёт её
        /// без собственного адреса, и отмена этого перехода оставила бы пустое окно.
        /// </summary>
        /// <param name="uri">Адрес, с которым WebView2 показывает саму страницу.</param>
        [Theory]
        [InlineData("about:blank")]
        [InlineData("data:text/html;charset=utf-8;base64,PGI+0L/RgNC40LLQtdGCPC9iPg==")]
        public void СамаСтраницаПоказываетсяКакБыла(string uri) {
            Assert.Equal(NewsLinkDecision.Show, NewsLinkPolicy.Decide(uri));
        }

        /// <summary>
        /// Обычный левый клик поднимает NavigationStarting, а не NewWindowRequested:
        /// без подписки на него WebView уходил со страницы новости на внешний сайт
        /// внутри окна лаунчера — без адресной строки и без кнопки «назад».
        /// <para>
        /// Проверяется РЕШЕНИЕ, а не текст исходника. Раньше здесь стоял поиск
        /// подстрок в NewsDetailPage.xaml.cs, и убранный <c>ev.Cancel = true</c>
        /// оставлял весь набор зелёным при полностью вернувшемся дефекте.
        /// </para>
        /// </summary>
        /// <param name="uri">Адрес перехода.</param>
        /// <param name="cancel">Ожидаемая отмена перехода.</param>
        /// <param name="openExternally">Ожидаемый адрес для оболочки; null — не отдавать.</param>
        [Theory]
        [InlineData("https://samoy.love/news", true, "https://samoy.love/news")]
        [InlineData("mailto:hi@samoy.love", true, "mailto:hi@samoy.love")]
        [InlineData("http://samoy.love", true, null)]
        [InlineData("file:///C:/Windows/System32/cmd.exe", true, null)]
        [InlineData("steam://run/1966720", true, null)]
        [InlineData("javascript:alert(1)", true, null)]
        [InlineData("ms-settings:windowsupdate", true, null)]
        [InlineData("", true, null)]
        [InlineData("about:blank", false, null)]
        public void ПереходСоСтраницыРешаетсяОдинаково(string uri, bool cancel, string? openExternally) {
            var action = NewsLinkPolicy.ForNavigation(uri);

            Assert.Equal(cancel, action.Cancel);
            Assert.Equal(openExternally, action.OpenExternally);
        }

        /// <summary>
        /// Новое окно WebView2 лаунчеру не нужно ни при каком адресе: отмена всегда,
        /// а наружу уходит только то, что разрешила политика.
        /// </summary>
        /// <param name="uri">Адрес перехода.</param>
        /// <param name="openExternally">Ожидаемый адрес для оболочки; null — не отдавать.</param>
        [Theory]
        [InlineData("https://samoy.love/news", "https://samoy.love/news")]
        [InlineData("mailto:hi@samoy.love", "mailto:hi@samoy.love")]
        [InlineData("file:///C:/Windows/System32/cmd.exe", null)]
        [InlineData("steam://run/1966720", null)]
        [InlineData("about:blank", null)]
        public void НовоеОкноНеОткрываетсяНикогда(string uri, string? openExternally) {
            var action = NewsLinkPolicy.ForNewWindow(uri);

            Assert.True(action.Cancel, "второе окно WebView2 не открывается ни при каком адресе");
            Assert.Equal(openExternally, action.OpenExternally);
        }

        /// <summary>
        /// Сама отрисованная страница — единственный переход, который НЕ отменяется:
        /// отменив его, лаунчер показал бы пустое окно вместо новости.
        /// </summary>
        [Fact]
        public void СамаСтраницаНеОтменяетсяИНикудаНеУходит() {
            var action = NewsLinkPolicy.ForNavigation("about:blank");

            Assert.False(action.Cancel);
            Assert.Null(action.OpenExternally);
        }
    }
}
