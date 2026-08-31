// <copyright file="ChangelogMarks.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Changelog {
    using System.Collections.Generic;

    /// <summary>
    /// Отмечает в списке выпуски, которых человек ещё не видел.
    /// <para>
    /// После обновления через несколько версий сразу список открывается длинным,
    /// и понять, что из него новое, по одним номерам нельзя: игрок не помнит, на
    /// какой версии сидел. Отметка отвечает на это за него.
    /// </para>
    /// </summary>
    internal static class ChangelogMarks {
        /// <summary>
        /// Проставляет <see cref="ChangelogRelease.IsNew"/> по отметке о прошлом показе.
        /// <para>
        /// Пустая отметка — первый запуск версии, которая вообще умеет вести список.
        /// Сравнивать не с чем, и новыми НЕ помечается ничего: подсветить всю историю
        /// от 1.0 значит не выделить ничего. Отметку в этом случае ставит сам показ,
        /// и со следующего обновления выделение заработает по-настоящему.
        /// </para>
        /// <para>
        /// Отметки снимаются с ВСЕХ выпусков, а не только проставляются: список
        /// в приложении один на весь запуск, и открытый второй раз он не должен
        /// показывать вчерашнюю подсветку.
        /// </para>
        /// </summary>
        /// <param name="releases">Выпуски, которые пойдут в окно.</param>
        /// <param name="lastSeenVersion">Версия, на которой список показывали в прошлый раз.</param>
        /// <returns>Сколько выпусков отмечено новыми.</returns>
        internal static int MarkUnseen(IReadOnlyList<ChangelogRelease>? releases, string? lastSeenVersion) {
            if (releases == null) {
                return 0;
            }

            var lastSeen = (lastSeenVersion ?? string.Empty).Trim();
            var marked = 0;
            foreach (var release in releases) {
                var isNew = lastSeen.Length > 0 && VersionOrder.Compare(release.Version, lastSeen) > 0;
                release.IsNew = isNew;
                if (isNew) {
                    marked++;
                }
            }

            return marked;
        }
    }
}
