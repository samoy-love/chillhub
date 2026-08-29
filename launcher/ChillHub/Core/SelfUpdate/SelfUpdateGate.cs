// <copyright file="SelfUpdateGate.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    /// <summary>
    /// Помнит найденное обновление, пока его некому показать.
    /// <para>
    /// РЕЗУЛЬТАТ ПРОВЕРКИ НЕ ДОЛЖЕН ПРОПАДАТЬ. Фоновая проверка ходит за версией по
    /// таймеру независимо от того, видно ли окно; найдя обновление у свёрнутого окна,
    /// показать его она не могла — и просто выбрасывала. Человек возвращался к
    /// лаунчеру, и тот молчал.
    /// </para>
    /// <para>
    /// Отдельным объектом, а не тремя присваиваниями поля внутри окна: состояние
    /// меняется в трёх местах — нашли, показали, обновления больше нет, — и ровно на
    /// таком, размазанном по обработчикам, автомате всё и сломалось. Здесь он один и
    /// проверяется тестами.
    /// </para>
    /// </summary>
    internal sealed class SelfUpdateGate {
        private SelfUpdatePrecheck? pending;

        /// <summary>Обновление найдено и ждёт подходящего момента.</summary>
        internal bool Waiting => this.pending != null;

        /// <summary>
        /// Обновления больше нет: уже поставили, откатили на сервере, проверка не
        /// нашла ничего. Отложенному показывать нечего.
        /// </summary>
        internal void Forget() => this.pending = null;

        /// <summary>
        /// Предлагает показать найденное обновление.
        /// </summary>
        /// <param name="precheck">Что нашла проверка.</param>
        /// <param name="windowVisible">Окно на экране (не в трее).</param>
        /// <param name="minimized">Окно свёрнуто.</param>
        /// <param name="busy">В очереди есть работа: закачка или проверка файлов.</param>
        /// <returns>
        /// Обновление, если показывать можно прямо сейчас; иначе null — оно запомнено
        /// и дождётся следующего подходящего момента.
        /// </returns>
        internal SelfUpdatePrecheck? Offer(
            SelfUpdatePrecheck precheck, bool windowVisible, bool minimized, bool busy) {
            if (!SelfUpdatePrompt.CanShowNow(windowVisible, minimized, busy)) {
                this.pending = precheck;
                return null;
            }

            // Показанное больше не отложено: иначе следующий возврат фокуса открыл бы
            // диалог второй раз поверх только что закрытого.
            this.pending = null;
            return precheck;
        }

        /// <summary>
        /// Идти ли за версией по возврату фокуса на окно: обычно — как разрешит
        /// ограничитель частоты, но с найденным обновлением он молчит.
        /// </summary>
        /// <param name="throttleAllows">Ограничитель частоты разрешает запрос.</param>
        /// <returns>true, если проверку нужно запустить.</returns>
        internal bool ShouldCheckOnActivate(bool throttleAllows)
            => SelfUpdatePrompt.ShouldCheckOnActivate(this.Waiting, throttleAllows);
    }
}
