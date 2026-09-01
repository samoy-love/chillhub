// <copyright file="SyncErrorEventTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Home;
    using ChillHub.Core.Mods;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Один отказ — одно событие error.
    /// <para>
    /// Событие error — это и «Топ ошибок» в админке, и счётчик отказов. Отправленное
    /// дважды, оно удваивает долю неудач на ровном месте, а если два отправителя
    /// называют причину по-разному, отказ ещё и приписывается не тому коду.
    /// </para>
    /// </summary>
    public class SyncErrorEventTests {
        /// <summary>
        /// Сорвавшийся модпак отчитывается о неудаче операции, но кода ошибки не
        /// добавляет: событие error по этому отказу уже отправил <see cref="ModsService"/>,
        /// и причину он называет точнее.
        /// </summary>
        [Fact]
        public async Task СорвавшийсяМодпакНеДобавляетВторогоСобытияОшибки() {
            using var dir = new TempDir();
            var sent = new List<SyncOutcome>();
            var sync = new FailingModsSync();
            var runner = new GameSyncRunner(sync, new GameSyncUi()) {
                Maintenance = () => new MaintenanceStateView(false, false, string.Empty),
                FreeSpaceFor = _ => long.MaxValue,
                WriteLocalVersion = (_, _) => { },
                ReportOutcome = sent.Add,
            };

            var game = new GameInfo {
                GameId = "game",
                Mods = new ModsInfo {
                    HasLatest = true,
                    Version = "v1",
                    ManifestUrl = "/manifests/_mods/game/v1.json",
                    ContentBaseUrl = "/content/_mods/game/v1/files",
                },
            };
            var request = new GameSyncRequest(
                "game", "1.2.0", "https://example.test", dir.Root, null, false, SyncKind.Update, game);

            await runner.RunAsync(request, CancellationToken.None);

            var outcome = Assert.Single(sent);
            Assert.Equal("fail", outcome.Result);
            Assert.Null(outcome.ErrorCode);
        }

        /// <summary>
        /// Подставная синхронизация, роняющая установку модпака: до синхронизации самой
        /// игры дело не доходит, раннер уходит из операции сразу после отказа модов.
        /// </summary>
        private sealed class FailingModsSync : ISyncService {
            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct)
                => throw new InvalidOperationException("сеть недоступна");

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => throw new NotSupportedException();

            public Task<DiffPlan> PlanAsync(
                Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => throw new NotSupportedException();

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct)
                => throw new NotSupportedException();
        }
    }
}
