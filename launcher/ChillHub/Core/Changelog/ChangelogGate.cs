// <copyright file="ChangelogGate.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Changelog {
    using System.Collections.Generic;

    /// <summary>
    /// Решает, показывать ли окно «Что нового» на этом запуске.
    /// <para>
    /// Отдельно от страницы, потому что решение здесь не одно: показать один раз
    /// после обновления, не показывать на каждом запуске, не показывать второй раз
    /// после отката на старую сборку и не открывать пустое окно после выпуска,
    /// в котором для игрока ничего не менялось. Через живое окно ни один из этих
    /// случаев не проверить.
    /// </para>
    /// </summary>
    internal static class ChangelogGate {
        /// <summary>
        /// Показывать ли список обновлений.
        /// <para>
        /// Пустая отметка — это либо свежая установка, либо обновление с версии,
        /// которая отметок ещё не вела. Отличить одно от другого нечем, и выбран
        /// показ: тот, кто обновился ради починок, узнает о них, а новичок один
        /// раз пролистает историю. Промолчать значило бы, что при появлении
        /// самого окна его не увидит ровно никто.
        /// </para>
        /// </summary>
        /// <param name="lastSeenVersion">Версия, для которой список уже показывали.</param>
        /// <param name="currentVersion">Версия, которая запущена сейчас.</param>
        /// <param name="releases">Выпуски, которые вообще показываются игроку (без технических).</param>
        /// <returns>true, если окно надо открыть.</returns>
        internal static bool ShouldShow(string? lastSeenVersion, string? currentVersion, IReadOnlyList<ChangelogRelease>? releases) {
            if (releases == null || releases.Count == 0) {
                return false;
            }

            var current = (currentVersion ?? string.Empty).Trim();
            if (current.Length == 0) {
                // Версию не узнали — показ отложим: иначе окно всплывало бы на каждом
                // запуске, потому что запомнить его было бы нечем.
                return false;
            }

            var lastSeen = (lastSeenVersion ?? string.Empty).Trim();
            if (lastSeen.Length == 0) {
                return true;
            }

            // Откат на старую сборку — не новость: список за неё уже видели.
            if (VersionOrder.Compare(current, lastSeen) <= 0) {
                return false;
            }

            // Версия выросла, а рассказать нечего: обновление было техническим.
            // Отметку в этом случае не двигаем — накопленное покажем на следующем
            // выпуске, в котором для игрока что-то изменилось.
            return HasNewsSince(lastSeen, releases);
        }

        /// <summary>Есть ли среди показываемых выпусков хоть один новее уже виденного.</summary>
        private static bool HasNewsSince(string lastSeen, IReadOnlyList<ChangelogRelease> releases) {
            foreach (var release in releases) {
                if (VersionOrder.Compare(release.Version, lastSeen) > 0) {
                    return true;
                }
            }

            return false;
        }
    }
}
