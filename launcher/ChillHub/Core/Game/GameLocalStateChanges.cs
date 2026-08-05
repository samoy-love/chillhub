// <copyright file="GameLocalStateChanges.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    /// <summary>
    /// Признак того, что со страницы игры менялось локальное состояние (установка/обновление/откат).
    /// Главная страница читает и сбрасывает флаг при возврате, чтобы освежить список без полной перезагрузки.
    /// </summary>
    internal static class GameLocalStateChanges {
        private static bool localStateChanged;

        /// <summary>Отмечает, что файлы игры на диске изменились.</summary>
        internal static void MarkChanged() => localStateChanged = true;

        /// <summary>
        /// Забирает и сбрасывает признак изменения локального состояния.
        /// Возвращает true, если после последнего вызова игра ставилась, обновлялась или откатывалась.
        /// </summary>
        /// <returns>True, если главной странице нужно перечитать состояние игр.</returns>
        internal static bool Consume() {
            var value = localStateChanged;
            localStateChanged = false;
            return value;
        }
    }
}
