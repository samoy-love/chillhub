// <copyright file="VerifiedGames.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Игры, чей статус уже проверен в этой сессии. Блокируем действия только по играм с неизвестным статусом,
    /// чтобы не держать кнопку в режиме «Проверка…» пока проверяются остальные игры (C4).
    /// <para>
    /// Читается и пополняется и с UI-потока, и из фоновых проверок, поэтому доступ под замком.
    /// </para>
    /// </summary>
    internal sealed class VerifiedGames {
        private readonly object verifiedLock = new();
        private readonly HashSet<string> verifiedGameIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Известен ли статус игры. Для пустого идентификатора — да: нет выбора, нечего блокировать.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>True, если статус проверен либо игра не выбрана.</returns>
        internal bool IsKnown(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return true; // нет выбора — нечего блокировать
            }

            lock (this.verifiedLock) {
                return this.verifiedGameIds.Contains(gameId);
            }
        }

        /// <summary>Помечает статус игры известным.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        internal void MarkKnown(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            lock (this.verifiedLock) {
                this.verifiedGameIds.Add(gameId);
            }
        }

        /// <summary>Забывает все проверки: статусы будут пересчитаны заново.</summary>
        internal void Reset() {
            lock (this.verifiedLock) {
                this.verifiedGameIds.Clear();
            }
        }
    }
}
