// <copyright file="GameLaunch.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;

    using ChillHub.Core.Game;
    using ChillHub.Core.Maintenance;

    using static ChillHub.Core.Home.GameLocalState;

    /// <summary>Чем закончилась попытка запустить игру.</summary>
    internal enum LaunchOutcome {
        /// <summary>Игра не выбрана.</summary>
        NoGameSelected,

        /// <summary>Выбранной игры нет в списке.</summary>
        NotInList,

        /// <summary>Для игры не указан путь к исполняемому файлу.</summary>
        NoExePath,

        /// <summary>Запуск запрещён режимом технических работ.</summary>
        BlockedByMaintenance,

        /// <summary>На диске остался след незавершённого обновления.</summary>
        UnfinishedUpdate,

        /// <summary>Исполняемого файла нет на диске.</summary>
        ExeMissing,

        /// <summary>Процесс игры запущен.</summary>
        Started,

        /// <summary>Запуск сорвался исключением.</summary>
        Failed,
    }

    /// <summary>
    /// Итог попытки запуска: что показать пользователю и что записать в лог.
    /// </summary>
    /// <param name="Outcome">Чем закончилась попытка.</param>
    /// <param name="Message">Короткий текст для пользователя.</param>
    /// <param name="Context">Технические подробности для лога и подсказки.</param>
    /// <param name="Error">Исключение, если оно было.</param>
    internal sealed record LaunchResult(LaunchOutcome Outcome, string Message, string? Context = null, Exception? Error = null);

    /// <summary>
    /// Запуск установленной игры: путь к исполняемому файлу, запреты и сам старт процесса.
    /// <para>
    /// Ни один отказ не должен выглядеть как «ничего не произошло»: каждая ветка возвращает
    /// текст, который вызывающий код показывает в строке состояния.
    /// </para>
    /// </summary>
    internal static class GameLaunch {
        /// <summary>
        /// Запускает процесс. Отдельным швом — чтобы проверять сборку пути и проверки запрета,
        /// не запуская в прогоне тестов посторонних программ. gameId идёт вторым параметром
        /// (а не через общее состояние), чтобы конкурентные вызовы <see cref="Play"/> для
        /// разных игр не могли перепутать, чьё наигранное время считать.
        /// </summary>
        internal static Action<ProcessStartInfo, string> StartProcess { get; set; } = DefaultStartProcess;

        /// <summary>
        /// Запоминает последнюю запущенную игру в настройках. Шов того же назначения:
        /// настоящая реализация пишет config.json пользователя.
        /// </summary>
        internal static Action<string> RememberLastGame { get; set; } = DefaultRememberLastGame;

        /// <summary>
        /// Побочные действия после успешного старта: отправка метрики. Шов того же
        /// назначения — она уходит в сеть, а прогон тестов в сеть не ходит.
        /// </summary>
        internal static Action<GameInfo> AfterStarted { get; set; } = DefaultAfterStarted;

        /// <summary>Возвращает запуск к настоящим процессу, настройкам и отчётам.</summary>
        internal static void ResetForTests() {
            StartProcess = DefaultStartProcess;
            RememberLastGame = DefaultRememberLastGame;
            AfterStarted = DefaultAfterStarted;
        }

        /// <summary>
        /// Пытается запустить выбранную игру.
        /// </summary>
        /// <param name="gid">Идентификатор выбранной игры.</param>
        /// <param name="games">Список игр главного экрана.</param>
        /// <param name="maintenance">Текущее состояние режима технических работ.</param>
        /// <returns>Что показать пользователю.</returns>
        internal static LaunchResult Play(string? gid, IEnumerable<GameInfo>? games, MaintenanceState maintenance) {
            try {
                if (gid is not string selected || string.IsNullOrWhiteSpace(selected)) {
                    return new LaunchResult(LaunchOutcome.NoGameSelected, "Не выбрана игра");
                }

                var game = games?.FirstOrDefault(g => g.GameId == selected);
                if (game == null) {
                    return new LaunchResult(LaunchOutcome.NotInList, "Игра не найдена в списке");
                }

                if (string.IsNullOrWhiteSpace(game.ExeRelativePath)) {
                    return new LaunchResult(
                        LaunchOutcome.NoExePath,
                        "Для игры не указан путь к исполняемому файлу. Настройте его в админ-панели.");
                }

                // Сервер может запретить и запуск (например, работы на игровых серверах)
                if (maintenance.BlocksPlay) {
                    return new LaunchResult(LaunchOutcome.BlockedByMaintenance, maintenance.BuildBannerText());
                }

                // Предыдущее обновление не довели до конца — файлы игры смешаны из двух версий (C2)
                if (HasUnfinishedUpdate(selected)) {
                    return new LaunchResult(
                        LaunchOutcome.UnfinishedUpdate,
                        "Обновление не завершено. Нажмите «Обновить», чтобы восстановить игру.");
                }

                // Запомним последнюю запущенную игру
                RememberLastGame(selected);
                var localRoot = GameLocalRoot(selected);
                var rel = game.ExeRelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var exePath = Path.Combine(localRoot, rel);
                if (!File.Exists(exePath)) {
                    // Пользователю — короткое объяснение, путь оставляем в подсказке и логе (C5)
                    return new LaunchResult(
                        LaunchOutcome.ExeMissing,
                        "Файлы игры повреждены или неполные. Нажмите «Обновить», чтобы восстановить.",
                        $"PlaySelectedGame: не найден исполняемый файл '{exePath}'");
                }

                var psi = new ProcessStartInfo {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? localRoot,
                    UseShellExecute = true,
                };

                StartProcess(psi, selected);
                AfterStarted(game);

                return new LaunchResult(LaunchOutcome.Started, string.Empty);
            }
            catch (Exception ex) {
                return new LaunchResult(LaunchOutcome.Failed, "Не удалось запустить игру.", "HomePage.PlaySelectedGame", ex);
            }
        }

        private static void DefaultStartProcess(ProcessStartInfo psi, string gameId) {
            var proc = Process.Start(psi);

            // Наигранное время считается на выходе процесса ИГРЫ, не лаунчера:
            // PlaytimeStore сам переживёт закрытие лаунчера раньше игры — сессия закрывается
            // либо тем же лаунчером в фоне, либо следующим его запуском (см. EnsureReconciled).
            if (proc != null && !string.IsNullOrWhiteSpace(gameId)) {
                try {
                    // Этот путь ведёт только к сборке с сервера и только без модов:
                    // игру с модпаком запускает Mods.ModsLaunch, а не он.
                    PlaytimeStore.BeginSession(gameId, Mods.LaunchTarget.LocalVanilla, proc);
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"GameLaunch.DefaultStartProcess: не удалось завести отсчёт времени: {ex.Message}");
                }
            }
        }

        private static void DefaultAfterStarted(GameInfo game) {
            Metrics.MetricsService.GameLaunch(game.GameId, game.LatestVersion);
        }

        private static void DefaultRememberLastGame(string gameId) {
            var cfg = ConfigService.Current;
            cfg.LastGameId = gameId;
            ConfigService.Save(cfg);
        }
    }
}
