// <copyright file="NewsNavigationGate.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    /// <summary>
    /// Состояние переходов одной открытой новости: отличает нашу собственную
    /// отрисовку от перехода по ссылке из текста.
    /// <para>
    /// Отдельным классом, а не полем страницы, потому что именно здесь жил дефект,
    /// из-за которого новость открывалась пустой: обработчик отменял всё, чей адрес
    /// не узнавал, — и не узнавал в том числе свою же страницу. Внутри обработчика
    /// WebView2 это не проверить ничем, а порядок «пометили — пришёл переход — метка
    /// снялась» и есть то, что должно удерживаться тестом.
    /// </para>
    /// </summary>
    internal sealed class NewsNavigationGate {
        private bool ownPageLoadPending;

        /// <summary>
        /// Объявляет, что следующий переход — наша собственная отрисовка страницы.
        /// Зовётся непосредственно перед <c>NavigateToString</c>.
        /// </summary>
        internal void BeginOwnPageLoad() => this.ownPageLoadPending = true;

        /// <summary>
        /// Решение по обычному переходу.
        /// <para>
        /// Метка одноразовая: её снимает первый же пришедший переход, поэтому
        /// следующий — уже настоящий, из ссылки в тексте новости.
        /// </para>
        /// </summary>
        /// <param name="uri">Адрес из события.</param>
        /// <returns>Что сделать с переходом.</returns>
        internal NewsNavigationAction OnNavigationStarting(string? uri) {
            var own = this.ownPageLoadPending;
            this.ownPageLoadPending = false;
            return NewsLinkPolicy.ForNavigation(uri, own);
        }

        /// <summary>
        /// Решение по попытке открыть новое окно. Метки не касается: своя отрисовка
        /// нового окна не просит, а второе окно не нужно ни при каком адресе.
        /// </summary>
        /// <param name="uri">Адрес из события.</param>
        /// <returns>Что сделать с переходом.</returns>
        internal NewsNavigationAction OnNewWindowRequested(string? uri) => NewsLinkPolicy.ForNewWindow(uri);
    }
}
