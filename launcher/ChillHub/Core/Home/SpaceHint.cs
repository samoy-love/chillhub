// <copyright file="SpaceHint.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Подсказка «сколько нужно скачать и сколько свободно на диске».
    /// Держит потокобезопасный кеш оценок по играм (заполняется при проверке статуса и после установки)
    /// и собирает готовую строку для UI. Сам ничего не рисует.
    /// </summary>
    internal sealed class SpaceHint {
        private readonly object cacheLock = new();
        private readonly Dictionary<string, long> neededBytes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Запоминает, сколько байт нужно докачать для игры.</summary>
        internal void Remember(string? gameId, long need) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            lock (this.cacheLock) {
                this.neededBytes[gameId] = need;
            }
        }

        /// <summary>
        /// Забывает все оценки. Нужен, когда сменилась папка для игр: прежние цифры
        /// относятся к другому диску и другому содержимому.
        /// </summary>
        internal void Clear() {
            lock (this.cacheLock) {
                this.neededBytes.Clear();
            }
        }

        /// <summary>Достаёт закешированную оценку. false — оценки ещё нет.</summary>
        internal bool TryGet(string? gameId, out long need) {
            need = 0;
            if (string.IsNullOrWhiteSpace(gameId)) {
                return false;
            }

            lock (this.cacheLock) {
                return this.neededBytes.TryGetValue(gameId, out need);
            }
        }

        /// <summary>
        /// Строка для UI по закешированной оценке. Пустая строка = показывать нечего
        /// (оценки нет либо качать ничего не надо).
        /// </summary>
        internal string BuildTextFromCache(string? gameId) {
            if (!this.TryGet(gameId, out var need)) {
                return string.Empty;
            }

            return BuildText(need, GameLocalState.GetAvailableFreeSpaceFor(gameId));
        }

        /// <summary>Собирает строку вида «Нужно: 1,2 ГБ (40,0 ГБ доступно)».</summary>
        internal static string BuildText(long need, long have)
            => need > 0 ? $"Нужно: {HomeFormat.FormatSize(need)} ({HomeFormat.FormatSize(have)} доступно)" : string.Empty;
    }
}
