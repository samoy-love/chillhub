// <copyright file="RunningGames.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;

    /// <summary>В каком состоянии игра прямо сейчас с точки зрения запуска.</summary>
    internal enum GameRunState {
        /// <summary>Игра не запущена и не запускается.</summary>
        None,

        /// <summary>
        /// Запуск начат, но процесса игры ещё не видно. Через Steam между командой и
        /// окном игры проходят десятки секунд, и всё это время лаунчер не имел ничего
        /// сказать о происходящем.
        /// </summary>
        Starting,

        /// <summary>Процесс игры найден и жив.</summary>
        Running,
    }

    /// <summary>
    /// Какие игры сейчас запущены или запускаются.
    /// <para>
    /// ЗАПУСК, ПОСЛЕ КОТОРОГО НИЧЕГО НЕ ПРОИСХОДИТ, ЧИТАЕТСЯ КАК СЛОМАННАЯ КНОПКА.
    /// Игра поднимается секунды, а через Steam — до минуты, и всё это время витрина
    /// выглядела ровно так же, как до нажатия. Игрок жал «Пиратка · с модами» второй
    /// и третий раз, и лаунчер послушно поднимал вторую и третью копию игры.
    /// </para>
    /// <para>
    /// Знание о запущенном уже есть — его накапливает <see cref="PlaytimeStore"/>,
    /// который дожидается выхода процесса ради наигранного времени и выключения модов.
    /// Здесь то же самое состояние выведено наружу: сессия заводится и закрывается в
    /// одном месте, а витрина узнаёт об этом событием, а не опросом.
    /// </para>
    /// <para>
    /// Ключ «запущенного» — номер процесса, а не игра: одна игра может идти в двух
    /// копиях (запустили из Steam, а лаунчер после перезапуска досматривает обе), и
    /// выход одной не должен гасить отметку о второй.
    /// </para>
    /// </summary>
    internal static class RunningGames {
        private static readonly object Gate = new object();

        /// <summary>Сколько запусков этой игры ждут появления процесса.</summary>
        private static readonly Dictionary<string, int> StartingCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Номер процесса — игра, которой он принадлежит.</summary>
        private static readonly Dictionary<int, string> RunningByPid = new Dictionary<int, string>();

        /// <summary>Состояние любой из игр изменилось.</summary>
        internal static event Action? Changed;

        /// <summary>Что сейчас с игрой.</summary>
        /// <param name="gameId">Игра; пусто — <see cref="GameRunState.None"/>.</param>
        /// <returns>Состояние запуска.</returns>
        internal static GameRunState StateOf(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return GameRunState.None;
            }

            lock (Gate) {
                foreach (var kv in RunningByPid) {
                    if (string.Equals(kv.Value, gameId, StringComparison.OrdinalIgnoreCase)) {
                        return GameRunState.Running;
                    }
                }

                return StartingCounts.ContainsKey(gameId) ? GameRunState.Starting : GameRunState.None;
            }
        }

        /// <summary>Отмечает начатый запуск, процесса которого ещё не видно.</summary>
        /// <param name="gameId">Игра.</param>
        internal static void BeginStarting(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            lock (Gate) {
                StartingCounts.TryGetValue(gameId!, out var count);
                StartingCounts[gameId!] = count + 1;
            }

            Raise();
        }

        /// <summary>
        /// Снимает отметку о начатом запуске: процесс нашёлся либо ждать его больше
        /// незачем. Счётчик, а не флаг: параллельных ожиданий на одну игру может быть
        /// несколько, и первое закончившееся не должно отменять остальные.
        /// </summary>
        /// <param name="gameId">Игра.</param>
        internal static void EndStarting(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            lock (Gate) {
                if (!StartingCounts.TryGetValue(gameId!, out var count)) {
                    return;
                }

                if (count <= 1) {
                    StartingCounts.Remove(gameId!);
                }
                else {
                    StartingCounts[gameId!] = count - 1;
                }
            }

            Raise();
        }

        /// <summary>Отмечает игру запущенной: процесс найден.</summary>
        /// <param name="gameId">Игра.</param>
        /// <param name="processId">Номер процесса игры.</param>
        internal static void MarkRunning(string? gameId, int processId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            lock (Gate) {
                if (RunningByPid.TryGetValue(processId, out var known)
                    && string.Equals(known, gameId, StringComparison.OrdinalIgnoreCase)) {
                    return;
                }

                RunningByPid[processId] = gameId!;
            }

            Raise();
        }

        /// <summary>Снимает отметку: процесс игры завершился.</summary>
        /// <param name="processId">Номер процесса.</param>
        internal static void ClearRunning(int processId) {
            lock (Gate) {
                if (!RunningByPid.Remove(processId)) {
                    return;
                }
            }

            Raise();
        }

        /// <summary>Забывает всё, что накопилось, — для тестов.</summary>
        internal static void ResetForTests() {
            lock (Gate) {
                StartingCounts.Clear();
                RunningByPid.Clear();
            }
        }

        /// <summary>
        /// Сообщает подписчикам, не держа замок: обработчик уходит в диспетчер окна и
        /// оттуда снова спрашивает состояние — под замком это был бы тупик.
        /// </summary>
        private static void Raise() {
            try {
                Changed?.Invoke();
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"RunningGames.Changed: {ex.Message}");
            }
        }
    }
}
