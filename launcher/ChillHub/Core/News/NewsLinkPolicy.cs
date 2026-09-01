// <copyright file="NewsLinkPolicy.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System;

    /// <summary>Что делать с адресом, по которому уходит страница новости.</summary>
    internal enum NewsLinkDecision {
        /// <summary>Ничего не делать: это сама отрисованная страница.</summary>
        Show,

        /// <summary>Отдать системному браузеру и никуда не переходить.</summary>
        OpenInBrowser,

        /// <summary>Не переходить и никому не отдавать.</summary>
        Block,
    }

    /// <summary>
    /// Решает, что делать со ссылкой из текста новости.
    /// <para>
    /// Страница новости открыта в окне лаунчера, где нет ни адресной строки, ни кнопки
    /// «назад»: если WebView2 уйдёт по ссылке, игрок останется на чужом сайте без
    /// единого признака, что он уже не в лаунчере. Поэтому сам WebView со страницы не
    /// уходит никуда, а ссылку открывает системный браузер.
    /// </para>
    /// <para>
    /// Наружу через оболочку уходят только <c>https</c> и <c>mailto</c>. Оболочка
    /// запускает по адресу что угодно — <c>file://</c>, <c>steam:</c>, <c>ms-*</c>
    /// заводят стороннюю программу молча, без вопроса игроку, а адрес приходит из
    /// текста новости. Разрешаем ровно два вида, которые редактор и имеет в виду,
    /// когда ставит ссылку.
    /// </para>
    /// </summary>
    /// <summary>
    /// Что страница делает с попыткой перехода: отменять ли её и что отдать оболочке.
    /// </summary>
    /// <param name="Cancel">Отменить переход, оставив WebView на странице новости.</param>
    /// <param name="OpenExternally">Адрес для системного браузера; null — не отдавать ничего.</param>
    internal readonly record struct NewsNavigationAction(bool Cancel, string? OpenExternally);

    internal static class NewsLinkPolicy {
        /// <summary>
        /// Что делать с обычным переходом (событие <c>NavigationStarting</c>).
        /// <para>
        /// Отдельно от <see cref="Decide"/>, потому что решение — это ДВА действия
        /// сразу: отменить переход и, возможно, отдать адрес оболочке. Пока они жили
        /// прямо в обработчике, проверить их было нечем: тест мог убедиться только
        /// в том, что в исходнике есть нужные строки, и убранный <c>ev.Cancel</c>
        /// оставлял гейт зелёным при полностью вернувшемся дефекте — WebView уходил
        /// на чужой сайт внутри окна лаунчера, да ещё и открывал его вторым окном
        /// в браузере.
        /// </para>
        /// </summary>
        /// <param name="uri">Адрес из события.</param>
        /// <param name="ownPageLoad">Это наша собственная отрисовка страницы новости.</param>
        /// <returns>Что сделать.</returns>
        internal static NewsNavigationAction ForNavigation(string? uri, bool ownPageLoad) {
            // СВОЯ ОТРИСОВКА ПРОХОДИТ ВСЕГДА, и решает это флаг, а не адрес.
            //
            // Адрес нашей же страницы приходит от движка, а не от нас: NavigateToString
            // не даёт странице настоящего адреса, и какой именно суррогат окажется в
            // событии — about:blank, data:text/html или что-то третье, — зависит от
            // версии среды WebView2. Ровно на этом всё и сломалось: правило не узнало
            // свою страницу, отменило её переход, и вместо новости открывалась пустота.
            // Угадывать здесь нечего: кто вызвал отрисовку, знает сам вызывающий.
            if (ownPageLoad) {
                return new NewsNavigationAction(false, null);
            }

            return Decide(uri) switch {
                NewsLinkDecision.Show => new NewsNavigationAction(false, null),
                NewsLinkDecision.OpenInBrowser => new NewsNavigationAction(true, uri),
                _ => new NewsNavigationAction(true, null),
            };
        }

        /// <summary>
        /// Что делать с попыткой открыть новое окно (событие <c>NewWindowRequested</c>).
        /// <para>
        /// Отмена здесь всегда: второе окно WebView2 лаунчеру не нужно ни при каком
        /// адресе. Отличается только то, уходит ли адрес наружу.
        /// </para>
        /// </summary>
        /// <param name="uri">Адрес из события.</param>
        /// <returns>Что сделать.</returns>
        internal static NewsNavigationAction ForNewWindow(string? uri)
            => new NewsNavigationAction(true, Decide(uri) == NewsLinkDecision.OpenInBrowser ? uri : null);

        /// <summary>
        /// Разбирает адрес перехода.
        /// </summary>
        /// <param name="uri">Адрес из события WebView2.</param>
        /// <returns>Решение по этому адресу.</returns>
        internal static NewsLinkDecision Decide(string? uri) {
            if (string.IsNullOrWhiteSpace(uri)) {
                return NewsLinkDecision.Block;
            }

            if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed)) {
                return NewsLinkDecision.Block;
            }

            // Сама страница: NavigateToString отдаёт её без адреса, и переход к ней
            // WebView2 показывает то как about:blank, то как data:text/html. Отменить
            // этот переход — значит оставить окно пустым.
            if (IsRenderedPage(parsed)) {
                return NewsLinkDecision.Show;
            }

            return parsed.Scheme switch {
                "https" => NewsLinkDecision.OpenInBrowser,
                "mailto" => NewsLinkDecision.OpenInBrowser,
                _ => NewsLinkDecision.Block,
            };
        }

        /// <summary>Это адрес самой отрисованной страницы, а не переход с неё.</summary>
        /// <param name="uri">Разобранный адрес.</param>
        /// <returns><c>true</c>, если переход ведёт в саму страницу новости.</returns>
        private static bool IsRenderedPage(Uri uri) {
            if (string.Equals(uri.Scheme, "about", StringComparison.Ordinal)) {
                return uri.AbsolutePath.StartsWith("blank", StringComparison.Ordinal);
            }

            return string.Equals(uri.Scheme, "data", StringComparison.Ordinal)
                && uri.AbsolutePath.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
