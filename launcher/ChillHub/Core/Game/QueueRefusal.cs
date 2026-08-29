// <copyright file="QueueRefusal.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    /// <summary>
    /// Почему очередь не приняла игру — словами для игрока.
    /// <para>
    /// Отказ очереди беззвучен: <c>Enqueue</c> возвращает false и всё. Причин у него
    /// две на каждую работу, и обе стоит назвать: молчащая кнопка читается как
    /// сломанная, а «не удалось» не подсказывает, что делать дальше.
    /// </para>
    /// </summary>
    internal static class QueueRefusal {
        /// <summary>Текст отказа для строки состояния.</summary>
        /// <param name="kind">Что пытались поставить в очередь.</param>
        /// <param name="title">Название игры.</param>
        /// <returns>Готовая строка.</returns>
        internal static string For(QueueTaskKind kind, string? title) {
            var name = string.IsNullOrWhiteSpace(title) ? "Игра" : "«" + title + "»";
            return kind == QueueTaskKind.Verify
                ? name + " уже проверяется или ещё не установлена."
                : name + " уже установлена или уже в очереди.";
        }
    }
}
