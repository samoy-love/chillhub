// <copyright file="GameSession.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;

    /// <summary>
    /// Начало игровой сессии: найти процесс игры и отдать его
    /// <see cref="PlaytimeStore"/>.
    /// <para>
    /// Со времён, когда игра с модами стала запускаться через <c>Mods.ModsLaunch</c>,
    /// наигранное время перестало считаться вовсе: отсчёт заводил только старый прямой
    /// путь <c>Home.GameLaunch</c>, а новый про него не знал. Здесь это место одно на оба
    /// пути.
    /// </para>
    /// <para>
    /// Через Steam всё сложнее: мы стартуем steam.exe, а игру поднимает он сам — и
    /// вернувшийся процесс не тот, чьего выхода надо ждать. Настоящий процесс приходится
    /// дожидаться поиском по папке (см. <see cref="GameProcessFinder"/>).
    /// </para>
    /// </summary>
    internal static class GameSession {
        /// <summary>
        /// Заводит отсчёт времени для только что запущенной игры.
        /// <para>
        /// Ничего не ждёт: поиск процесса, запущенного Steam, уходит в фоновую задачу —
        /// на витрине в этот момент человек уже смотрит на игру, а не на лаунчер.
        /// </para>
        /// </summary>
        /// <param name="gameId">Игра.</param>
        /// <param name="gameDir">Папка, из которой игра запущена.</param>
        /// <param name="exePath">Путь к исполняемому файлу игры; без него процесс не найти.</param>
        /// <param name="started">Процесс, который вернул старт: игра при прямом запуске, Steam — при запуске через него.</param>
        /// <param name="viaSteam">Запуск шёл через Steam.</param>
        /// <param name="moddedDir">Папка с включёнными на время сессии модами; null — запуск без модов.</param>
        internal static void Begin(
            string? gameId,
            string? gameDir,
            string? exePath,
            Process? started,
            bool viaSteam,
            string? moddedDir) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            if (!viaSteam) {
                if (started != null) {
                    Track(gameId, started, moddedDir);
                }

                return;
            }

            _ = Task.Run(async () => {
                try {
                    var pid = await GameProcessFinder.WaitAsync(gameDir, exePath);
                    if (pid is not int found) {
                        // Не дождались — не беда: незакрытой сессии нет, а моды в папке
                        // вернёт в исходное следующий запуск лаунчера.
                        Logging.Logger.Warn($"[mods] процесс игры '{gameId}' в '{gameDir}' так и не появился");
                        return;
                    }

                    Track(gameId, Process.GetProcessById(found), moddedDir);
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"GameSession.Begin({gameId}): {ex.Message}");
                }
            });
        }

        private static void Track(string gameId, Process process, string? moddedDir) {
            try {
                PlaytimeStore.BeginSession(gameId, process, moddedDir);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"GameSession.Track({gameId}): {ex.Message}");
            }
        }
    }
}
