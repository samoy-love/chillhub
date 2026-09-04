// <copyright file="DownloadQueueTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Очередь загрузок (фаза 1). Ни одного теста для этого класса не было ни в одном
    /// из трёх раундов правок, хотя тот же класс подряд ловил гонки на State/Cts между
    /// Remove() и Finish() — эти тесты фиксируют то самое поведение, которое чинили,
    /// чтобы регресс не мог снова проскочить незамеченным.
    /// </summary>
    public class DownloadQueueTests {
        private static GameInfo Game(string id, bool installed = false, bool needsUpdate = false) => new GameInfo {
            GameId = id,
            Title = id,
            LatestVersion = "1.0.0",
            ExeRelativePath = "game.exe",
            IsInstalled = installed,
            NeedsUpdate = needsUpdate,
        };

        private static DownloadQueue NewQueue(FakeSync sync, IReadOnlyDictionary<string, GameInfo> games)
            => new DownloadQueue(gid => games.TryGetValue(gid, out var g) ? g : null, () => "https://example.test", () => sync);

        /// <summary>Уже установленную и не требующую обновления игру ставить в очередь нечего.</summary>
        [Fact]
        public void EnqueueНеСтавитУжеУстановленнуюИгруБезОбновлений() {
            var sync = new FakeSync();
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a", installed: true, needsUpdate: false) });

            Assert.False(queue.Enqueue("a"));
        }

        /// <summary>
        /// ИГРА БЕЗ СБОРКИ НА СЕРВЕРЕ В ОЧЕРЕДЬ НЕ ПОПАДАЕТ. Она живёт только копией из
        /// Steam: очередь принимала позицию, синхронизация шла за манифестом, которого
        /// не существует, и через секунду карточка падала с отказом — на глазах у
        /// игрока и без единого объяснения, что качать было нечего.
        /// </summary>
        [Fact]
        public void EnqueueНеСтавитИгруБезСборкиНаСервере() {
            var sync = new FakeSync();
            var game = Game("steam-only");
            game.LatestVersion = string.Empty;
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["steam-only"] = game });

            Assert.False(queue.Enqueue("steam-only"));
            Assert.Empty(queue.Snapshot());
        }

        /// <summary>Неизвестная игра (пропала из списка) в очередь не попадает.</summary>
        [Fact]
        public void EnqueueНеСтавитНеизвестнуюИгру() {
            var sync = new FakeSync();
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo>());

            Assert.False(queue.Enqueue("ghost"));
        }

        /// <summary>Повторный Enqueue той же игры, пока первая позиция ещё стоит, второй раз не добавляет.</summary>
        [Fact]
        public void EnqueueНеДублируетУжеСтоящуюПозицию() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { ExecuteGate = new SemaphoreSlim(0) }; // держит воркер занятым
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a") });

            Assert.True(queue.Enqueue("a"));
            Assert.False(queue.Enqueue("a"));

            sync.ExecuteGate.Release(10); // отпустить воркер, чтобы Dispose не завис
        }

        /// <summary>
        /// Снятая из очереди позиция, ещё не начавшая качаться (Waiting), убирается сразу
        /// и присылает ровно одно событие ItemRemoved — без ItemCompleted следом.
        /// </summary>
        [Fact]
        public async Task RemoveWaitingПозициюУбираетСразуИШлётОдноСобытие() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { ExecuteGate = new SemaphoreSlim(0) }; // первая позиция держит воркер занятым
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a"), ["b"] = Game("b") });
            var removed = new ConcurrentBag<QueueItem>();
            var completed = new ConcurrentBag<QueueItem>();
            queue.ItemRemoved += i => removed.Add(i);
            queue.ItemCompleted += i => completed.Add(i);

            queue.Enqueue("a"); // воркер тут же берёт её и застревает на ExecuteGate
            await WaitUntil(() => sync.ExecuteStarted, "воркер не начал качать 'a'");
            queue.Enqueue("b"); // 'b' остаётся Waiting, пока 'a' держит воркер

            Assert.True(queue.Remove("b"));

            await WaitUntil(() => removed.Count == 1, "ItemRemoved для 'b' не пришёл");
            Assert.Equal("b", Assert.Single(removed).GameId);
            Assert.Empty(completed); // 'b' никогда не запускалась — ItemCompleted для неё быть не должно

            sync.ExecuteGate!.Release(10);
        }

        /// <summary>
        /// Снятая из очереди Running-позиция не убирается из списка немедленно — только
        /// когда воркер реально остановится по токену отмены и дойдёт до Finish(), и тогда
        /// шлётся ровно одно событие (ItemCompleted с состоянием Cancelled), а не два
        /// противоречащих (Remove()'s ItemRemoved и Finish()'s ItemCompleted для одной
        /// и той же позиции — race, которую чинили в раунде 2).
        /// </summary>
        [Fact]
        public async Task RemoveRunningПозициюЖдётFinishИШлётРовноОдноСобытие() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { RespectCancellation = true };
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a") });
            var removedCount = 0;
            var completedCount = 0;
            QueueItem? lastCompleted = null;
            queue.ItemRemoved += _ => Interlocked.Increment(ref removedCount);
            queue.ItemCompleted += i => { Interlocked.Increment(ref completedCount); lastCompleted = i; };

            queue.Enqueue("a");
            await WaitUntil(() => sync.ExecuteStarted, "воркер не начал качать 'a'");

            Assert.True(queue.Remove("a"));

            await WaitUntil(() => completedCount == 1, "Finish() для отменённой 'a' не случился");
            Assert.Equal(0, removedCount);
            Assert.Equal(1, completedCount);
            Assert.Equal(QueueItemState.Cancelled, lastCompleted!.State);
        }

        /// <summary>
        /// НАЖАТИЕ «ОТМЕНА» ВИДНО СРАЗУ. Движок встаёт не мгновенно, и до правки карточка
        /// всё это время показывала прежний процент, прежнюю скорость и прежнее
        /// «Скачивание»: нажатие выглядело как не сработавшее, и его повторяли.
        /// </summary>
        [Fact]
        public async Task RemoveRunningСразуСообщаетОбОстановке() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { RespectCancellation = true, StopGate = new SemaphoreSlim(0) };
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a") });
            var progress = new ConcurrentBag<QueueItem>();
            var added = new ConcurrentBag<QueueItem>();
            queue.ItemProgress += i => progress.Add(i);
            queue.ItemAdded += i => added.Add(i);

            queue.Enqueue("a");
            await WaitUntil(() => sync.ExecuteStarted, "воркер не начал качать 'a'");
            Assert.Equal("a", Assert.Single(added).GameId); // без ItemAdded карточка в панели загрузок не появится

            Assert.True(queue.Remove("a"));

            var stopping = Assert.Single(queue.Snapshot());
            Assert.True(stopping.Cancelling, "остановленная позиция обязана отличаться от идущей закачки");
            Assert.Equal("Останавливаем…", stopping.StatusText);
            Assert.Contains(progress, i => i.Cancelling);

            sync.StopGate!.Release(10);
        }

        /// <summary>
        /// ОСТАНОВЛЕННУЮ ИГРУ МОЖНО ЗАПУСТИТЬ ЗАНОВО, НЕ ДОЖИДАЯСЬ ОСТАНОВКИ. Позиция
        /// остаётся в очереди, пока движок встаёт, и Enqueue отвечал на «Скачать» отказом
        /// «уже в очереди»: запустить игру снова было нельзя, пока прежняя попытка не
        /// домотает — а домотать она могла и через минуту.
        /// </summary>
        [Fact]
        public async Task EnqueueПослеОтменыВозвращаетПозициюВОчередь() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { RespectCancellation = true, StopGate = new SemaphoreSlim(0) };
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a") });
            var completed = new ConcurrentBag<QueueItem>();
            queue.ItemCompleted += i => completed.Add(i);

            queue.Enqueue("a");
            await WaitUntil(() => sync.ExecuteStarted, "воркер не начал качать 'a'");
            Assert.True(queue.Remove("a"));
            await WaitUntil(() => sync.CancelObserved, "движок не увидел отмену");

            // Игрок жмёт «Скачать» ещё раз, пока прежняя попытка ещё не встала
            Assert.True(queue.Enqueue("a"), "повторный запуск остановленной игры обязан приниматься");

            sync.StopGate!.Release(10); // прежняя попытка наконец домотала

            await WaitUntil(
                () => queue.Snapshot().Any(i => i.GameId == "a" && i.State == QueueItemState.Running && !i.Cancelling),
                "позиция должна была начаться заново, а не исчезнуть из очереди");
            Assert.Empty(completed); // позиция никуда не уходила — «снята из очереди» присылать не за что
        }

        /// <summary>Соседние ожидающие позиции меняются местами и присылают новый порядок целиком.</summary>
        [Fact]
        public void MoveUpМеняетМестамиССоседнейОжидающейПозицией() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { ExecuteGate = new SemaphoreSlim(0) }; // 'a' держит воркер занятым
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a"), ["b"] = Game("b"), ["c"] = Game("c") });
            IReadOnlyList<QueueItem>? lastOrder = null;
            queue.Reordered += order => lastOrder = order;

            queue.Enqueue("a"); // тут же становится Running
            queue.Enqueue("b"); // Waiting
            queue.Enqueue("c"); // Waiting

            Assert.True(queue.MoveUp("c"));

            Assert.NotNull(lastOrder);
            Assert.Equal(new[] { "a", "c", "b" }, lastOrder!.Select(i => i.GameId));

            sync.ExecuteGate!.Release(10);
        }

        /// <summary>Саму качающуюся позицию двигать нельзя: её место определяет то, что она уже качается.</summary>
        [Fact]
        public async Task КачающуюсяПозициюДвигатьНельзя() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { ExecuteGate = new SemaphoreSlim(0) };
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a"), ["b"] = Game("b") });

            queue.Enqueue("a"); // тут же становится Running
            await WaitUntil(() => sync.ExecuteStarted, "воркер не начал качать 'a'");
            queue.Enqueue("b"); // Waiting

            Assert.False(queue.MoveUp("a"));
            Assert.False(queue.MoveDown("a"));

            // Ниже качающейся уходить некуда: она и так впереди
            Assert.False(queue.MoveDown("b"));

            sync.ExecuteGate!.Release(10);
        }

        /// <summary>
        /// Шаг вверх через качающуюся позицию заменяет текущую закачку: ожидающая встаёт
        /// первой и стартует, а прерванная возвращается в очередь ожидающей — не снимается.
        /// <para>
        /// Прежняя перестановка ходила только среди ожидающих, поэтому при раскладе
        /// «одна качается, одна ждёт» обе стрелки не делали ничего вообще.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ШагВверхЧерезКачающуюсяЗаменяетТекущуюЗакачку() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { RespectCancellation = true };
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a"), ["b"] = Game("b") });

            queue.Enqueue("a");
            await WaitUntil(() => sync.ExecuteStarted, "воркер не начал качать 'a'");
            queue.Enqueue("b");

            Assert.True(queue.MoveUp("b"), "ожидающую позицию обязано пускать вперёд качающейся");

            // 'b' встала первой и пошла качаться, 'a' вернулась в очередь — но НЕ исчезла
            await WaitUntil(
                () => queue.Snapshot().FirstOrDefault(i => i.GameId == "b")?.State == QueueItemState.Running,
                "'b' должна была начать качаться после замены");

            var snapshot = queue.Snapshot();
            Assert.Equal(new[] { "b", "a" }, snapshot.Select(i => i.GameId));
            Assert.Equal(QueueItemState.Waiting, snapshot.Single(i => i.GameId == "a").State);
        }

        /// <summary>
        /// Признаки «можно сдвинуть» описывают реальную возможность: верхняя позиция не
        /// поднимается, нижняя не опускается. Кнопка, которая нажимается и молчит, читается
        /// как сломанная — а именно так вели себя обе стрелки до правки.
        /// </summary>
        [Fact]
        public async Task ПризнакиСдвигаСоответствуютРеальнойВозможности() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { ExecuteGate = new SemaphoreSlim(0) };
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a"), ["b"] = Game("b"), ["c"] = Game("c") });

            queue.Enqueue("a"); // Running
            await WaitUntil(() => sync.ExecuteStarted, "воркер не начал качать 'a'");
            queue.Enqueue("b"); // Waiting
            queue.Enqueue("c"); // Waiting

            var byId = queue.Snapshot().ToDictionary(i => i.GameId);

            // Качающаяся не двигается вообще
            Assert.False(byId["a"].CanMoveUp);
            Assert.False(byId["a"].CanMoveDown);

            // 'b' можно и вверх (заменить текущую), и вниз (под 'c')
            Assert.True(byId["b"].CanMoveUp);
            Assert.True(byId["b"].CanMoveDown);

            // 'c' — последняя: вниз некуда
            Assert.True(byId["c"].CanMoveUp);
            Assert.False(byId["c"].CanMoveDown);

            sync.ExecuteGate!.Release(10);
        }

        /// <summary>Snapshot() отдаёт то, что реально стоит в очереди, а не устаревший кеш.</summary>
        [Fact]
        public void SnapshotОтражаетТекущееСодержимое() {
            using var pathScope = new GamesPathScope();
            var sync = new FakeSync { ExecuteGate = new SemaphoreSlim(0) };
            using var queue = NewQueue(sync, new Dictionary<string, GameInfo> { ["a"] = Game("a") });

            Assert.Empty(queue.Snapshot());
            queue.Enqueue("a");
            Assert.Single(queue.Snapshot());

            sync.ExecuteGate!.Release(10);
        }

        private static async Task WaitUntil(Func<bool> condition, string failureMessage, int timeoutMs = 5000) {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail(failureMessage);
                }

                await Task.Delay(10);
            }
        }

        /// <summary>
        /// Подставная служба синхронизации, управляемая тестом: ExecuteAsync либо блокируется
        /// на ExecuteGate (имитирует «качаем прямо сейчас»), либо, если RespectCancellation,
        /// ждёт токен отмены и сама решает считать это успехом — как настоящий
        /// GameSyncRunner/SimpleSyncService делает при отмене (не выпускает исключение наружу).
        /// </summary>
        private sealed class FakeSync : ISyncService {
            internal SemaphoreSlim? ExecuteGate { get; set; }

            internal bool RespectCancellation { get; set; }

            /// <summary>
            /// Держит движок ПОСЛЕ того, как он увидел отмену: настоящая остановка не
            /// мгновенна (непрерываемый шаг, пробуждение диска), и без этой задержки
            /// проверить поведение очереди в это самое окно нечем.
            /// </summary>
            internal SemaphoreSlim? StopGate { get; set; }

            internal volatile bool ExecuteStarted;

            internal volatile bool CancelObserved;

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) => Task.FromResult(new Manifest());

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => Task.FromResult(new DiffPlan());

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => Task.FromResult(new DiffPlan());

            public async Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
                this.ExecuteStarted = true;
                if (this.RespectCancellation) {
                    try {
                        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) {
                        // Ушли по отмене — как и настоящий движок, не выпускаем исключение наружу.
                    }

                    this.CancelObserved = true;
                    if (this.StopGate != null) {
                        await this.StopGate.WaitAsync().ConfigureAwait(false);
                    }

                    return;
                }

                if (this.ExecuteGate != null) {
                    await this.ExecuteGate.WaitAsync(ct).ConfigureAwait(false);
                }
            }
        }
    }
}
