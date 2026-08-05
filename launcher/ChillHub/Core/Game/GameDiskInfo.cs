// <copyright file="GameDiskInfo.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Ответы на два вопроса страницы игры про машину пользователя: сколько занимает
    /// папка игры и не запущена ли сама игра. Никакого UI.
    /// </summary>
    internal static class GameDiskInfo {
        /// <summary>
        /// Считает процессы с таким именем. Отдельным швом — иначе проверку «игра запущена»
        /// нечем проверить: настоящий опрос процессов зависит от того, что открыто на машине.
        /// </summary>
        internal static Func<string, int> ProcessCountByName { get; set; } = DefaultProcessCount;

        /// <summary>Возвращает опрос процессов к настоящему.</summary>
        internal static void ResetProcessProbeForTests() => ProcessCountByName = DefaultProcessCount;

        /// <summary>Суммарный размер файлов в папке игры. 0, если папки нет или её не удалось обойти.</summary>
        /// <param name="root">Папка игры.</param>
        /// <returns>Размер в байтах.</returns>
        internal static long GetDirectorySize(string root) {
            try {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                    return 0;
                }

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                    try {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) {
                        // Файл могли удалить во время обхода — пропускаем и считаем дальше
                        Logging.Logger.Warn($"GamePage.GetDirectorySize: '{file}': {ex.Message}");
                    }
                }

                return total;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"GamePage.GetDirectorySize('{root}'): {ex.Message}");
                return 0;
            }
        }

        /// <summary>Запущена ли игра прямо сейчас: пока её файлы открыты, менять их нельзя.</summary>
        /// <param name="exeRelativePath">Путь к exe игры относительно её папки.</param>
        /// <param name="exeName">Имя процесса, по которому шла проверка.</param>
        /// <returns>True, если процесс игры найден.</returns>
        internal static bool IsGameRunning(string? exeRelativePath, out string exeName) {
            exeName = string.Empty;
            try {
                if (string.IsNullOrWhiteSpace(exeRelativePath)) {
                    return false;
                }

                exeName = Path.GetFileNameWithoutExtension(exeRelativePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(exeName)) {
                    return false;
                }

                return ProcessCountByName(exeName) > 0;
            }
            catch (Exception ex) {
                // Опрос процессов может быть запрещён политиками — не мешаем операции
                Logging.Logger.Warn($"GamePage.IsGameRunning: {ex.Message}");
                return false;
            }
        }

        private static int DefaultProcessCount(string name) => Process.GetProcessesByName(name).Length;
    }
}
