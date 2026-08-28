// <copyright file="LaunchRunnerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Maintenance;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Щелчок по строке меню запуска: от решения до игры.
    /// <para>
    /// Цепочка короткая, но каждое её звено дорого ошибается: не запомнить выбор —
    /// вернуть игрока в меню на следующий запуск; поставить моды без вопроса —
    /// записать полтора гигабайта в чужую установку Steam; запустить по старой
    /// строке — стартовать игру без модов, которых игрок ждал.
    /// </para>
    /// </summary>
    public class LaunchRunnerTests : IDisposable {
        private static readonly MaintenanceState Off = new() { Enabled = false };

        private readonly TempDir dir = new();

        /// <inheritdoc/>
        public void Dispose() {
            this.dir.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>Папка, в которой Doorstop действительно стоит: только тогда «играть» готово.</summary>
        private string ModdedDir() {
            this.dir.WriteFile("winhttp.dll", "proxy");
            this.dir.WriteFile("doorstop_config.ini", "[General]\nenabled = true\n");
            return this.dir.Root;
        }

        /// <summary>Готовая строка запускает игру и запоминает выбор.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ГотоваяСтрокаЗапускаетИЗапоминает() {
            var probe = new Probe();

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.Play), Off, this.Probes());

            Assert.Single(probe.Launched);
            Assert.Equal(LaunchTarget.SteamModded, probe.Remembered);
            Assert.Equal(1, probe.Refreshed);
        }

        /// <summary>Установка сборки уходит в очередь, а не в запуск.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task УстановкаСборкиУходитВОчередь() {
            var probe = new Probe();

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.InstallGame), Off, this.Probes());

            Assert.Equal(new[] { "game" }, probe.Enqueued);
            Assert.Empty(probe.Launched);
        }

        /// <summary>Игра уже в очереди — об этом говорят, а не молчат.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПовторнаяПостановкаВОчередьОбъясняется() {
            var probe = new Probe { EnqueueResult = false };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.InstallGame), Off, this.Probes());

            Assert.Contains(probe.Statuses, t => t.Contains("очереди", StringComparison.Ordinal));
        }

        /// <summary>
        /// Выбор запоминается ДО закачки: к её концу пользователя перед лаунчером
        /// обычно уже нет, и «запомню потом» означает «не запомню».
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ВыборЗапоминаетсяДажеКогдаИграНеЗапускается() {
            var probe = new Probe();

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.Update), Off, this.Probes());

            Assert.Equal(LaunchTarget.SteamModded, probe.Remembered);
        }

        /// <summary>Недоступная строка ничего не делает и объясняет причину.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task НедоступнаяСтрокаТолькоОбъясняет() {
            var probe = new Probe();

            await probe.Runner().RunAsync(
                Game(), this.Option(LaunchAction.Unavailable, "Steam не установлен"), Off, this.Probes());

            Assert.Contains("Steam не установлен", probe.Statuses);
            Assert.Empty(probe.Launched);
            Assert.Null(probe.Remembered);
        }

        /// <summary>Режим технических работ останавливает действие до всякой памяти.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task РежимРаботОстанавливаетДействие() {
            var probe = new Probe();
            var state = new MaintenanceState { Enabled = true, Blocks = new MaintenanceBlocks { Launch = true } };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.Play), state, this.Probes());

            Assert.Empty(probe.Launched);
            Assert.Null(probe.Remembered);
            Assert.NotEmpty(probe.Statuses);
        }

        /// <summary>Установка модов спрашивает разрешение и после согласия запускает игру.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task УстановкаМодовСпрашиваетИЗатемЗапускает() {
            var probe = new Probe { ConfirmAnswer = true, InstallResult = true };
            this.ModdedDir();

            await probe.Runner().RunAsync(
                Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes(modsInSteam: "pack-1"));

            Assert.Single(probe.Questions);
            Assert.Single(probe.Installed);
            Assert.Single(probe.Launched);
            Assert.True(probe.Launched[0].ReadyToPlay);
        }

        /// <summary>Отказ игрока останавливает всё: чужую установку Steam не трогают.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ОтказОстанавливаетУстановку() {
            var probe = new Probe { ConfirmAnswer = false };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes());

            Assert.Empty(probe.Installed);
            Assert.Empty(probe.Launched);
        }

        /// <summary>Неудачная установка не ведёт к запуску игры без модов.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task НеудачнаяУстановкаНеЗапускаетИгру() {
            var probe = new Probe { ConfirmAnswer = true, InstallResult = false };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes());

            Assert.Single(probe.Installed);
            Assert.Empty(probe.Launched);
        }

        /// <summary>
        /// Установка прошла, но моды в папке так и не появились — запускать «с модами»
        /// нельзя: это была бы ванильная игра под видом модовой.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task БезГотовойСтрокиПослеУстановкиЗапускаНет() {
            var probe = new Probe { ConfirmAnswer = true, InstallResult = true };

            await probe.Runner().RunAsync(
                Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes(modsInSteam: string.Empty));

            Assert.Single(probe.Installed);
            Assert.Empty(probe.Launched);
        }

        /// <summary>Пока моды ставятся, второй щелчок не начинает вторую установку.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ВтораяУстановкаНеНачинается() {
            var probe = new Probe { ConfirmAnswer = true, Busy = true };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes());

            Assert.Empty(probe.Installed);
            Assert.NotEmpty(probe.Toasts);
        }

        /// <summary>Без игры или без строки делать нечего — и падать не за что.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПустыеАргументыНичегоНеЛомают() {
            var probe = new Probe();

            await probe.Runner().RunAsync(null, this.Option(LaunchAction.Play), Off, this.Probes());
            await probe.Runner().RunAsync(Game(), null, Off, this.Probes());
            await probe.Runner().RunAsync(new GameInfo { GameId = "x" }, this.Option(LaunchAction.Play), Off, this.Probes());

            Assert.Empty(probe.Launched);
        }

        private static GameInfo Game() => new() {
            GameId = "game",
            Title = "Lethal Company",
            LatestVersion = "1.0.0",
            Mods = new ModsInfo {
                HasLatest = true,
                Version = "pack-1",
                SteamAppId = "1966720",
                DisplayName = "Pack",
                DisplayVersion = "1",
            },
        };

        private LaunchOption Option(LaunchAction action, string note = "")
            => new(LaunchTarget.SteamModded, "Steam", this.dir.Root, true, action, note);

        private LaunchProbes Probes(string modsInSteam = "pack-1")
            => new(
                gid => @"C:\games\" + gid,
                _ => true,
                (_, _) => new SteamGame(
                    SteamLookup.Found, this.dir.Root, @"C:\steam\steam.exe", Array.Empty<string>()),
                _ => modsInSteam);

        /// <summary>Экран, заменённый на список того, что на нём произошло.</summary>
        private sealed class Probe {
            internal List<string> Statuses { get; } = new();

            internal List<string> Toasts { get; } = new();

            internal List<string> Questions { get; } = new();

            internal List<string> Enqueued { get; } = new();

            internal List<string> Installed { get; } = new();

            internal List<LaunchOption> Launched { get; } = new();

            internal LaunchTarget? Remembered { get; private set; }

            internal int Refreshed { get; private set; }

            internal bool ConfirmAnswer { get; set; }

            internal bool InstallResult { get; set; }

            internal bool EnqueueResult { get; set; } = true;

            internal bool Busy { get; set; }

            internal LaunchRunner Runner() => new(new LaunchUi {
                SetStatus = t => this.Statuses.Add(t),
                Toast = t => this.Toasts.Add(t),
                Confirm = (text, _) => {
                    this.Questions.Add(text);
                    return this.ConfirmAnswer;
                },
                Enqueue = gid => {
                    this.Enqueued.Add(gid);
                    return this.EnqueueResult;
                },
                RefreshChoice = () => this.Refreshed++,
                InstallMods = (_, _, dir) => {
                    this.Installed.Add(dir);
                    return Task.FromResult(this.InstallResult);
                },
                Launch = (_, option) => this.Launched.Add(option),
            }) {
                ModsBusy = () => this.Busy,
                Remember = (_, target) => this.Remembered = target,
            };
        }
    }
}
