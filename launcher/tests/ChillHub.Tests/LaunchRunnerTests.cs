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
    /// вернуть игрока в меню на следующий запуск; починить моды и уйти в игру —
    /// запустить её там, где игрок просил одну лишь починку; запустить по старой
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

        /// <summary>
        /// Установка модов идёт без вопросов и доводит до игры одним щелчком: на строке
        /// меню и так написано, куда лягут файлы, а окно с тем же текстом стояло между
        /// «хочу играть с модами» и игрой на каждом запуске.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task УстановкаМодовСразуЗапускаетИгру() {
            var probe = new Probe { InstallResult = true };
            this.ModdedDir();

            await probe.Runner().RunAsync(
                Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes(modsInSteam: "pack-1"));

            Assert.Single(probe.Installed);
            Assert.Single(probe.Launched);
            Assert.True(probe.Launched[0].ReadyToPlay);
        }

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА ПОЧИНКИ: она чинит и останавливается. Игрок нажимал
        /// «восстановить моды» — ровно одно действие, — и начавшаяся следом игра
        /// оказывалась неожиданностью. Запускает уже следующий щелчок.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПочинкаМодовИгруНеЗапускает() {
            var probe = new Probe { InstallResult = true };
            this.ModdedDir();

            await probe.Runner().RunAsync(
                Game(), this.Option(LaunchAction.RepairMods), Off, this.Probes(modsInSteam: "pack-1"));

            Assert.Single(probe.Installed);
            Assert.Empty(probe.Launched);
        }

        /// <summary>
        /// Конец починки не остаётся в нижней панели: она показывает идущую работу и
        /// уходит с экрана, когда работы нет, а отчёт о кончившейся держал её на экране
        /// до следующей закачки. Рассказывает об исходе всплывашка — её пишет сама
        /// установка (Home.SteamModsInstall.DescribeResult).
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПочинкаНеОставляетОтчётВНижнейПанели() {
            var probe = new Probe { InstallResult = true };
            this.ModdedDir();

            await probe.Runner().RunAsync(
                Game(), this.Option(LaunchAction.RepairMods), Off, this.Probes(modsInSteam: "pack-1"));

            Assert.Empty(probe.Statuses);
        }

        /// <summary>Починка и установка с нуля различаются в подписях, а не только внутри.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПочинкаНазываетсяПочинкой() {
            var probe = new Probe { InstallResult = true };
            this.ModdedDir();

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.RepairMods), Off, this.Probes());
            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes());

            Assert.Equal(new[] { true, false }, probe.Repairs);
        }

        /// <summary>
        /// Неудачная починка игру не запускает: следующий щелчок должен снова предложить
        /// починку, а не игру с половиной модов.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task НеудачнаяПочинкаИгруНеЗапускает() {
            var probe = new Probe { InstallResult = false };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.RepairMods), Off, this.Probes());

            Assert.Single(probe.Installed);
            Assert.Empty(probe.Launched);
        }

        /// <summary>Пока моды ставятся, починка не начинает вторую запись в ту же папку.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПочинкаНеНачинаетсяПоверхУстановки() {
            var probe = new Probe { Busy = true };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.RepairMods), Off, this.Probes());

            Assert.Empty(probe.Installed);
            Assert.NotEmpty(probe.Toasts);
        }

        /// <summary>Режим технических работ, закрывший обновление, закрывает и починку.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task РежимРаботОстанавливаетПочинку() {
            var probe = new Probe { InstallResult = true };
            var state = new MaintenanceState { Enabled = true, Blocks = new MaintenanceBlocks { Update = true } };

            await probe.Runner().RunAsync(Game(), this.Option(LaunchAction.RepairMods), state, this.Probes());

            Assert.Empty(probe.Installed);
            Assert.NotEmpty(probe.Statuses);
        }

        /// <summary>Неудачная установка не ведёт к запуску игры без модов.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task НеудачнаяУстановкаНеЗапускаетИгру() {
            var probe = new Probe { InstallResult = false };

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
            var probe = new Probe { InstallResult = true };

            await probe.Runner().RunAsync(
                Game(), this.Option(LaunchAction.InstallMods), Off, this.Probes(modsInSteam: string.Empty));

            Assert.Single(probe.Installed);
            Assert.Empty(probe.Launched);
        }

        /// <summary>Пока моды ставятся, второй щелчок не начинает вторую установку.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ВтораяУстановкаНеНачинается() {
            var probe = new Probe { Busy = true };

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

            internal List<string> Enqueued { get; } = new();

            internal List<string> Installed { get; } = new();

            /// <summary>Чем была каждая установка: починкой или установкой с нуля.</summary>
            internal List<bool> Repairs { get; } = new();

            internal List<LaunchOption> Launched { get; } = new();

            internal LaunchTarget? Remembered { get; private set; }

            internal int Refreshed { get; private set; }

            internal bool InstallResult { get; set; }

            internal bool EnqueueResult { get; set; } = true;

            internal bool Busy { get; set; }

            internal LaunchRunner Runner() => new(new LaunchUi {
                SetStatus = t => this.Statuses.Add(t),
                Toast = t => this.Toasts.Add(t),
                Enqueue = gid => {
                    this.Enqueued.Add(gid);
                    return this.EnqueueResult;
                },
                RefreshChoice = () => this.Refreshed++,
                InstallMods = (_, _, dir, repair) => {
                    this.Installed.Add(dir);
                    this.Repairs.Add(repair);
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
