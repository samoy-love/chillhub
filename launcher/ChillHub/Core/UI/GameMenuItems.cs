// <copyright file="GameMenuItems.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core.Maintenance;

    /// <summary>Каким показать один пункт контекстного меню строки списка игр.</summary>
    /// <param name="Visible">Показывать ли пункт.</param>
    /// <param name="Enabled">Можно ли его нажать.</param>
    internal readonly record struct GameMenuItemLook(bool Visible, bool Enabled);

    /// <summary>
    /// Что показать в контекстном меню строки списка игр.
    /// <para>
    /// ПУНКТ, КОТОРЫЙ НИЧЕГО НЕ СДЕЛАЕТ, ХУЖЕ ОТСУТСТВУЮЩЕГО. У установленной и свежей
    /// игры качать нечего, и «Добавить в очередь загрузок» молча отвечал отказом —
    /// вместо него ей место проверке файлов. У остальных наоборот: проверять ещё нечего.
    /// </para>
    /// <para>
    /// Правило живёт здесь, а не в обработчике страницы: внутри WPF-меню его никто не
    /// проверит, а ошибка в нём выглядит как пункт, который не работает.
    /// </para>
    /// </summary>
    internal static class GameMenuItems {
        /// <summary>Имя пункта «Добавить в очередь загрузок» в разметке.</summary>
        internal const string Enqueue = "EnqueueMenuItem";

        /// <summary>Имя пункта «Проверить файлы игры» в разметке.</summary>
        internal const string Verify = "VerifyMenuItem";

        /// <summary>
        /// Показывать ли игре проверку файлов вместо постановки в очередь загрузок.
        /// </summary>
        /// <param name="game">Игра из списка; null — сказать о ней нечего.</param>
        /// <returns>true, если игра установлена и обновлять её не нужно.</returns>
        internal static bool ShowsVerify(GameInfo? game) => game is { IsInstalled: true, NeedsUpdate: false };

        /// <summary>
        /// Вид одного пункта меню.
        /// </summary>
        /// <param name="name">Имя пункта в разметке; у безымянных — null.</param>
        /// <param name="isFirst">
        /// Это первый пункт («Подробнее об игре»). Он остаётся живым всегда: страница
        /// игры полезна и до установки.
        /// </param>
        /// <param name="game">Игра из строки списка.</param>
        /// <param name="hasFiles">На диске есть файлы этой игры.</param>
        /// <param name="maintenance">Режим технических работ; null — ограничений нет.</param>
        /// <returns>Показывать ли пункт и можно ли его нажать.</returns>
        internal static GameMenuItemLook For(
            string? name, bool isFirst, GameInfo? game, bool hasFiles, MaintenanceState? maintenance = null) {
            var verify = ShowsVerify(game);
            var visible = name switch {
                Enqueue => !verify,
                Verify => verify,
                _ => true,
            };

            // ФАЙЛЫ НУЖНЫ ТОМУ, КТО С НИМИ РАБОТАЕТ. Правило «нужны файлы» стояло на всём
            // меню сразу, и «Добавить в очередь загрузок» оказывался серым ровно у той игры,
            // которую ещё ни разу не ставили, — единственный путь установки из списка игр
            // не работал. Постановка в очередь файлы создаёт, а не читает.
            //
            // Но и «нажимается всегда» неверно: пункт обязан повторять предусловия самой
            // очереди и запрет техработ. Иначе у игры, которая живёт только копией из Steam,
            // он отвечает молчаливым отказом, а во время техработ через меню начинается
            // установка, которую кнопка на странице начать не даёт.
            var enabled = name switch {
                Enqueue => CanEnqueue(game, maintenance),
                _ => isFirst || hasFiles,
            };

            return new GameMenuItemLook(visible, enabled);
        }

        /// <summary>
        /// Есть ли смысл ставить эту игру в очередь прямо сейчас.
        /// <para>
        /// Оба условия взяты не отсюда, а у тех, кто их и без того применяет: сборка на
        /// сервере — предусловие <c>DownloadQueue.Enqueue</c>, без неё позиция появлялась
        /// в панели загрузок и через секунду падала с отказом; запрет техработ — то же
        /// правило, по которому гаснет кнопка действия (<c>ActionButtonState</c>).
        /// </para>
        /// </summary>
        /// <param name="game">Игра из строки списка.</param>
        /// <param name="maintenance">Режим технических работ; null — ограничений нет.</param>
        /// <returns>true, если пункт имеет смысл нажимать.</returns>
        internal static bool CanEnqueue(GameInfo? game, MaintenanceState? maintenance) {
            if (game == null || string.IsNullOrWhiteSpace(game.LatestVersion)) {
                return false;
            }

            if (maintenance == null) {
                return true;
            }

            // Установка это или обновление, решает наличие игры на диске — ровно так же,
            // как это решает страница игры.
            return !(game.IsInstalled ? maintenance.BlocksUpdate : maintenance.BlocksInstall);
        }

        /// <summary>
        /// Игра, к строке которой относится нажатый пункт меню.
        /// <para>
        /// Сначала CommandParameter, потом DataContext — тот же порядок, что у
        /// остальных пунктов этого меню: параметр задан в разметке явно, а контекст
        /// достаётся строке сам и может оказаться чужим, если пункт лежит вложенно.
        /// </para>
        /// </summary>
        /// <param name="sender">Нажатый пункт меню.</param>
        /// <returns>Игра или null.</returns>
        internal static GameInfo? GameOf(object? sender) {
            var fe = sender as FrameworkElement;
            return fe?.GetValue(MenuItem.CommandParameterProperty) as GameInfo
                   ?? fe?.DataContext as GameInfo;
        }

        /// <summary>
        /// Одевает всё меню сразу.
        /// <para>
        /// Проход здесь, а не в обработчике страницы, по той же причине, что и само
        /// правило: внутри страницы его никто не проверит, а «первый пункт» и «пункт по
        /// имени» — ровно те места, где легко ошибиться молча.
        /// </para>
        /// </summary>
        /// <param name="items">Пункты меню; null — одевать нечего.</param>
        /// <param name="game">Игра из строки списка.</param>
        /// <param name="hasFiles">На диске есть файлы этой игры.</param>
        /// <param name="maintenance">Режим технических работ; null — ограничений нет.</param>
        internal static void Apply(
            ItemCollection? items, GameInfo? game, bool hasFiles, MaintenanceState? maintenance = null) {
            if (items == null) {
                return;
            }

            for (var i = 0; i < items.Count; i++) {
                if (items[i] is not MenuItem item) {
                    continue;
                }

                var look = For(item.Name, i == 0, game, hasFiles, maintenance);
                item.Visibility = look.Visible ? Visibility.Visible : Visibility.Collapsed;
                item.IsEnabled = look.Enabled;
            }
        }
    }
}
