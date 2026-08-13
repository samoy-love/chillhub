// <copyright file="DownloadQueue.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    /// <summary>
    /// Фаза 1 очереди загрузок: всё в памяти, качает по одной игре за раз, воркер — один
    /// фоновый цикл на весь процесс лаунчера. Ни на диск, ни в общий лимитер скорости не
    /// смотрит — это фаза 2 (см. PLAN.md, трек C), и переезд на неё не должен требовать
    /// правок в коде, который держит только <see cref="IDownloadQueue"/>.
    /// <para>
    /// Установку/обновление одной игры не переизобретает — вызывает тот же
    /// <see cref="GameSyncRunner"/>, которым пользуется страница игры, так что очередь и
    /// одиночная кнопка «Установить»/«Обновить» ведут себя одинаково.
    /// </para>
    /// </summary>
    internal sealed class DownloadQueue : IDownloadQueue, IDisposable {
        private readonly object gate = new();
        private readonly List<Entry> items = new();
        private readonly Func<string, GameInfo?> gameLookup;
        private readonly Func<string> baseApiProvider;
        private readonly Func<ISyncService> syncServiceFactory;
        private readonly SemaphoreSlim workSignal = new(0);
        private readonly CancellationTokenSource lifetimeCts = new();
        private readonly Task worker;

        /// <summary>Initializes a new instance of the <see cref="DownloadQueue"/> class.</summary>
        /// <param name="gameLookup">
        /// Поиск описания игры по идентификатору — очередь принимает только <c>gameId</c>
        /// (см. интерфейс), а сборка запроса на установку нуждается в полном <see cref="GameInfo"/>.
        /// </param>
        /// <param name="baseApiProvider">Текущий базовый URL API (может меняться в настройках).</param>
        /// <param name="syncServiceFactory">
        /// Фабрика службы синхронизации файлов — новый экземпляр на каждую позицию, как это
        /// делает <c>GamePage</c>. По умолчанию — <see cref="SimpleSyncService"/>.
        /// </param>
        internal DownloadQueue(Func<string, GameInfo?> gameLookup, Func<string> baseApiProvider, Func<ISyncService>? syncServiceFactory = null) {
            this.gameLookup = gameLookup ?? throw new ArgumentNullException(nameof(gameLookup));
            this.baseApiProvider = baseApiProvider ?? throw new ArgumentNullException(nameof(baseApiProvider));
            this.syncServiceFactory = syncServiceFactory ?? (() => new SimpleSyncService());
            this.worker = Task.Run(() => this.RunWorkerAsync(this.lifetimeCts.Token));
        }

        /// <inheritdoc/>
        public event Action<QueueItem>? ItemAdded;

        /// <inheritdoc/>
        public event Action<QueueItem>? ItemProgress;

        /// <inheritdoc/>
        public event Action<QueueItem>? ItemCompleted;

        /// <inheritdoc/>
        public event Action<QueueItem>? ItemRemoved;

        /// <inheritdoc/>
        public IReadOnlyList<QueueItem> Snapshot() {
            lock (this.gate) {
                return this.items.Select(e => e.ToItem()).ToList();
            }
        }

        /// <inheritdoc/>
        public bool Enqueue(string gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return false;
            }

            var game = this.gameLookup(gameId);
            if (game == null) {
                return false;
            }

            // Уже установлена и совпадает с последней версией — ставить в очередь нечего.
            if (game.IsInstalled && !game.NeedsUpdate) {
                return false;
            }

            Entry entry;
            lock (this.gate) {
                if (this.items.Any(e => string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase))) {
                    return false;
                }

                entry = new Entry(gameId, string.IsNullOrWhiteSpace(game.Title) ? gameId : game.Title);
                this.items.Add(entry);
            }

            this.ItemAdded?.Invoke(entry.ToItem());
            this.workSignal.Release();
            return true;
        }

        /// <inheritdoc/>
        public bool Remove(string gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return false;
            }

            Entry? entry;
            var removedNow = false;
            lock (this.gate) {
                entry = this.items.FirstOrDefault(e => string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                if (entry == null) {
                    return false;
                }

                if (entry.State == QueueItemState.Running) {
                    // Не трогаем items/State здесь: Finish() (под тем же gate) снимет позицию и
                    // пришлёт ровно одно событие, когда ProcessAsync реально остановится по токену
                    // отмены — иначе Remove() и Finish() могли гонять State/items друг у друга из-под
                    // ног и породить два противоречащих события для одной и той же позиции.
                    entry.CancelRequested = true;
                    entry.Cts?.Cancel();
                }
                else {
                    this.items.Remove(entry);
                    entry.State = QueueItemState.Cancelled;
                    removedNow = true;
                }
            }

            if (removedNow) {
                this.ItemRemoved?.Invoke(entry.ToItem());
            }

            return true;
        }

        /// <summary>Останавливает фоновый воркер. Позиции, которые не успели стартовать, просто пропадают.</summary>
        public void Dispose() {
            this.lifetimeCts.Cancel();
            this.workSignal.Release();
            try {
                this.worker.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception) {
                // Воркер сам глотает свои исключения — сюда долетает разве что таймаут ожидания.
            }

            this.lifetimeCts.Dispose();
            this.workSignal.Dispose();
        }

        private async Task RunWorkerAsync(CancellationToken lifetime) {
            while (!lifetime.IsCancellationRequested) {
                try {
                    await this.workSignal.WaitAsync(lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    return;
                }

                Entry? next;
                lock (this.gate) {
                    next = this.items.FirstOrDefault(e => e.State == QueueItemState.Waiting);
                    if (next != null) {
                        // State/Cts flip atomically with picking the entry, under the same
                        // lock Remove() reads them through — otherwise Remove() could see a
                        // stale Waiting state (and remove+notify the UI) for an entry
                        // ProcessAsync was already about to run, or catch the entry between
                        // "Running" and "Cts assigned" and call Cancel() on a still-null Cts.
                        next.State = QueueItemState.Running;
                        next.Cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    }
                }

                if (next == null) {
                    continue;
                }

                await this.ProcessAsync(next).ConfigureAwait(false);

                // Могло остаться больше одной позиции, а сигнал был съеден одним Release() на Enqueue —
                // будим себя снова, чтобы не ждать нового Enqueue ради уже стоящих в очереди игр.
                lock (this.gate) {
                    if (this.items.Any(e => e.State == QueueItemState.Waiting)) {
                        this.workSignal.Release();
                    }
                }
            }
        }

        private async Task ProcessAsync(Entry entry) {
            var game = this.gameLookup(entry.GameId);
            if (game == null) {
                this.Finish(entry, QueueItemState.Failed, "Игра больше не найдена в списке.");
                return;
            }

            // State и Cts уже выставлены в RunWorkerAsync под gate — см. комментарий там.
            this.RaiseProgress(entry, "Ожидание в очереди завершено, начинаем…");

            var ui = new GameSyncUi {
                SetStatus = text => this.RaiseProgress(entry, text),
                ReportProgress = (p, _) => this.RaiseProgress(entry, entry.StatusText, p.BytesDownloaded, p.TotalBytes),
            };

            var runner = new GameSyncRunner(this.syncServiceFactory(), ui);
            var localRoot = GameLocalState.GameLocalRoot(entry.GameId);
            var request = new GameSyncRequest(
                entry.GameId,
                game.LatestVersion,
                this.baseApiProvider(),
                localRoot,
                game.ExeRelativePath,
                IsVersionSwitch: false,
                ConfirmDeletions: false);

            try {
                // entry.Cts всегда назначен в RunWorkerAsync до вызова ProcessAsync — см. gate там.
                await runner.RunAsync(request, entry.Cts!.Token).ConfigureAwait(false);
                this.Finish(entry, entry.CancelRequested ? QueueItemState.Cancelled : QueueItemState.Completed, entry.CancelRequested ? "Снята из очереди." : "Готово.");
            }
            catch (Exception ex) {
                // GameSyncRunner.RunAsync сам не выпускает исключения наружу — сюда попадём
                // только если что-то пошло не так уже в самой очереди (например, отмена).
                Logging.Logger.Error(ex, $"DownloadQueue.ProcessAsync gid={entry.GameId}");
                this.Finish(entry, QueueItemState.Failed, "Не удалось завершить операцию.");
            }
            finally {
                entry.Cts?.Dispose();
                entry.Cts = null;
            }
        }

        private void RaiseProgress(Entry entry, string status, long bytesDownloaded = -1, long totalBytes = -1) {
            entry.StatusText = status;
            if (bytesDownloaded >= 0) {
                entry.BytesDownloaded = bytesDownloaded;
            }

            if (totalBytes >= 0) {
                entry.TotalBytes = totalBytes;
            }

            this.ItemProgress?.Invoke(entry.ToItem());
        }

        private void Finish(Entry entry, QueueItemState state, string status) {
            // State/StatusText мутируются под тем же gate, что читает Remove() — без этого
            // Remove() мог застать позицию ещё Waiting/Running и сообщить об отмене только что
            // успешно завершённой закачки, пока Finish() параллельно ставил ей Completed.
            bool stillPresent;
            lock (this.gate) {
                stillPresent = this.items.Remove(entry);
                entry.State = state;
                entry.StatusText = status;
            }

            if (stillPresent) {
                this.ItemCompleted?.Invoke(entry.ToItem());
            }

            // !stillPresent значит Remove() уже убрал позицию и разослал ItemRemoved сам —
            // не дублируем и не переопределяем то, что UI уже увидел.
        }

        /// <summary>Внутреннее изменяемое состояние позиции — наружу отдаём только снимки <see cref="QueueItem"/>.</summary>
        private sealed class Entry {
            internal Entry(string gameId, string title) {
                this.GameId = gameId;
                this.Title = title;
            }

            internal string GameId { get; }

            internal string Title { get; }

            internal QueueItemState State { get; set; } = QueueItemState.Waiting;

            internal long BytesDownloaded { get; set; }

            internal long TotalBytes { get; set; }

            internal string StatusText { get; set; } = "Ждёт очереди…";

            internal bool CancelRequested { get; set; }

            internal CancellationTokenSource? Cts { get; set; }

            internal QueueItem ToItem() => new(this.GameId, this.Title, this.State, this.BytesDownloaded, this.TotalBytes, this.StatusText);
        }
    }
}
