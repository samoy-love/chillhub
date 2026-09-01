// <copyright file="TrayPlayDecision.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    /// <summary>Что делает пункт «Играть: имя игры» в меню значка в трее.</summary>
    internal enum TrayPlay {
        /// <summary>Запустить игру, не поднимая окно, — ровно за этим пункт и нужен.</summary>
        Launch,

        /// <summary>Только показать окно: решение за пользователем, трогать работу нельзя.</summary>
        ShowWindow,

        /// <summary>Показать окно и нажать кнопку действия за пользователя.</summary>
        ShowWindowAndAct,
    }

    /// <summary>
    /// Пункт «Играть» в меню значка обещает запуск игры и ничего больше.
    /// <para>
    /// ПУНКТ, КОТОРЫЙ ДЕЛАЕТ ОБРАТНОЕ НАПИСАННОМУ, ХУЖЕ ОТСУТСТВУЮЩЕГО. Пока меню
    /// безусловно нажимало кнопку действия витрины, «Играть» у качающейся игры уходило
    /// в её же отмену: игрок сворачивал лаунчер, жал «Играть» и без единого сообщения
    /// терял идущую установку. Начать установку или обновление из трея — не потеря, и
    /// окно при этом показывается; снять работу — потеря, и её делают только руками.
    /// </para>
    /// </summary>
    internal static class TrayPlayDecision {
        /// <summary>Что сделать по нажатию пункта «Играть» в меню значка.</summary>
        /// <param name="canPlay">Выбранная игра установлена, свежая и не запущена.</param>
        /// <param name="actionCancels">Кнопка действия витрины сейчас снимает работу, а не начинает её.</param>
        /// <returns>Что делать окну.</returns>
        internal static TrayPlay For(bool canPlay, bool actionCancels) {
            if (canPlay) {
                return TrayPlay.Launch;
            }

            return actionCancels ? TrayPlay.ShowWindow : TrayPlay.ShowWindowAndAct;
        }
    }
}
