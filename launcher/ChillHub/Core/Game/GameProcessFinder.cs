// <copyright file="GameProcessFinder.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;

    /// <summary>Процесс, найденный в системе: номер и путь к исполняемому файлу.</summary>
    /// <param name="Pid">Номер процесса.</param>
    /// <param name="ExePath">Путь к exe; null — прочитать не удалось.</param>
    internal readonly record struct RunningProcess(int Pid, string? ExePath);

    /// <summary>
    /// Ищет процесс игры, которую запустили не мы.
    /// <para>
    /// Через Steam лаунчер стартует steam.exe с ключом <c>-applaunch</c>, а игру поднимает
    /// сам Steam — вернувшийся процесс к игре отношения не имеет и завершается сразу.
    /// Из-за этого запуск через Steam не давал ни отсчёта наигранного времени, ни момента,
    /// когда игру закрыли, — а этот момент нужен, чтобы вернуть папку в состояние без модов.
    /// </para>
    /// <para>
    /// Ищем по имени исполняемого файла и проверяем, что он лежит В ТОЙ САМОЙ папке: имя
    /// exe у копии из Steam и у сборки с сервера одинаковое, и без проверки пути лаунчер
    /// считал бы временем одной копии игру в другую.
    /// </para>
    /// </summary>
    internal static class GameProcessFinder {
        /// <summary>Сколько ждать появления процесса игры по умолчанию.</summary>
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

        /// <summary>Как часто опрашивать систему, пока игра не появилась.</summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Шов для тестов: список процессов с таким именем. Настоящая реализация ходит в
        /// систему, поэтому в прогоне тестов её подменяют.
        /// </summary>
        internal static Func<string, IReadOnlyList<RunningProcess>> ByName { get; set; } = DefaultByName;

        /// <summary>Возвращает поиск к настоящим процессам системы.</summary>
        internal static void ResetForTests() => ByName = DefaultByName;

        /// <summary>Лежит ли исполняемый файл процесса в этой папке игры.</summary>
        /// <param name="exePath">Путь к exe процесса; null — процесс не наш.</param>
        /// <param name="gameDir">Папка игры.</param>
        /// <returns>true, если процесс запущен из этой папки.</returns>
        internal static bool BelongsTo(string? exePath, string? gameDir) {
            if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(gameDir)) {
                return false;
            }

            try {
                var dir = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var exe = Path.GetFullPath(exePath);
                return exe.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"GameProcessFinder.BelongsTo('{exePath}'): {ex.Message}");
                return false;
            }
        }

        /// <summary>Ищет запущенную прямо сейчас игру из этой папки.</summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <param name="exePath">Путь к исполняемому файлу игры.</param>
        /// <returns>Номер процесса или null, если игра ещё (или уже) не запущена.</returns>
        internal static int? Find(string? gameDir, string? exePath) {
            if (string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(exePath)) {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(exePath);
            if (string.IsNullOrWhiteSpace(name)) {
                return null;
            }

            try {
                foreach (var candidate in ByName(name)) {
                    if (BelongsTo(candidate.ExePath, gameDir)) {
                        return candidate.Pid;
                    }
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"GameProcessFinder.Find('{gameDir}'): {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Ждёт появления процесса игры. Steam умеет тянуть с запуском минуты — обновляет
        /// игру, собирает шейдеры, — поэтому срок ожидания щедрый, а отказ по нему не
        /// ошибка: незакрытая сессия доживёт до следующего запуска лаунчера, где её
        /// подберёт реконсиляция.
        /// </summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <param name="exePath">Путь к исполняемому файлу игры.</param>
        /// <param name="timeout">Сколько ждать; по умолчанию <see cref="DefaultTimeout"/>.</param>
        /// <param name="delay">Чем выдерживать паузу между опросами; шов для тестов.</param>
        /// <returns>Номер процесса или null, если игра так и не появилась.</returns>
        internal static async Task<int?> WaitAsync(
            string? gameDir, string? exePath, TimeSpan? timeout = null, Func<TimeSpan, Task>? delay = null) {
            var deadline = timeout ?? DefaultTimeout;
            var wait = delay ?? Task.Delay;
            var spent = TimeSpan.Zero;

            while (true) {
                if (Find(gameDir, exePath) is int pid) {
                    return pid;
                }

                if (spent >= deadline) {
                    return null;
                }

                await wait(PollInterval);
                spent += PollInterval;
            }
        }

        private static IReadOnlyList<RunningProcess> DefaultByName(string name) {
            var found = new List<RunningProcess>();

            // Каждый Process держит системный хендл, и опрос идёт раз в секунду до трёх
            // минут: неосвобождённые хендлы копились бы всё это время. Нам нужны только
            // номер и путь — сам объект не переживает этот цикл.
            foreach (var p in Process.GetProcessesByName(name)) {
                using (p) {
                    found.Add(new RunningProcess(p.Id, SafeExePath(p)));
                }
            }

            return found;
        }

        /// <summary>
        /// Путь к exe процесса. Обёрнут, потому что у чужих процессов чтение модуля
        /// запрещено системой, и одно такое исключение оборвало бы весь перебор.
        /// </summary>
        /// <param name="process">Процесс.</param>
        /// <returns>Путь или null.</returns>
        private static string? SafeExePath(Process process) {
            try {
                return process.MainModule?.FileName;
            }
            catch {
                return null;
            }
        }
    }
}
