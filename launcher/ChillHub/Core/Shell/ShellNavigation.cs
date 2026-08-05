// <copyright file="ShellNavigation.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;

    /// <summary>
    /// Решения оболочки о переходах между страницами.
    /// <para>
    /// Главное здесь — не переходить на страницу, которая уже открыта. Каждый лишний
    /// переход создаёт ВТОРОЙ экземпляр страницы: у главной это второй
    /// <c>FeedbackService</c> со своей копией очереди и своим таймером, который
    /// перезаписывал бы feedback_queue.json без нового сообщения — и оно терялось бы
    /// навсегда.
    /// </para>
    /// </summary>
    internal static class ShellNavigation {
        /// <summary>
        /// Нужен ли переход: страница нужного типа уже показана — не трогаем.
        /// </summary>
        /// <param name="currentContent">Что сейчас в области содержимого окна.</param>
        /// <param name="pageType">Тип страницы, на которую собрались.</param>
        /// <returns>true, если переходить надо.</returns>
        internal static bool NeedsNavigation(object? currentContent, Type pageType)
            => !pageType.IsInstanceOfType(currentContent);

        /// <summary>
        /// Куда ведёт кнопка «Назад» со страницы настроек: в историю навигации, если она есть,
        /// иначе — на главную. На главную именно переходом окна, а не созданием страницы:
        /// вторая копия каталога тянет за собой вторую очередь обратной связи.
        /// </summary>
        /// <param name="hasNavigationService">У страницы есть служба навигации.</param>
        /// <param name="canGoBack">В истории есть куда вернуться.</param>
        /// <returns>true — шаг назад по истории; false — переход на главную.</returns>
        internal static bool ShouldGoBack(bool hasNavigationService, bool canGoBack)
            => hasNavigationService && canGoBack;
    }
}
