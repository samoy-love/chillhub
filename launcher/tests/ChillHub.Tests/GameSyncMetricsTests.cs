// <copyright file="GameSyncMetricsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Game;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Отчёт об установке и обновлении в статистику.
    /// <para>
    /// Проверяется ровно то, чего не было: <see cref="GameSyncRunner"/> проводил
    /// установки и обновления и не сообщал о них ни слова, поэтому админка показывала
    /// нули на живом лаунчере. Метрику отправляет именно раннер, а не страница игры и
    /// не очередь загрузок, — обе ведут сюда, и починка в одном из двух мест дала бы
    /// половину правды.
    /// </para>
    /// </summary>
    public class GameSyncMetricsTests {
        /// <summary>Успешная установка уходит в статистику установкой с результатом ok.</summary>
        [Fact]
        public async Task УспешнаяУстановкаСообщаетсяКакУстановка() {
            var sent = new List<SyncOutcome>();
            var runner = NewRunner(new FakeSync(), sent);

            await runner.RunAsync(Request(SyncKind.Install), CancellationToken.None);

            var o = Assert.Single(sent);
            Assert.Equal(SyncKind.Install, o.Kind);
            Assert.Equal("ok", o.Result);
            Assert.Equal("game", o.GameId);
            Assert.Equal("1.2.0", o.Version);
            Assert.Null(o.ErrorCode);
        }

        /// <summary>
        /// Обновление — не установка. Пока вид операции не передавался, «Обновлений» в
        /// админке было столько же, сколько «Установок», то есть ноль и ноль.
        /// </summary>
        [Fact]
        public async Task ОбновлениеСообщаетсяОбновлением() {
            var sent = new List<SyncOutcome>();
            var runner = NewRunner(new FakeSync(), sent);

            await runner.RunAsync(Request(SyncKind.Update), CancellationToken.None);

            Assert.Equal(SyncKind.Update, Assert.Single(sent).Kind);
        }

        /// <summary>
        /// «Проверить файлы» у установленной и свежей игры — проверка целостности, а не
        /// установка: иначе каждая сверка с манифестом дописывала бы в отчёт установку,
        /// которой не было.
        /// </summary>
        [Fact]
        public async Task ПроверкаФайловНеСчитаетсяУстановкой() {
            var sent = new List<SyncOutcome>();
            var runner = NewRunner(new FakeSync(), sent);

            await runner.RunAsync(Request(SyncKind.Repair), CancellationToken.None);

            Assert.Equal(SyncKind.Repair, Assert.Single(sent).Kind);
        }

        /// <summary>
        /// Отмена — не провал: у неё свой результат, и он существует затем, чтобы
        /// брошенная закачка не попадала ни в долю неудач, ни в среднее время операции.
        /// </summary>
        [Fact]
        public async Task ОтменаСообщаетсяОтменой() {
            var sent = new List<SyncOutcome>();
            var sync = new FakeSync { OnExecute = () => throw new OperationCanceledException() };
            var runner = NewRunner(sync, sent);

            await runner.RunAsync(Request(SyncKind.Install), CancellationToken.None);

            var o = Assert.Single(sent);
            Assert.Equal("cancel", o.Result);
            Assert.Null(o.ErrorCode);
        }

        /// <summary>
        /// Сбой записи получает свой код. Код классифицирует проблему и только её: текст
        /// исключения содержит пути и имена файлов пользователя, а сводка публична.
        /// </summary>
        [Fact]
        public async Task СбойЗаписиСообщаетсяКодомОшибки() {
            var sent = new List<SyncOutcome>();
            var sync = new FakeSync { OnExecute = () => throw new IOException("диск переполнен") };
            var runner = NewRunner(sync, sent);

            await runner.RunAsync(Request(SyncKind.Install), CancellationToken.None);

            var o = Assert.Single(sent);
            Assert.Equal("fail", o.Result);
            Assert.Equal("sync_io", o.ErrorCode);
        }

        /// <summary>Отклонённый манифест отличается от сбоя записи — по коду это видно.</summary>
        [Fact]
        public async Task ОтклонённыйМанифестСообщаетсяСвоимКодом() {
            var sent = new List<SyncOutcome>();
            var sync = new FakeSync { OnManifest = () => throw new ManifestValidationException("опасный путь") };
            var runner = NewRunner(sync, sent);

            await runner.RunAsync(Request(SyncKind.Install), CancellationToken.None);

            Assert.Equal("manifest_invalid", Assert.Single(sent).ErrorCode);
        }

        /// <summary>
        /// Нехватка места — та самая жалоба «ничего не качается». Отдельный код нужен,
        /// чтобы её было видно в сводке, а не только на чужом скриншоте.
        /// </summary>
        [Fact]
        public async Task НехваткаМестаСообщаетсяОтдельнымКодом() {
            var sent = new List<SyncOutcome>();
            var sync = new FakeSync { Plan = PlanWith(totalBytes: 1000) };
            var runner = NewRunner(sync, sent);
            runner.FreeSpaceFor = _ => 10;

            await runner.RunAsync(Request(SyncKind.Install), CancellationToken.None);

            var o = Assert.Single(sent);
            Assert.Equal("fail", o.Result);
            Assert.Equal("no_disk_space", o.ErrorCode);
        }

        /// <summary>
        /// Запущенная игра — не сорвавшаяся операция: лаунчер даже не ходил на сервер.
        /// Считать её неудачей значило бы завысить долю провалов на ровном месте.
        /// </summary>
        [Fact]
        public async Task ЗапущеннаяИграНеПорождаетМетрику() {
            var sent = new List<SyncOutcome>();
            var runner = NewRunner(new FakeSync(), sent);

            var previous = GameDiskInfo.ProcessCountByName;
            GameDiskInfo.ProcessCountByName = _ => 1;
            try {
                await runner.RunAsync(Request(SyncKind.Install, exeRelativePath: "game.exe"), CancellationToken.None);
            }
            finally {
                GameDiskInfo.ProcessCountByName = previous;
            }

            Assert.Empty(sent);
        }

        /// <summary>
        /// Полный вес сборки и число файлов уходят вместе с результатом. Ради этой пары
        /// лаунчер и существует: «скачано 40 МБ» само по себе не значит ничего, смысл
        /// появляется только рядом с «вместо 12 ГБ».
        /// </summary>
        [Fact]
        public async Task ОбъёмЗакачкиУходитВместеСПолнымВесомСборки() {
            var sent = new List<SyncOutcome>();
            var plan = new DiffPlan {
                TotalDownloadBytes = 500,
                TotalFilesToDownload = 5,
                TotalManifestBytes = 12000,
                TotalManifestFiles = 300,
                HashMismatches = 2,
            };
            var runner = NewRunner(new FakeSync { Plan = plan }, sent);

            await runner.RunAsync(Request(SyncKind.Update), CancellationToken.None);

            var o = Assert.Single(sent);
            Assert.Equal(500, o.Bytes);
            Assert.Equal(5, o.FilesDownloaded);
            Assert.Equal(12000, o.FullBytes);
            Assert.Equal(300, o.FilesTotal);
            Assert.Equal(2, o.HashMismatches);
        }

        /// <summary>
        /// Сбой до построения плана всё равно сообщается: объём неизвестен, но сам факт
        /// неудачи важнее любых чисел о ней.
        /// </summary>
        [Fact]
        public async Task СбойДоПланаСообщаетсяБезОбъёма() {
            var sent = new List<SyncOutcome>();
            var sync = new FakeSync { OnManifest = () => throw new InvalidOperationException("сеть") };
            var runner = NewRunner(sync, sent);

            await runner.RunAsync(Request(SyncKind.Install), CancellationToken.None);

            var o = Assert.Single(sent);
            Assert.Equal("fail", o.Result);
            Assert.Equal(0, o.Bytes);
            Assert.Equal(0, o.FullBytes);
        }

        private static GameSyncRequest Request(SyncKind kind, string? exeRelativePath = null)
            => new GameSyncRequest(
                "game",
                "1.2.0",
                "https://example.test",
                Path.Combine(Path.GetTempPath(), "chillhub-metrics-test"),
                exeRelativePath,
                ConfirmDeletions: false,
                Kind: kind);

        private static DiffPlan PlanWith(long totalBytes)
            => new DiffPlan { TotalDownloadBytes = totalBytes, ToDelete = new List<string>() };

        private static GameSyncRunner NewRunner(FakeSync sync, List<SyncOutcome> sent) {
            var runner = new GameSyncRunner(sync, new GameSyncUi());
            runner.Maintenance = () => new MaintenanceStateView(false, false, string.Empty);
            runner.FreeSpaceFor = _ => long.MaxValue;
            runner.WriteLocalVersion = (_, _) => { };
            runner.ReportOutcome = sent.Add;
            return runner;
        }

        /// <summary>
        /// Подставная служба синхронизации: отдаёт заданный план и позволяет уронить
        /// загрузку манифеста или саму закачку.
        /// </summary>
        private sealed class FakeSync : ISyncService {
            internal DiffPlan Plan { get; set; } = new DiffPlan();

            internal Action? OnManifest { get; set; }

            internal Action? OnExecute { get; set; }

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
                this.OnManifest?.Invoke();
                return Task.FromResult(new Manifest());
            }

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => Task.FromResult(this.Plan);

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => Task.FromResult(this.Plan);

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
                this.OnExecute?.Invoke();
                return Task.CompletedTask;
            }
        }
    }
}
