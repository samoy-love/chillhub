// <copyright file="LaunchPlanTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.Linq;

    using ChillHub.Core;
    using ChillHub.Core.Maintenance;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Решения меню запуска: что делает щелчок по строке и когда рядом с «Играть»
    /// появляется стрелка выбора.
    /// <para>
    /// Ошибка здесь не падает, а тихо делает не то: «установить моды» вместо «играть»
    /// стоит полутора гигабайт трафика, «играть» вместо «установить» — запуска игры
    /// без модов, которых игрок ждал.
    /// </para>
    /// </summary>
    public class LaunchPlanTests {
        private static readonly MaintenanceState Off = new() { Enabled = false };

        /// <summary>Готовый к запуску вариант запускается.</summary>
        [Fact]
        public void ГотовыйВариантЗапускается() {
            Assert.Equal(LaunchStep.Play, LaunchPlan.Decide(Option(LaunchAction.Play), Off).Step);
        }

        /// <summary>Строка «установить моды» ставит их и запускает — это один шаг для игрока.</summary>
        [Fact]
        public void СтрокаУстановкиМодовВедётКЗапуску() {
            Assert.Equal(
                LaunchStep.InstallModsThenPlay,
                LaunchPlan.Decide(Option(LaunchAction.InstallMods), Off).Step);
        }

        /// <summary>Установка и обновление сборки уходят в очередь загрузок.</summary>
        [Fact]
        public void СборкаУходитВОчередь() {
            Assert.Equal(LaunchStep.Enqueue, LaunchPlan.Decide(Option(LaunchAction.InstallGame), Off).Step);
            Assert.Equal(LaunchStep.Enqueue, LaunchPlan.Decide(Option(LaunchAction.Update), Off).Step);
        }

        /// <summary>Недоступная строка ничего не делает, но объясняет причину.</summary>
        [Fact]
        public void НедоступнаяСтрокаНичегоНеДелаетНоОбъясняет() {
            var decision = LaunchPlan.Decide(Option(LaunchAction.Unavailable, "Steam не установлен"), Off);

            Assert.Equal(LaunchStep.Nothing, decision.Step);
            Assert.Equal("Steam не установлен", decision.Message);
        }

        /// <summary>Пустая строка меню — тоже ничего; вызывающий не обязан это проверять.</summary>
        [Fact]
        public void ОтсутствиеВариантаНичегоНеДелает() {
            Assert.Equal(LaunchStep.Nothing, LaunchPlan.Decide(null, Off).Step);
        }

        /// <summary>
        /// РЕЖИМ РАБОТ ПРОВЕРЯЕТСЯ ПО ДЕЙСТВИЮ, А НЕ ПО ОДНОМУ «ЗАПУСК ЗАПРЕЩЁН».
        /// Работы, закрывшие только установку, не должны мешать играть в уже
        /// установленное — и наоборот.
        /// </summary>
        [Fact]
        public void РежимРаботЗапрещаетТолькоСвоё() {
            // действие, blocksInstall, blocksUpdate, blocksPlay, ожидаемый шаг
            Check(LaunchAction.Play, false, false, true, LaunchStep.Blocked);
            Check(LaunchAction.Play, true, true, false, LaunchStep.Play);
            Check(LaunchAction.InstallGame, true, false, false, LaunchStep.Blocked);
            Check(LaunchAction.InstallGame, false, true, true, LaunchStep.Enqueue);
            Check(LaunchAction.Update, false, true, false, LaunchStep.Blocked);
            Check(LaunchAction.InstallMods, false, true, false, LaunchStep.Blocked);
            Check(LaunchAction.InstallMods, true, false, true, LaunchStep.InstallModsThenPlay);

            static void Check(
                LaunchAction action, bool blocksInstall, bool blocksUpdate, bool blocksPlay, LaunchStep expected) {
                var state = new MaintenanceState {
                    Enabled = true,
                    Blocks = new MaintenanceBlocks { Install = blocksInstall, Update = blocksUpdate, Launch = blocksPlay },
                };

                Assert.Equal(expected, LaunchPlan.Decide(Option(action), state).Step);
            }
        }

        /// <summary>Запрет сопровождается текстом баннера, а не молчанием.</summary>
        [Fact]
        public void ЗапретОбъясняетсяТекстом() {
            var state = new MaintenanceState { Enabled = true, Blocks = new MaintenanceBlocks { Launch = true } };

            var decision = LaunchPlan.Decide(Option(LaunchAction.Play), state);

            Assert.Equal(LaunchStep.Blocked, decision.Step);
            Assert.NotEmpty(decision.Message);
        }

        /// <summary>У игры без настроек модов вариантов запуска нет вовсе.</summary>
        [Fact]
        public void БезНастроекМодовВариантовНет() {
            Assert.Empty(LaunchPlan.OptionsFor(new GameInfo { GameId = "x" }, Probes()));
            Assert.Empty(LaunchPlan.OptionsFor(null, Probes()));
        }

        /// <summary>
        /// Полный набор: сборка на сервере есть и стоит на диске, копия в Steam
        /// найдена, модпак в ней тот же — все четыре строки готовы к запуску.
        /// </summary>
        [Fact]
        public void ПолныйНаборДаётЧетыреГотовыеСтроки() {
            var game = new GameInfo {
                GameId = "lethal-company",
                LatestVersion = "1.0.7",
                NeedsUpdate = false,
                Mods = Pack(),
            };

            var options = LaunchPlan.OptionsFor(game, Probes(modsInSteam: "ASTeam-LethalReloaded-2.2.12"));

            Assert.Equal(4, options.Count);
            Assert.Equal(2, options.Count(o => o.ViaSteam));
        }

        /// <summary>
        /// Сборки на сервере нет — строк две. LatestVersion пуст ровно у такой игры:
        /// она есть только в Steam.
        /// </summary>
        [Fact]
        public void БезСборкиНаСервереСтрокДве() {
            var game = new GameInfo { GameId = "how-to-fish", LatestVersion = string.Empty, Mods = Pack() };

            var options = LaunchPlan.OptionsFor(game, Probes(modsInSteam: "ASTeam-LethalReloaded-2.2.12"));

            Assert.Equal(2, options.Count);
            Assert.All(options, o => Assert.True(o.ViaSteam));
        }

        /// <summary>Ход поиска Steam уходит в журнал, когда его об этом просят.</summary>
        [Fact]
        public void ХодПоискаSteamПопадаетВЖурнал() {
            var lines = new List<string>();
            var game = new GameInfo { GameId = "g", LatestVersion = "1", Mods = Pack() };

            LaunchPlan.OptionsFor(game, Probes(trace: new[] { "смотрю реестр" }, log: lines.Add));

            Assert.Contains(lines, l => l.Contains("смотрю реестр", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// После установки запускается ПЕРЕСЧИТАННАЯ строка, а не та, по которой
        /// щёлкнули: в старой записано, что модов нет.
        /// </summary>
        [Fact]
        public void ПослеУстановкиБерётсяГотоваяСтрока() {
            var ready = Option(LaunchAction.Play);
            var notReady = new LaunchOption(
                LaunchTarget.LocalModded, "x", @"C:\g", true, LaunchAction.InstallGame, "установить");

            Assert.Same(ready, LaunchPlan.ReadyAfterInstall(new[] { notReady, ready }, LaunchTarget.SteamModded));
            Assert.Null(LaunchPlan.ReadyAfterInstall(new[] { notReady }, LaunchTarget.LocalModded));
            Assert.Null(LaunchPlan.ReadyAfterInstall(null, LaunchTarget.SteamModded));
        }

        private static LaunchProbes Probes(
            string modsInSteam = "", string[]? trace = null, System.Action<string>? log = null)
            => new(
                gid => @"C:\games\" + gid,
                _ => true,
                (_, _) => new SteamGame(SteamLookup.Found, @"C:\steam\game", @"C:\steam\steam.exe", trace ?? System.Array.Empty<string>()),
                _ => modsInSteam,
                log);

        private static ModsInfo Pack() => new() {
            HasLatest = true,
            SteamAppId = "1966720",
            DisplayName = "Lethal Reloaded",
            DisplayVersion = "2.2.12",
        };

        private static LaunchOption Option(LaunchAction action, string note = "")
            => new(LaunchTarget.SteamModded, "Steam · с модами", @"C:\game", true, action, note);
    }
}
