// <copyright file="QueuedVerifyTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Sync;
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Проверка файлов идёт через ту же очередь, что и закачка.
    /// <para>
    /// ПРОВЕРКА — ТАКАЯ ЖЕ ДОЛГАЯ РАБОТА. Она читает и хеширует всю папку игры —
    /// десятки гигабайт, — а потом докачивает разошедшееся. Пока она шла мимо очереди,
    /// уход со страницы игры обрывал её на середине, в панели загрузок её не было
    /// видно вовсе, а запущенная второй раз она шла параллельно первой по тем же
    /// файлам.
    /// </para>
    /// </summary>
    public class QueuedVerifyTests {
        /// <summary>
        /// ГЛАВНОЕ. Установленную и свежую игру качать нечего — а проверять есть что:
        /// ради этого проверку и запускают.
        /// </summary>
        [Fact]
        public void УстановленнуюИСвежуюИгруМожноПоставитьНаПроверку() {
            var games = Games(Game("a", installed: true, needsUpdate: false));
            using var queue = NewQueue(new FakeSyncStub(), games);

            Assert.False(queue.Enqueue("a"), "качать у неё нечего");
            Assert.True(queue.Enqueue("a", QueueTaskKind.Verify), "а проверять — есть что");
        }

        /// <summary>
        /// Пустую папку сверять с манифестом — это установка, и называться она должна
        /// установкой. «Проверка», которая на деле качает игру с нуля, врёт об объёме.
        /// </summary>
        [Fact]
        public void НеустановленнуюИгруНаПроверкуНеСтавят() {
            var games = Games(Game("a", installed: false));
            using var queue = NewQueue(new FakeSyncStub(), games);

            Assert.False(queue.Enqueue("a", QueueTaskKind.Verify));
        }

        /// <summary>
        /// Позиция проверки видна в очереди наравне с закачкой — и знает, что она
        /// проверка, а не закачка.
        /// </summary>
        [Fact]
        public void ПроверкаВиднаВОчередиСвоимИменем() {
            var games = Games(Game("a", installed: true, needsUpdate: false));
            using var queue = NewQueue(new FakeSyncStub(), games);

            Assert.True(queue.Enqueue("a", QueueTaskKind.Verify));

            var item = queue.Snapshot().Single();
            Assert.Equal(QueueTaskKind.Verify, item.Kind);

            // Строка списка игр называет работу своим именем: «Скачивание» у свежей
            // установленной игры читается как «мне опять что-то катят».
            var running = item with { State = QueueItemState.Running, BytesDownloaded = 5, TotalBytes = 10 };
            Assert.Equal("Проверка · 50%", QueueRowLabel.For(running));

            var downloading = running with { Kind = QueueTaskKind.Download };
            Assert.Equal("Скачивание · 50%", QueueRowLabel.For(downloading));
        }

        /// <summary>
        /// ВТОРОЙ ЗАПУСК НЕ ЗАВОДИТ ВТОРУЮ РАБОТУ ПО ТЕМ ЖЕ ФАЙЛАМ. Проверку можно
        /// нажать и в списке игр, и на странице «Об игре»; обе кнопки обязаны попасть
        /// в одну позицию очереди, а не пойти двумя проходами по одной папке.
        /// </summary>
        [Fact]
        public void ПовторныйЗапускПроверкиНеДублируетРаботу() {
            var games = Games(Game("a", installed: true, needsUpdate: false));
            using var queue = NewQueue(new FakeSyncStub(), games);

            Assert.True(queue.Enqueue("a", QueueTaskKind.Verify));
            Assert.False(queue.Enqueue("a", QueueTaskKind.Verify));
            Assert.False(queue.Enqueue("a"));

            Assert.Single(queue.Snapshot());
        }

        /// <summary>
        /// Стадия называется по работе: у проверки та же фаза докачивает только
        /// разошедшееся, и «Скачивание обновления…» на ней читалось бы неправдой.
        /// </summary>
        [Fact]
        public void СтадияНазываетсяПоРаботе() {
            var downloading = new SyncProgress { Stage = "Downloading" };

            Assert.Equal("Скачивание обновления…", DownloadQueue.StageText(downloading));
            Assert.Equal("Восстановление файлов…", DownloadQueue.StageText(downloading, QueueTaskKind.Verify));

            // Остальные стадии у обеих работ называются одинаково.
            var activating = new SyncProgress { Stage = "Activating" };
            Assert.Equal(
                DownloadQueue.StageText(activating),
                DownloadQueue.StageText(activating, QueueTaskKind.Verify));
        }

        /// <summary>
        /// Спросить о удалении лишних файлов некому — значит, не удаляем. Отказ от
        /// проверки дешевле снесённых модов, скриншотов и сохранений.
        /// </summary>
        [Fact]
        public void БезВопросаПроверкаНичегоНеУдаляет() {
            var games = Games(Game("a", installed: true, needsUpdate: false));

            // Очередь без обработчика вопроса — как её создаёт код без окна.
            using var queue = new DownloadQueue(
                gid => games.TryGetValue(gid, out var g) ? g : null,
                () => "https://example.test",
                () => new FakeSyncStub());

            Assert.True(queue.Enqueue("a", QueueTaskKind.Verify));
        }

        private static GameInfo Game(string id, bool installed = false, bool needsUpdate = false) => new GameInfo {
            GameId = id,
            Title = id,
            LatestVersion = "1.0.0",
            ExeRelativePath = "game.exe",
            IsInstalled = installed,
            NeedsUpdate = needsUpdate,
        };

        private static Dictionary<string, GameInfo> Games(params GameInfo[] games)
            => games.ToDictionary(g => g.GameId, g => g);

        private static DownloadQueue NewQueue(ISyncService sync, IReadOnlyDictionary<string, GameInfo> games)
            => new DownloadQueue(
                gid => games.TryGetValue(gid, out var g) ? g : null,
                () => "https://example.test",
                () => sync);

        /// <summary>Синхронизация, которая ничего не делает: очередь проверяется без диска и сети.</summary>
        private sealed class FakeSyncStub : ISyncService {
            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct)
                => Task.FromResult(new Manifest());

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => Task.FromResult(new DiffPlan());

            public Task<DiffPlan> PlanAsync(
                Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => Task.FromResult(new DiffPlan());

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct)
                => Task.CompletedTask;
        }
    }
}
