// <copyright file="GameStatus.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;

    using static ChillHub.Core.Home.GameLocalState;

    /// <summary>
    /// Состояние игры в списке: установлена ли, нужна ли докачка, какая версия лежит на диске.
    /// <para>
    /// От ответа на эти вопросы зависит надпись на кнопке действия. Ошибка стоит пользователю
    /// повторной закачки сборки на десятки гигабайт, поэтому решения приняты в одном месте
    /// и без участия UI.
    /// </para>
    /// </summary>
    internal static class GameStatus {
        /// <summary>
        /// Приводит к нормальному виду адрес иконки и версию, пришедшие от API, и определяет
        /// локальное состояние каждой игры по маркерам на диске.
        /// </summary>
        /// <param name="games">Список игр (может быть null — тогда делать нечего).</param>
        /// <param name="baseApi">База адреса сервера для корнеотносительных ссылок.</param>
        internal static void NormalizeIconsAndLocalState(IEnumerable<GameInfo> games, string baseApi) {
            if (games == null) {
                return;
            }

            foreach (var g in games) {
                try {
                    // Normalize icon URL if server returned a root-relative path
                    if (!string.IsNullOrWhiteSpace(g.IconUrl) && g.IconUrl.StartsWith("/")) {
                        g.IconUrl = baseApi + g.IconUrl;
                    }

                    // Normalize API version string
                    if (!string.IsNullOrWhiteSpace(g.LatestVersion)) {
                        g.LatestVersion = g.LatestVersion.Trim();
                    }

                    // Determine local state from version marker
                    var ver = ReadLocalVersion(g.GameId);
                    var verTrimmed = string.IsNullOrWhiteSpace(ver) ? string.Empty : ver.Trim();
                    g.IsInstalled = !string.IsNullOrWhiteSpace(verTrimmed);
                    g.InstalledVersion = verTrimmed ?? string.Empty;

                    // Compute needs update: installed and latest known but different
                    g.NeedsUpdate = g.IsInstalled && !string.IsNullOrWhiteSpace(g.LatestVersion) &&
                                     !string.Equals(g.InstalledVersion?.Trim(), g.LatestVersion?.Trim(), StringComparison.OrdinalIgnoreCase);

                    // Модпак — вторая версия у той же игры, и обновляется он отдельно
                    if (g.IsInstalled && ModsOutOfDate(g)) {
                        g.NeedsUpdate = true;
                    }

                    // Прерванное обновление: игра гарантированно требует восстановления (C2)
                    if (HasUnfinishedUpdate(g.GameId)) {
                        g.NeedsUpdate = true;
                    }

                    Logging.Logger.Info($"NormalizeState gid={g.GameId} latest='{g.LatestVersion}' local='{g.InstalledVersion}' isInstalled={g.IsInstalled} needsUpdate={g.NeedsUpdate}");
                }
                catch (Exception ex) {
                    // Одна игра с некорректными данными не должна ломать нормализацию всего списка
                    Logging.Logger.Error(ex, $"NormalizeGameIconsAndLocalState(gid={g?.GameId})");
                }
            }
        }

        /// <summary>
        /// Отмечает игру установленной в только что применённой версии.
        /// «Нужно обновление» пересчитывается тут же: latest мог уйти вперёд, пока шла установка.
        /// </summary>
        /// <param name="g">Игра из списка; null — игры уже нет, отмечать нечего.</param>
        /// <param name="version">Установленная версия.</param>
        internal static void MarkInstalled(GameInfo? g, string? version) {
            if (g != null) {
                g.IsInstalled = true;
                g.InstalledVersion = (version ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(g.LatestVersion)) {
                    g.LatestVersion = g.LatestVersion.Trim();
                }

                g.NeedsUpdate = !string.IsNullOrWhiteSpace(g.LatestVersion) &&
                                 !string.Equals(g.InstalledVersion?.Trim(), g.LatestVersion?.Trim(), StringComparison.OrdinalIgnoreCase);
                if (ModsOutOfDate(g)) {
                    g.NeedsUpdate = true;
                }
            }
        }

        /// <summary>
        /// Отличается ли модпак на диске от того, что сервер объявил активным.
        /// <para>
        /// У игры с модами ДВЕ версии: сборка игры и модпак. Раньше «нужно ли
        /// обновление» считалось только по первой, и активация модпака в админке
        /// не доходила до игрока вовсе: карточка так и показывала «Играть»,
        /// потому что сама сборка игры не менялась.
        /// </para>
        /// <para>
        /// Пустой маркер при объявленном модпаке — тоже расхождение: это первая
        /// установка модов поверх уже стоящей игры.
        /// </para>
        /// </summary>
        /// <param name="g">Игра из списка.</param>
        /// <returns>True, если модпак нужно доставить или обновить.</returns>
        internal static bool ModsOutOfDate(GameInfo? g) {
            var wanted = g?.Mods is { HasLatest: true } mods ? (mods.Version ?? string.Empty).Trim() : string.Empty;
            if (wanted.Length == 0) {
                return false;
            }

            var installed = GameLocalState.ReadLocalModsVersion(g!.GameId).Trim();
            return !string.Equals(installed, wanted, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Отмечает игру удалённой.</summary>
        /// <param name="g">Игра из списка; null — игры уже нет.</param>
        internal static void MarkUninstalled(GameInfo? g) {
            if (g != null) {
                g.IsInstalled = false;
                g.InstalledVersion = string.Empty;

                // После удаления считаем, что обновление не требуется до повторной проверки
                g.NeedsUpdate = false;
            }
        }

        /// <summary>
        /// Обновляет состояние выбранной игры по маркеру версии на диске.
        /// Читает файл, поэтому вызывающий уводит его с UI-потока.
        /// </summary>
        /// <param name="g">Игра из списка; null — обновлять нечего.</param>
        /// <param name="localVersion">Версия, прочитанная с диска.</param>
        /// <returns>Та же версия без краевых пробелов — её же пишут в лог.</returns>
        internal static string ApplyLocalVersion(GameInfo? g, string? localVersion) {
            string localTrimmed = string.IsNullOrWhiteSpace(localVersion) ? string.Empty : localVersion.Trim();
            if (g != null) {
                g.IsInstalled = !string.IsNullOrWhiteSpace(localTrimmed);
                g.InstalledVersion = localTrimmed ?? string.Empty;
            }

            return localTrimmed ?? string.Empty;
        }
    }
}
