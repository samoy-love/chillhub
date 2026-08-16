// <copyright file="DownloadsChip.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using ChillHub.Core.Game;

    /// <summary>
    /// Подпись чипа загрузок в шапке окна: «38%», «38% · ещё 2», «в очереди 3».
    /// Пустая строка — очередь пуста, чип прячется.
    /// <para>
    /// Шапка одна на все экраны, а очередь живёт только на главной: со страницы игры или из
    /// настроек ход закачки иначе не виден. Подпись нарочно короткая — это индикатор, а не
    /// вторая очередь; подробности по клику, на главной.
    /// </para>
    /// </summary>
    internal static class DownloadsChip {
        /// <summary>Текст чипа для текущего снимка очереди.</summary>
        /// <param name="items">Снимок очереди (см. <see cref="IDownloadQueue.Snapshot"/>).</param>
        /// <returns>Подпись или пустая строка, если показывать нечего.</returns>
        internal static string Text(IReadOnlyList<QueueItem> items) {
            if (items == null || items.Count == 0) {
                return string.Empty;
            }

            var running = items.FirstOrDefault(i => i.State == QueueItemState.Running);
            var waiting = items.Count(i => i.State == QueueItemState.Waiting);

            if (running == null) {
                return waiting > 0 ? $"в очереди {waiting}" : string.Empty;
            }

            var head = running.TotalBytes > 0
                ? $"{Math.Clamp(running.BytesDownloaded * 100.0 / running.TotalBytes, 0, 100):0}%"
                : "загрузка";
            return waiting > 0 ? $"{head} · ещё {waiting}" : head;
        }
    }
}
