// <copyright file="GameDeletion.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;

    /// <summary>
    /// Удаление локальных файлов игры: запреты и сама работа — одним куском на всё
    /// приложение.
    /// <para>
    /// Удалять игру можно из двух мест — из контекстного меню списка и со страницы игры.
    /// Разрушающее действие в двух местах — это две копии его защит, и расходятся они
    /// молча: достаточно поправить одну и забыть про другую, чтобы одна из кнопок начала
    /// сносить файлы из-под работающей закачки. Поэтому и запреты, и сама работа живут
    /// здесь, а страницы отвечают только за своё оформление.
    /// </para>
    /// </summary>
    internal static class GameDeletion {
        /// <summary>
        /// Почему удалять нельзя прямо сейчас; пусто — можно.
        /// <para>
        /// Две причины, и обе про то, что файлы прямо сейчас кто-то держит: их пишет
        /// закачка либо читает запущенная игра. Directory.Delete в обоих случаях снёс бы
        /// половину и оставил игру, которая числится установленной и не запускается.
        /// </para>
        /// </summary>
        /// <param name="queued">Игра стоит в очереди загрузок или качается прямо сейчас.</param>
        /// <param name="exeRelativePath">Путь к exe игры относительно её папки.</param>
        /// <param name="processesByName">Опрос процессов; по умолчанию — настоящий.</param>
        /// <returns>Текст запрета или пустая строка.</returns>
        internal static string Blocker(
            bool queued, string? exeRelativePath, Func<string, int>? processesByName = null) {
            if (queued) {
                return "Идёт установка или обновление этой игры. Дождитесь завершения или снимите её с очереди.";
            }

            var exeName = string.Empty;
            try {
                exeName = Path.GetFileNameWithoutExtension(exeRelativePath ?? string.Empty);
            }
            catch (Exception ex) {
                // Кривой путь — не повод запрещать удаление: файлы всё равно защищены ОС.
                Logging.Logger.Warn($"GameDeletion.Blocker: путь '{exeRelativePath}' не разобран: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(exeName)) {
                return string.Empty;
            }

            try {
                var count = processesByName != null
                    ? processesByName(exeName)
                    : Process.GetProcessesByName(exeName).Length;
                return count > 0
                    ? $"Игра запущена ({exeName}). Закройте игру перед удалением."
                    : string.Empty;
            }
            catch (Exception ex) {
                // Не удалось опросить процессы — не блокируем: ОС не даст удалить занятое.
                Logging.Logger.Warn($"GameDeletion.Blocker: процессы не опрошены: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Сносит файлы игры и всё, что за ними тянется: ярлыки, кеш хешей, запись о
        /// размере в «Установке и удалении программ».
        /// </summary>
        /// <param name="gameId">Игра.</param>
        /// <param name="localRoot">Папка игры на диске.</param>
        /// <returns>Файлы, которые снести не вышло: заняты чужим процессом.</returns>
        internal static async Task<IReadOnlyList<string>> RunAsync(string gameId, string localRoot) {
            // Directory.Delete(recursive) обрывается на ПЕРВОМ занятом файле, когда
            // остальное уже снесено: игра продолжала числиться установленной, а на диске
            // лежали её остатки, неспособные запуститься. Поэтому по файлу и до конца.
            var blocked = await Task.Run(() => GameFiles.DeleteGameFiles(localRoot)).ConfigureAwait(true);

            // Ярлык уносим вместе с файлами: иначе на рабочем столе остаётся иконка,
            // которая по клику ругается «не найден элемент».
            await Task.Run(() => GameLocalState.TryRemoveDesktopShortcuts(localRoot)).ConfigureAwait(true);

            Sync.FileHashCache.Remove(gameId);

            // Освободившиеся гигабайты обязаны отразиться и в «Установке и удалении
            // программ»: размер там считается вместе с папкой игр. Обход уходит в фон.
            Shell.InstalledAppsEntry.RefreshInBackground();
            return blocked;
        }
    }
}
