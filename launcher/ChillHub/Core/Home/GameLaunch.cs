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
        /// не запуская в прогоне тестов посторонних программ.
        /// </summary>
        internal static Action<ProcessStartInfo> StartProcess { get; set; } = DefaultStartProcess;

        /// <summary>
        /// Запоминает последнюю запущенную игру в настройках. Шов того же назначения:
        /// настоящая реализация пишет config.json пользователя.
        /// </summary>
        internal static Action<string> RememberLastGame { get; set; } = DefaultRememberLastGame;

        /// <summary>
        /// Побочные действия после успешного старта: метрика и статус в Discord.
        /// Шов того же назначения — обе уходят в сеть, а прогон тестов в сеть не ходит.
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
        /// <param name="profile">
        /// Выбранный модпак-профиль (первая итерация трека F — без реальной установки
        /// модов): даёт путь к папке модов и доп. аргументы командной строки. Null — как
        /// если бы профилей не было вовсе.
        /// </param>
        /// <returns>Что показать пользователю.</returns>
        internal static LaunchResult Play(string? gid, IEnumerable<GameInfo>? games, MaintenanceState maintenance, ModProfile? profile = null) {
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
                ApplyModProfile(psi, localRoot, profile);

                // DefaultStartProcess нужен gameId, чтобы завести отсчёт наигранного времени,
                // но публичный шов StartProcess остаётся Action<ProcessStartInfo> — его сигнатуру
                // используют существующие тесты. Передаём id через поле, читаемое только внутри
                // настоящей (не тестовой) реализации запуска.
                pendingPlaytimeGameId = selected;
                StartProcess(psi);
                AfterStarted(game);

                return new LaunchResult(LaunchOutcome.Started, string.Empty);
            }
            catch (Exception ex) {
                return new LaunchResult(LaunchOutcome.Failed, "Не удалось запустить игру.", "HomePage.PlaySelectedGame", ex);
            }
        }

        /// <summary>
        /// Первая итерация модпак-профилей (трек F): реальной установки модов нет — только
        /// доп. аргументы командной строки и путь к папке модов (сообщается игре через
        /// аргумент, а не подкладыванием файлов — этим займётся трек K).
        /// </summary>
        private static void ApplyModProfile(ProcessStartInfo psi, string localRoot, ModProfile? profile) {
            if (profile == null) {
                return;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(psi.Arguments)) {
                parts.Add(psi.Arguments);
            }

            if (!string.IsNullOrWhiteSpace(profile.ModFolder)) {
                var modPath = Path.Combine(localRoot, profile.ModFolder!.Replace('/', Path.DirectorySeparatorChar));
                parts.Add($"--mods \"{modPath}\"");
            }

            if (!string.IsNullOrWhiteSpace(profile.ExtraArgs)) {
                parts.Add(profile.ExtraArgs!);
            }

            psi.Arguments = string.Join(' ', parts);
        }

        /// <summary>gameId последнего запуска — читается только <see cref="DefaultStartProcess"/>, см. её вызов в <see cref="Play"/>.</summary>
        private static string? pendingPlaytimeGameId;

        private static void DefaultStartProcess(ProcessStartInfo psi) {
            var proc = Process.Start(psi);
            var gameId = pendingPlaytimeGameId;
            pendingPlaytimeGameId = null;

            // Наигранное время считается на выходе процесса ИГРЫ, не лаунчера (трек E):
            // PlaytimeStore сам переживёт закрытие лаунчера раньше игры — сессия закрывается
            // либо тем же лаунчером в фоне, либо следующим его запуском (см. EnsureReconciled).
            if (proc != null && !string.IsNullOrWhiteSpace(gameId)) {
                try {
                    PlaytimeStore.BeginSession(gameId!, proc);
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"GameLaunch.DefaultStartProcess: не удалось завести отсчёт времени: {ex.Message}");
                }
            }
        }

        private static void DefaultAfterStarted(GameInfo game) {
            Metrics.MetricsService.GameLaunch(game.GameId, game.LatestVersion);

            // Discord Rich Presence: полностью опционален и не должен влиять на запуск.
            // Пока Application ID не задан владельцем — вызов сразу выходит.
            DiscordRichPresence.SetPlaying(game.Title, game.LatestVersion);
        }

        private static void DefaultRememberLastGame(string gameId) {
            var cfg = ConfigService.Current;
            cfg.LastGameId = gameId;
            ConfigService.Save(cfg);
        }
    }
}
