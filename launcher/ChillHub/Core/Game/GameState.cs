// <copyright file="GameState.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;

    /// <summary>Состояние игры на диске, определяющее подписи и доступность действий.</summary>
    internal enum GameState {
        /// <summary>Локальных файлов нет.</summary>
        NotInstalled,

        /// <summary>Установлена и совпадает с последней версией.</summary>
        Installed,

        /// <summary>Установлена, но доступна более новая сборка.</summary>
        UpdateAvailable,

        /// <summary>Найден маркер `.updating`: обновление прервали посередине.</summary>
        Unfinished,
    }

    /// <summary>
    /// Что очередь должна сделать с игрой в этом состоянии.
    /// <para>
    /// Установленной и свежей качать нечего — ей место на проверке файлов; остальным
    /// наоборот. Правило живёт рядом с самим состоянием, а не в обработчике кнопки:
    /// нажимают её из двух мест, и разойтись им негде.
    /// </para>
    /// </summary>
    internal static class GameStateWork {
        /// <summary>Какую работу ставить в очередь.</summary>
        /// <param name="state">Состояние игры на диске.</param>
        /// <returns>Проверка для установленной, закачка для остальных.</returns>
        internal static QueueTaskKind QueueKindFor(GameState state) =>
            state == GameState.Installed ? QueueTaskKind.Verify : QueueTaskKind.Download;
    }

    /// <summary>Подписи страницы для одного состояния игры.</summary>
    /// <param name="StateText">Текст в строке состояния.</param>
    /// <param name="ActionText">Надпись на кнопке действия.</param>
    internal readonly record struct GameStateLabels(string StateText, string ActionText);

    /// <summary>
    /// Определение состояния игры по тому, что лежит на диске, и подписи для него.
    /// Ничего не знает про контролы — только про факты «есть файлы», «есть маркер»
    /// и «версии разошлись».
    /// </summary>
    internal static class GameStateResolver {
        /// <summary>
        /// Считает состояние игры.
        /// </summary>
        /// <param name="unfinished">Найден маркер незавершённого обновления.</param>
        /// <param name="hasFiles">В папке игры есть хотя бы один полезный файл.</param>
        /// <param name="localVersion">Версия из маркера (уже обрезанная по краям).</param>
        /// <param name="latestVersion">Последняя версия из списка игр.</param>
        /// <param name="needsUpdate">Признак «нужно обновление», посчитанный главной страницей.</param>
        /// <returns>Состояние, от которого зависят подписи и доступность действий.</returns>
        internal static GameState Compute(bool unfinished, bool hasFiles, string localVersion, string? latestVersion, bool needsUpdate) {
            if (unfinished) {
                return GameState.Unfinished;
            }

            var installed = hasFiles || !string.IsNullOrWhiteSpace(localVersion);
            if (!installed) {
                return GameState.NotInstalled;
            }

            var latest = (latestVersion ?? string.Empty).Trim();

            // NeedsUpdate главная страница считает по полному сравнению с манифестом — доверяем ему,
            // а сравнение маркеров версий добавляем как второй, более дешёвый признак.
            var versionMismatch = !string.IsNullOrWhiteSpace(latest)
                && !string.Equals(localVersion, latest, StringComparison.OrdinalIgnoreCase);
            return (needsUpdate || versionMismatch) ? GameState.UpdateAvailable : GameState.Installed;
        }

        /// <summary>Подписи строки состояния и кнопки действия для состояния игры.</summary>
        /// <param name="state">Состояние игры.</param>
        /// <returns>Тексты, которые страница выводит без изменений.</returns>
        internal static GameStateLabels Labels(GameState state) {
            switch (state) {
                case GameState.NotInstalled:
                    return new GameStateLabels("Не установлена", "Установить");
                case GameState.UpdateAvailable:
                    return new GameStateLabels("Доступно обновление", "Обновить");
                case GameState.Unfinished:
                    return new GameStateLabels("Обновление не завершено", "Завершить обновление");
                case GameState.Installed:
                default:
                    return new GameStateLabels("Установлена", "Проверить файлы");
            }
        }
    }
}
