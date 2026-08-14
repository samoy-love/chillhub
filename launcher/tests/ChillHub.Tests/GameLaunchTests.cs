// <copyright file="GameLaunchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Maintenance;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Запуск установленной игры.
    /// <para>
    /// Каждая ветка обязана объяснить пользователю, почему игра не запустилась: молчащая
    /// кнопка «Играть» — самый частый повод написать в обратную связь. Отдельно проверяется
    /// запрет на запуск наполовину обновлённой игры: она собрана из двух версий и падает
    /// уже после старта, когда виноватым выглядит не лаунчер, а игра.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class GameLaunchTests : IDisposable {
        private readonly List<ProcessStartInfo> started = new();
        private readonly List<string> remembered = new();
        private readonly List<GameInfo> reported = new();

        public GameLaunchTests() {
            // Настоящие реализации запускают процесс, пишут config.json пользователя и ходят в сеть
            GameLaunch.StartProcess = (psi, gameId) => this.started.Add(psi);
            GameLaunch.RememberLastGame = gid => this.remembered.Add(gid);
            GameLaunch.AfterStarted = game => this.reported.Add(game);
        }

        public void Dispose() => GameLaunch.ResetForTests();

        /// <summary>Игра не выбрана — говорим об этом, а не запускаем что попало.</summary>
        [Fact]
        public void БезВыбраннойИгрыЗапускаНет() {
            using var games = new GamesPathScope();

            var result = GameLaunch.Play(null, new List<GameInfo>(), MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.NoGameSelected, result.Outcome);
            Assert.Equal("Не выбрана игра", result.Message);
            Assert.Empty(this.started);
        }

        /// <summary>Выбранной игры нет в списке — список успел смениться под руками.</summary>
        [Fact]
        public void ИгрыНетВСпискеЗначитЗапускаНет() {
            using var games = new GamesPathScope();

            var result = GameLaunch.Play("game", new List<GameInfo>(), MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.NotInList, result.Outcome);
        }

        /// <summary>
        /// Путь к исполняемому файлу не заполнен в админ-панели: пользователю называют
        /// именно это, иначе он ищет причину у себя.
        /// </summary>
        [Fact]
        public void БезПутиКExeСообщаемОНастройке() {
            using var games = new GamesPathScope();
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = string.Empty } };

            var result = GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.NoExePath, result.Outcome);
            Assert.Contains("админ-панели", result.Message);
        }

        /// <summary>Сервер запретил запуск — показываем его текст, а не свой.</summary>
        [Fact]
        public void ЗапретСервераОстанавливаетЗапуск() {
            using var games = new GamesPathScope();
            WriteExe(games.Root, "game", "bin/game.exe");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };
            var state = new MaintenanceState {
                Enabled = true,
                Reason = "Работы на игровых серверах",
                Blocks = new MaintenanceBlocks { Launch = true },
            };

            var result = GameLaunch.Play("game", list, state);

            Assert.Equal(LaunchOutcome.BlockedByMaintenance, result.Outcome);
            Assert.Contains("Работы на игровых серверах", result.Message);
            Assert.Empty(this.started);
        }

        /// <summary>
        /// Незавершённое обновление запрещает запуск: файлы игры смешаны из двух версий (C2).
        /// Разрешить старт значит подсунуть пользователю заведомо сломанную игру.
        /// </summary>
        [Fact]
        public void НезавершённоеОбновлениеЗапрещаетЗапуск() {
            using var games = new GamesPathScope();
            var root = WriteExe(games.Root, "game", "bin/game.exe");
            File.WriteAllText(Path.Combine(root, SimpleSyncService.UpdateMarkerFileName), "version=1.0.0");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };

            var result = GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.UnfinishedUpdate, result.Outcome);
            Assert.Contains("Обновление не завершено", result.Message);
            Assert.Empty(this.started);
        }

        /// <summary>
        /// Исполняемого файла нет — предлагаем восстановить, а полный путь оставляем
        /// в технических подробностях: в самом сообщении ему не место (C5).
        /// </summary>
        [Fact]
        public void ПропавшийExeПредлагаетВосстановление() {
            using var games = new GamesPathScope();
            Directory.CreateDirectory(Path.Combine(games.Root, "game"));
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };

            var result = GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.ExeMissing, result.Outcome);
            Assert.Contains("Обновить", result.Message);
            Assert.Contains("game.exe", result.Context);
            Assert.DoesNotContain("game.exe", result.Message);
        }

        /// <summary>Путь к exe собирается от папки игры, а не от корня папки игр.</summary>
        [Fact]
        public void ПутьКExeСобираетсяОтПапкиИгры() {
            using var games = new GamesPathScope();
            var expected = WriteExeFile(games.Root, "game", "bin/game.exe");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };

            var result = GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.Started, result.Outcome);
            Assert.Equal(expected, Assert.Single(this.started).FileName);
        }

        /// <summary>
        /// Разделители в пути из админ-панели приводятся к системным: сервер отдаёт
        /// и «bin/game.exe», и «bin\game.exe», а File.Exists должен найти файл в обоих случаях.
        /// </summary>
        [Fact]
        public void РазделителиВПутиПриводятсяКСистемным() {
            using var games = new GamesPathScope();
            var expected = WriteExeFile(games.Root, "game", "bin/game.exe");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = @"bin\game.exe" } };

            var result = GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.Started, result.Outcome);
            Assert.Equal(expected, Assert.Single(this.started).FileName);
        }

        /// <summary>Рабочий каталог — папка с исполняемым файлом: иначе игра не найдёт свои ресурсы.</summary>
        [Fact]
        public void РабочийКаталогЭтоПапкаExe() {
            using var games = new GamesPathScope();
            var exe = WriteExeFile(games.Root, "game", "bin/game.exe");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };

            GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal(Path.GetDirectoryName(exe), Assert.Single(this.started).WorkingDirectory);
        }

        /// <summary>Успешный запуск запоминается в настройках: с этой игры лаунчер откроется в следующий раз.</summary>
        [Fact]
        public void ЗапущеннаяИграЗапоминается() {
            using var games = new GamesPathScope();
            WriteExeFile(games.Root, "game", "bin/game.exe");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };

            GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal("game", Assert.Single(this.remembered));
        }

        /// <summary>
        /// Метрика запуска уходит только после реального старта: отчёт о запуске,
        /// которого не было, портит статистику.
        /// </summary>
        [Fact]
        public void ОтчётУходитТолькоПослеСтарта() {
            using var games = new GamesPathScope();
            WriteExeFile(games.Root, "game", "bin/game.exe");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };

            GameLaunch.Play("нет-такой", list, MaintenanceState.Off);
            Assert.Empty(this.reported);

            GameLaunch.Play("game", list, MaintenanceState.Off);
            Assert.Equal("game", Assert.Single(this.reported).GameId);
        }

        /// <summary>Отказ до запуска не должен переписывать «последнюю запущенную игру».</summary>
        [Fact]
        public void ОтказНеМеняетПоследнююЗапущенную() {
            using var games = new GamesPathScope();
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = string.Empty } };

            GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Empty(this.remembered);
        }

        /// <summary>Сбой запуска возвращается ошибкой, а не улетает исключением из обработчика кнопки.</summary>
        [Fact]
        public void СбойЗапускаНеБросаетИсключение() {
            using var games = new GamesPathScope();
            WriteExeFile(games.Root, "game", "bin/game.exe");
            GameLaunch.StartProcess = (_, _) => throw new InvalidOperationException("процесс не создан");
            var list = new List<GameInfo> { new GameInfo { GameId = "game", ExeRelativePath = "bin/game.exe" } };

            var result = GameLaunch.Play("game", list, MaintenanceState.Off);

            Assert.Equal(LaunchOutcome.Failed, result.Outcome);
            Assert.Equal("Не удалось запустить игру.", result.Message);
            Assert.NotNull(result.Error);
        }

        private static string WriteExe(string gamesRoot, string gameId, string relative) {
            WriteExeFile(gamesRoot, gameId, relative);
            return Path.Combine(gamesRoot, gameId);
        }

        private static string WriteExeFile(string gamesRoot, string gameId, string relative) {
            var full = Path.Combine(gamesRoot, gameId, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "MZ");
            return full;
        }
    }
}
