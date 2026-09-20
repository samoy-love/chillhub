// <copyright file="SyncPlannerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Объединение одинаковых расчётов плана.
    /// <para>
    /// Обход папки игры — это чтение диска и пересчёт хешей, на сборке в несколько
    /// гигабайт минуты. Открытие страницы игры спрашивает один и тот же ответ дважды:
    /// проверкой статуса и подсказкой «Нужно: N». Здесь проверяется, что второй
    /// спрашивающий ждёт первого, а не заводит свой обход, и что расчёт не остаётся
    /// в списке идущих после конца — иначе следующий вопрос получил бы ответ
    /// пятиминутной давности.
    /// </para>
    /// </summary>
    public class SyncPlannerTests {
        private const string Root = @"C:\games\gid";
        private const string Content = "https://example.test/content";

        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        /// <summary>Три одинаковых вопроса — один обход папки и один и тот же ответ на всех.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ОдинаковыеЗапросыДелятОдинОбходПапки() {
            var sync = new CountingSync();
            var manifest = ManifestFor("1.0.1");

            var first = SyncPlanner.SharedPlanOffUiThreadAsync(sync, manifest, Root, Content, CancellationToken.None);
            await sync.Started.WaitAsync(Limit);
            var second = SyncPlanner.SharedPlanOffUiThreadAsync(sync, manifest, Root, Content, CancellationToken.None);
            var third = SyncPlanner.SharedPlanOffUiThreadAsync(sync, manifest, Root, Content, CancellationToken.None);

            sync.Release();
            var plans = await Task.WhenAll(first, second, third).WaitAsync(Limit);

            Assert.Equal(1, sync.Calls);
            Assert.Same(plans[0], plans[1]);
            Assert.Same(plans[1], plans[2]);
        }

        /// <summary>
        /// Разные версии — разные ответы: объединять их значило бы показать подсказку
        /// о размере от чужой сборки.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task РазныеВерсииНеОбъединяются() {
            var sync = new CountingSync();

            var first = SyncPlanner.SharedPlanOffUiThreadAsync(sync, ManifestFor("1.0.1"), Root, Content, CancellationToken.None);
            await sync.Started.WaitAsync(Limit);
            var second = SyncPlanner.SharedPlanOffUiThreadAsync(sync, ManifestFor("1.0.2"), Root, Content, CancellationToken.None);

            sync.Release();
            await Task.WhenAll(first, second).WaitAsync(Limit);

            Assert.Equal(2, sync.Calls);
        }

        /// <summary>
        /// Ушедший по отмене не уносит расчёт, который ещё нужен остальным: иначе
        /// перещёлкивание списка игр обрывало бы проверку статуса на полпути.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task УходОдногоЖдущегоНеОтменяетРасчётОстальным() {
            var sync = new CountingSync();
            var manifest = ManifestFor("1.0.1");
            using var cts = new CancellationTokenSource();

            var leaving = SyncPlanner.SharedPlanOffUiThreadAsync(sync, manifest, Root, Content, cts.Token);
            await sync.Started.WaitAsync(Limit);
            var staying = SyncPlanner.SharedPlanOffUiThreadAsync(sync, manifest, Root, Content, CancellationToken.None);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

            sync.Release();
            var plan = await staying.WaitAsync(Limit);

            Assert.NotNull(plan);
            Assert.False(sync.Cancelled, "обход папки прервали, хотя его ещё ждали");
        }

        /// <summary>
        /// Ушёл последний — обход прекращается: считать то, за чем никто не придёт, значит
        /// молотить диском впустую.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task УходПоследнегоЖдущегоПрекращаетОбход() {
            var sync = new CountingSync();
            using var cts = new CancellationTokenSource();

            var only = SyncPlanner.SharedPlanOffUiThreadAsync(sync, ManifestFor("1.0.1"), Root, Content, cts.Token);
            await sync.Started.WaitAsync(Limit);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);

            await WaitUntil(() => sync.Cancelled, "обход папки не прервали, хотя ждать результат больше некому");
            sync.Release();
        }

        /// <summary>
        /// Законченный расчёт из списка идущих уходит: следующий вопрос обязан получить
        /// свежий обход папки, а не ответ, снятый до установки.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ЗаконченныйРасчётНеОтдаётсяСледующему() {
            var sync = new CountingSync();
            var manifest = ManifestFor("1.0.1");

            sync.Release();
            await SyncPlanner.SharedPlanOffUiThreadAsync(sync, manifest, Root, Content, CancellationToken.None).WaitAsync(Limit);
            await SyncPlanner.SharedPlanOffUiThreadAsync(sync, manifest, Root, Content, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, sync.Calls);
        }

        private static Manifest ManifestFor(string version)
            => new Manifest { GameId = "gid", Version = version };

        private static async Task WaitUntil(Func<bool> condition, string message) {
            var deadline = DateTime.UtcNow + Limit;
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            Assert.Fail(message);
        }

        /// <summary>
        /// Служба, которая считает обходы и держит каждый до отмашки: без задержки все
        /// расчёты успевали закончиться до того, как подойдёт второй спрашивающий, и
        /// объединять было бы нечего.
        /// </summary>
        private sealed class CountingSync : ISyncService {
            private readonly SemaphoreSlim gate = new SemaphoreSlim(0);
            private readonly TaskCompletionSource started =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            private int calls;

            /// <summary>Gets сколько раз считали план.</summary>
            internal int Calls => Volatile.Read(ref this.calls);

            /// <summary>Gets признак «обход прервали по токену».</summary>
            internal bool Cancelled { get; private set; }

            /// <summary>Gets задачу, завершающуюся с началом первого обхода.</summary>
            internal Task Started => this.started.Task;

            /// <summary>Отпускает задержанные обходы.</summary>
            internal void Release() => this.gate.Release(16);

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct)
                => Task.FromResult(new Manifest());

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => this.PlanAsync(manifest, localRoot, contentBaseUrl, new PlanOptions(), ct);

            public async Task<DiffPlan> PlanAsync(
                Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct) {
                Interlocked.Increment(ref this.calls);
                this.started.TrySetResult();
                try {
                    await this.gate.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    this.Cancelled = true;
                    throw;
                }

                return new DiffPlan { GameId = manifest.GameId, Version = manifest.Version };
            }

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct)
                => Task.CompletedTask;
        }
    }
}
