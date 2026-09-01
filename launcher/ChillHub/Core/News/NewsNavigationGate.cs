// <copyright file="NewsNavigationGate.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System;

    /// <summary>
    /// Переходы одной открытой новости: отличает нашу собственную отрисовку от
    /// перехода по ссылке из текста и сам исполняет решение — гасит переход, пишет
    /// след и отдаёт адрес оболочке.
    /// <para>
    /// Отдельным классом, а не полем и парой методов страницы, потому что именно
    /// здесь жил дефект, из-за которого новость открывалась пустой: обработчик
    /// отменял всё, чей адрес не узнавал, — и не узнавал в том числе свою же
    /// страницу. Внутри обработчика WebView2 не проверяется ничто, поэтому у
    /// страницы осталась только проводка события, а порядок «пометили — пришёл
    /// переход — метка снялась» и всё, что за ним следует, держит тест.
    /// </para>
    /// </summary>
    internal sealed class NewsNavigationGate {
        private readonly Action<string> openOutside;
        private readonly Action<string> note;

        private bool ownPageLoadPending;

        /// <summary>Создаёт шлюз одной открытой новости.</summary>
        /// <param name="openOutside">Отдать адрес системному браузеру.</param>
        /// <param name="note">Куда писать след; по умолчанию — общий журнал.</param>
        internal NewsNavigationGate(Action<string> openOutside, Action<string>? note = null) {
            this.openOutside = openOutside;
            this.note = note ?? Logging.Logger.Info;
        }

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
        /// <returns>true, если переход надо отменить.</returns>
        internal bool OnNavigationStarting(string? uri) {
            var own = this.ownPageLoadPending;
            this.ownPageLoadPending = false;
            return this.Execute(NewsLinkPolicy.ForNavigation(uri, own), uri);
        }

        /// <summary>
        /// Решение по попытке открыть новое окно. Метки не касается: своя отрисовка
        /// нового окна не просит, а второе окно не нужно ни при каком адресе.
        /// </summary>
        /// <param name="uri">Адрес из события.</param>
        /// <returns>true — второе окно не появляется никогда.</returns>
        internal bool OnNewWindowRequested(string? uri) => this.Execute(NewsLinkPolicy.ForNewWindow(uri), uri);

        /// <summary>Исполняет решение политики и отвечает, гасить ли переход.</summary>
        /// <param name="action">Что решила политика.</param>
        /// <param name="uri">Адрес из события — только для следа.</param>
        /// <returns>true, если переход надо отменить.</returns>
        private bool Execute(NewsNavigationAction action, string? uri) {
            if (action.Cancel) {
                // След обязателен: погашенный переход, которым оказалась сама новость,
                // выглядит на экране просто пустотой, и объяснить её было нечем — ровно
                // так этот обработчик однажды и стёр страницу целиком.
                this.note($"NewsDetailPage: переход отменён, адрес '{uri}', наружу={action.OpenExternally != null}");
            }

            if (action.OpenExternally != null) {
                this.openOutside(action.OpenExternally);
            }

            return action.Cancel;
        }
    }
}
