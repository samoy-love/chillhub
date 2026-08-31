// <copyright file="UpdateErrorScope.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;

    /// <summary>
    /// К какой игре относится сорвавшаяся закачка.
    /// <para>
    /// «ПОВТОРИТЬ» — СВОЙСТВО ИГРЫ, А НЕ ЭКРАНА. Пока сбой хранился отдельным флагом
    /// страницы, неудача на одной игре ставила «Повторить» и соседней — установленной и
    /// свежей: клик по ней отвечал «уже установлена или уже в очереди», а вернуть
    /// «Играть» можно было только уходом на другую игру и обратно. Флаг сбрасывался
    /// лишь при смене выбора, а кнопка пересчитывается и без неё — после запуска игры,
    /// после проверки статусов.
    /// </para>
    /// </summary>
    internal static class UpdateErrorScope {
        /// <summary>Относится ли последняя сорвавшаяся закачка к этой игре.</summary>
        /// <param name="errorGameId">Игра, на которой сорвалась закачка; пусто — сбоя не было.</param>
        /// <param name="gameId">Игра, для которой считается кнопка действия.</param>
        /// <returns>true, если это одна и та же игра.</returns>
        internal static bool AppliesTo(string? errorGameId, string? gameId)
            => !string.IsNullOrWhiteSpace(errorGameId)
               && !string.IsNullOrWhiteSpace(gameId)
               && string.Equals(errorGameId, gameId, StringComparison.OrdinalIgnoreCase);
    }
}
