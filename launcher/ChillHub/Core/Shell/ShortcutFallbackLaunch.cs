// <copyright file="ShortcutFallbackLaunch.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Запуск игры в обход лаунчера — единственная ветка, где это ещё делается.
    /// <para>
    /// Обычный запуск идёт с главной страницы и знает про версию, модпак и целостность
    /// файлов (см. <see cref="Home.GameLaunch"/>). Сюда приходят только тогда, когда игры
    /// в каталоге нет вовсе: сверять не с чем, а установленные файлы на диске лежат.
    /// Наигранное время здесь не считается — игры, которой нет в каталоге, нет и в
    /// статистике.
    /// </para>
    /// </summary>
    internal static class ShortcutFallbackLaunch {
        /// <summary>
        /// Сам старт процесса. Шов того же назначения, что и в
        /// <see cref="Home.GameLaunch.StartProcess"/>: прогон тестов не запускает
        /// посторонних программ.
        /// </summary>
        internal static Action<ProcessStartInfo> StartProcess { get; set; } = DefaultStartProcess;

        /// <summary>Возвращает запуск к настоящему процессу.</summary>
        internal static void ResetForTests() => StartProcess = DefaultStartProcess;

        /// <summary>
        /// Запускает exe игры как есть.
        /// </summary>
        /// <param name="exePath">Полный путь к исполняемому файлу.</param>
        /// <returns>true, если процесс запущен.</returns>
        internal static bool TryStart(string? exePath) {
            if (string.IsNullOrWhiteSpace(exePath)) {
                return false;
            }

            try {
                if (!File.Exists(exePath)) {
                    // Файл мог исчезнуть между показом окна и нажатием кнопки.
                    Logging.Logger.Warn($"ShortcutFallbackLaunch: не найден исполняемый файл '{exePath}'");
                    return false;
                }

                StartProcess(new ProcessStartInfo {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ShortcutFallbackLaunch('{exePath}'): {ex.Message}");
                return false;
            }
        }

        private static void DefaultStartProcess(ProcessStartInfo psi) => Process.Start(psi);
    }
}
