// <copyright file="VersionSwitchView.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;

    /// <summary>
    /// Состояние блока «переключение версии»: доступна ли кнопка и что написано подсказкой.
    /// </summary>
    /// <param name="CanSwitch">Доступна ли кнопка переключения.</param>
    /// <param name="Hint">Текст подсказки; null — подсказку не трогаем.</param>
    internal readonly record struct VersionSwitchView(bool CanSwitch, string? Hint);

    /// <summary>Текст модального вопроса о переключении версии.</summary>
    /// <param name="Title">Заголовок окна.</param>
    /// <param name="Text">Сам вопрос.</param>
    /// <param name="IsRollback">Выбрана не последняя версия — это откат.</param>
    internal readonly record struct VersionSwitchPrompt(string Title, string Text, bool IsRollback);

    /// <summary>
    /// Правила блока «переключение версии»: когда кнопка доступна, что говорит подсказка
    /// и о чём предупреждает вопрос перед откатом. Чистая логика, без контролов.
    /// </summary>
    internal static class VersionSwitch {
        /// <summary>
        /// Считает доступность кнопки и текст подсказки.
        /// </summary>
        /// <param name="selected">Версия, выбранная в выпадающем списке.</param>
        /// <param name="localVersion">Версия, установленная на диске.</param>
        /// <param name="latestVersion">Последняя версия из списка игр.</param>
        /// <param name="unfinished">Есть маркер незавершённого обновления.</param>
        /// <param name="isBusy">Идёт установка или обновление.</param>
        /// <param name="maintenanceBlocked">Сервер объявил технические работы.</param>
        /// <returns>Что показать в блоке переключения версии.</returns>
        internal static VersionSwitchView Compute(
            string? selected,
            string? localVersion,
            string? latestVersion,
            bool unfinished,
            bool isBusy,
            bool maintenanceBlocked) {
            var chosen = (selected ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(chosen)) {
                return new VersionSwitchView(false, null);
            }

            var sameAsInstalled = !string.IsNullOrWhiteSpace(localVersion)
                && string.Equals(chosen, localVersion, StringComparison.OrdinalIgnoreCase);

            // Маркер незавершённого обновления означает, что версия на диске «смешанная»:
            // повторная установка той же версии в этом случае осмысленна.
            var canSwitch = !isBusy && !maintenanceBlocked && (!sameAsInstalled || unfinished);

            var latest = (latestVersion ?? string.Empty).Trim();
            string hint;
            if (maintenanceBlocked) {
                hint = "Переключение версии недоступно: на сервере идут технические работы.";
            }
            else if (sameAsInstalled && !unfinished) {
                hint = "Эта версия уже установлена.";
            }
            else if (!string.IsNullOrWhiteSpace(latest) && !string.Equals(chosen, latest, StringComparison.OrdinalIgnoreCase)) {
                hint = $"Внимание: {chosen} — не последняя версия. Установка будет откатом с {latest}.";
            }
            else {
                hint = "Выбрана последняя версия.";
            }

            return new VersionSwitchView(canSwitch, hint);
        }

        /// <summary>
        /// Собирает вопрос перед переключением версии. Откат называется откатом прямым текстом:
        /// пользователь должен понимать, что новый контент и исправления пропадут.
        /// </summary>
        /// <param name="selected">Версия, на которую переключаются.</param>
        /// <param name="latestVersion">Последняя версия из списка игр.</param>
        /// <returns>Заголовок и текст вопроса.</returns>
        internal static VersionSwitchPrompt BuildPrompt(string selected, string? latestVersion) {
            var latest = (latestVersion ?? string.Empty).Trim();
            var isRollback = !string.IsNullOrWhiteSpace(latest)
                && !string.Equals(selected, latest, StringComparison.OrdinalIgnoreCase);

            var title = isRollback ? "Переключение версии (откат)" : "Переключение версии";
            var text = isRollback
                ? $"Сейчас будет установлена версия {selected}, а не последняя ({latest}).\n\n"
                  + "Это откат, а не обновление: файлы игры будут приведены к состоянию выбранной сборки, "
                  + "новый контент и исправления из более свежих версий пропадут. "
                  + "Сетевая игра с теми, у кого другая версия, работать не будет.\n\nПродолжить?"
                : $"Файлы игры будут приведены к состоянию версии {selected}.\n\nПродолжить?";

            return new VersionSwitchPrompt(title, text, isRollback);
        }
    }
}
