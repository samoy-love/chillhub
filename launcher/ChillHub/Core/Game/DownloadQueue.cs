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
    /// смотрит — это следующая фаза, и переезд на неё не должен требовать
    /// правок в коде, который держит только <see cref="IDownloadQueue"/>.
    /// <para>
    /// Установку/обновление одной игры не переизобретает — вызывает тот же
    /// <see cref="GameSyncRunner"/>, которым пользуется страница игры, так что очередь и
    /// одиночная кнопка «Установить»/«Обновить» ведут себя одинаково.
    /// </para>
    /// </summary>
    internal sealed class DownloadQueue : IDownloadQueue, IDisposable {
        /// <summary>Чувствительность сглаживания скорости — как у прежнего счётчика на странице игры.</summary>
        private const double SpeedEmaAlpha = 0.2;

        private readonly object gate = new();
        private readonly List<Entry> items = new();
        private readonly Func<string, GameInfo?> gameLookup;
        private readonly Func<string> baseApiProvider;
        private readonly Func<ISyncService> syncServiceFactory;

        /// <summary>Часы счётчика скорости: миллисекунды, монотонно. Подменяются в тестах.</summary>
        private readonly Func<long> clock;

        /// <summary>Вопрос «да/нет» игроку: текст, заголовок. Задаётся с UI-потока.</summary>
        private readonly Func<string, string, bool> confirm;
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
        /// <param name="confirm">Вопрос «да/нет» игроку: текст, заголовок. Задаётся с UI-потока.</param>
        /// <param name="clock">
        /// Часы счётчика скорости в миллисекундах. По умолчанию — <see cref="Environment.TickCount64"/>;
        /// тест подставляет свои, чтобы задавать интервал между отчётами явно, а не выжидать его.
        /// </param>
        internal DownloadQueue(
            Func<string, GameInfo?> gameLookup,
            Func<string> baseApiProvider,
            Func<ISyncService>? syncServiceFactory = null,
            Func<string, string, bool>? confirm = null,
            Func<long>? clock = null) {
            this.gameLookup = gameLookup ?? throw new ArgumentNullException(nameof(gameLookup));
            this.baseApiProvider = baseApiProvider ?? throw new ArgumentNullException(nameof(baseApiProvider));
            this.syncServiceFactory = syncServiceFactory ?? (() => new SimpleSyncService());
            this.clock = clock ?? (() => Environment.TickCount64);

            // Без вопроса проверка молча сносила бы всё, чего нет в манифесте: моды,
            // скриншоты, сохранения в папке игры. Некому спросить — значит, не удаляем:
            // отказ от проверки дешевле удалённых чужих файлов.
            this.confirm = confirm ?? ((_, _) => false);
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
        public event Action<IReadOnlyList<QueueItem>>? Reordered;

        /// <inheritdoc/>
        public IReadOnlyList<QueueItem> Snapshot() {
            lock (this.gate) {
                return this.SnapshotLocked();
            }
        }

        /// <inheritdoc/>
        public bool Enqueue(string gameId, QueueTaskKind kind = QueueTaskKind.Download) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return false;
            }

            var game = this.gameLookup(gameId);
            if (game == null) {
                return false;
            }

            // Уже установлена и совпадает с последней версией — качать нечего. А вот
            // проверять есть что: ради неё проверку и запускают.
            if (kind == QueueTaskKind.Download && game.IsInstalled && !game.NeedsUpdate) {
                return false;
            }

            // Проверять нечего, пока игры нет на диске: сверять с манифестом пустую папку —
            // это установка, и называться она должна установкой.
            if (kind == QueueTaskKind.Verify && !game.IsInstalled) {
                return false;
            }

            // СБОРКИ НА СЕРВЕРЕ НЕТ — КАЧАТЬ НЕЧЕГО. Такая игра живёт только копией из
            // Steam, а очередь принимала её и шла за манифестом, которого не существует:
            // позиция появлялась в панели загрузок и через секунду падала с отказом.
            // Проверка здесь, а не в кнопке: в очередь кладут ещё и из меню списка игр.
            if (string.IsNullOrWhiteSpace(game.LatestVersion)) {
                return false;
            }

            Entry entry = null!;
            Entry? revived = null;
            IReadOnlyList<QueueItem> snapshot;
            lock (this.gate) {
                var existing = this.items.FirstOrDefault(e => string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                if (existing != null) {
                    // ОСТАНОВЛЕННАЯ ПОЗИЦИЯ ЕЩЁ СТОИТ В СПИСКЕ, И ЭТО НЕ ПОВОД ОТКАЗЫВАТЬ.
                    //
                    // Движок останавливается не мгновенно: шаг может быть непрерываемым
                    // (опрос процессов, пробуждение диска), и до конца ProcessAsync позиция
                    // остаётся в очереди качающейся. Всё это время «Скачать» по той самой
                    // игре, которую игрок сам только что остановил, отвечало «уже в
                    // очереди» и не делало ничего — запустить её заново было нельзя.
                    // Возвращаем позицию в очередь: воркер поднимет её сразу, как только
                    // прежняя попытка домотает. Второй параллельной закачки той же игры при
                    // этом не возникает — позиция одна и та же.
                    if (existing.State != QueueItemState.Running
                        || !existing.CancelRequested
                        || existing.Kind != kind) {
                        return false;
                    }

                    existing.CancelRequested = false;
                    existing.RequeueRequested = true;
                    existing.StatusText = "Останавливаем прежнюю попытку, потом начнём заново…";
                    revived = existing;
                    snapshot = this.SnapshotLocked();
                }
                else {
                    entry = new Entry(
                        gameId,
                        string.IsNullOrWhiteSpace(game.Title) ? gameId : game.Title,
                        game.IconUrl,
                        kind,
                        this.clock);
                    this.items.Add(entry);
                    snapshot = this.SnapshotLocked();
                }
            }

            if (revived != null) {
                // Будить воркер не надо: позиция ещё занимает его собой, а обратно в
                // очередь её поставит Settle() — он же и разбудит.
                this.ItemProgress?.Invoke(revived.ToItem());
                this.Reordered?.Invoke(snapshot);
                return true;
            }

            this.ItemAdded?.Invoke(entry.ToItem());

            // Появление соседа меняет «можно сдвинуть» и у тех, кто уже стоял в очереди:
            // у последней позиции появляется сосед снизу. Рассылаем весь порядок целиком.
            this.Reordered?.Invoke(snapshot);
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
            var stopping = false;
            IReadOnlyList<QueueItem> snapshot;
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
                    // Снятие важнее возврата в очередь: пользователь просил убрать, а не пропустить.
                    entry.RequeueRequested = false;
                    entry.CancelRequested = true;

                    // КАРТОЧКА ОБЯЗАНА ИЗМЕНИТЬСЯ В ТОТ ЖЕ МИГ. Отмена доходит до движка
                    // не мгновенно, и до правки строка всё это время показывала прежний
                    // процент, прежнюю скорость и прежнее «Скачивание» — нажатие выглядело
                    // как не сработавшее, и его повторяли ещё несколько раз.
                    entry.StatusText = "Останавливаем…";
                    entry.ResetSpeed();
                    stopping = true;
                    entry.Cts?.Cancel();
                }
                else {
                    this.items.Remove(entry);
                    entry.State = QueueItemState.Cancelled;
                    removedNow = true;
                }

                snapshot = this.SnapshotLocked();
            }

            if (removedNow) {
                this.ItemRemoved?.Invoke(entry.ToItem());
                this.Reordered?.Invoke(snapshot);
            }
            else if (stopping) {
                this.ItemProgress?.Invoke(entry.ToItem());
            }

            return true;
        }

        /// <inheritdoc/>
        public bool MoveUp(string gameId) => this.Swap(gameId, -1);

        /// <inheritdoc/>
        public bool MoveDown(string gameId) => this.Swap(gameId, 1);

        /// <summary>
        /// Двигает ожидающую позицию на шаг по очереди.
        /// <para>
        /// Соседом считается позиция в СПИСКЕ, а не «следующая ожидающая». Пока перестановка
        /// шла только среди ожидающих, при обычном раскладе «одна качается, одна ждёт»
        /// ожидающая была единственной, и обе стрелки не делали ровно ничего, оставаясь при
        /// этом нарисованными и нажимаемыми.
        /// </para>
        /// <para>
        /// Шаг вверх через КАЧАЮЩУЮСЯ позицию — это «начать эту вместо текущей»: текущая
        /// закачка прерывается и возвращается в очередь следом (см. <see cref="Settle"/>).
        /// Прогресс прерванной не теряется — движок докачивает по Range из уцелевших
        /// .part-файлов.
        /// </para>
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="delta">-1 — раньше в очереди, +1 — позже.</param>
        /// <returns>True, если позиция действительно сдвинулась.</returns>
        private bool Swap(string gameId, int delta) {
            IReadOnlyList<QueueItem> snapshot;
            Entry? interrupted = null;
            lock (this.gate) {
                var idx = this.items.FindIndex(e => string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 || this.items[idx].State != QueueItemState.Waiting) {
                    // Двигать можно только ожидающую позицию: у качающейся место определяется
                    // тем, что она уже качается, а не порядком в списке.
                    return false;
                }

                var target = idx + delta;
                if (target < 0 || target >= this.items.Count) {
                    return false;
                }

                var neighbour = this.items[target];
                if (neighbour.State == QueueItemState.Running) {
                    if (delta > 0) {
                        // Уйти НИЖЕ качающейся нельзя: она и так впереди — двигать нечего.
                        return false;
                    }

                    interrupted = neighbour;
                }
                else if (neighbour.State != QueueItemState.Waiting) {
                    return false;
                }

                (this.items[idx], this.items[target]) = (this.items[target], this.items[idx]);

                if (interrupted != null) {
                    // Помечаем ДО отмены: ProcessAsync прочтёт флаг, когда RunAsync вернётся
                    // по токену, и вернёт позицию в очередь вместо снятия.
                    interrupted.RequeueRequested = true;
                    interrupted.Cts?.Cancel();
                }

                snapshot = this.SnapshotLocked();
            }

            this.Reordered?.Invoke(snapshot);
            return true;
        }

        /// <summary>
        /// Снимок очереди. Вызывать под <see cref="gate"/>: считает признаки «можно сдвинуть»,
        /// а они зависят от соседей, то есть от всего списка целиком.
        /// </summary>
        /// <returns>Снимок всех позиций в текущем порядке.</returns>
        private IReadOnlyList<QueueItem> SnapshotLocked() {
            var result = new List<QueueItem>(this.items.Count);
            for (var i = 0; i < this.items.Count; i++) {
                var e = this.items[i];
                var waiting = e.State == QueueItemState.Waiting;

                // Вверх — если есть кто-то выше (в т.ч. качающаяся: это «начать вместо неё»).
                var canUp = waiting && i > 0;

                // Вниз — только если ниже стоит такая же ожидающая позиция.
                var canDown = waiting && i + 1 < this.items.Count && this.items[i + 1].State == QueueItemState.Waiting;

                result.Add(e.ToItem(canUp, canDown, i + 1));
            }

            return result;
        }

        /// <summary>
        /// Решает судьбу позиции, у которой <c>RunAsync</c> только что вернулся: вернуть её
        /// в очередь (прервали ради другой позиции или игрок успел запустить её заново) или
        /// снять совсем.
        /// <para>
        /// РЕШЕНИЕ ПРИНИМАЕТСЯ ПОД <see cref="gate"/> ВМЕСТЕ С САМИМ ДЕЙСТВИЕМ. Раньше флаги
        /// читались снаружи замка, и между чтением и снятием позиции успевал вклиниться
        /// <see cref="Enqueue"/>: нажатие «Скачать» по только что остановленной игре
        /// пропадало вместе с позицией, а игрок видел пустую очередь и ничего не
        /// происходящее.
        /// </para>
        /// <para>
        /// Возвращённая в очередь позиция начинает с новой оценкой скорости, а воркер
        /// будится — он возьмёт ту, что теперь стоит первой. Прогресс не теряется: движок
        /// докачивает по Range из уцелевших .part-файлов.
        /// </para>
        /// </summary>
        /// <param name="entry">Позиция, которую только что перестали обрабатывать.</param>
        /// <param name="failed">Операция сорвалась — снимаем с ошибкой, а не с успехом.</param>
        private void Settle(Entry entry, bool failed) {
            IReadOnlyList<QueueItem> snapshot;
            bool requeued;
            var stillPresent = false;
            lock (this.gate) {
                requeued = entry.RequeueRequested;
                if (requeued) {
                    entry.RequeueRequested = false;
                    entry.CancelRequested = false;
                    entry.State = QueueItemState.Waiting;
                    entry.StatusText = "Ждёт очереди…";
                    entry.ResetSpeed();
                }
                else {
                    // State/StatusText мутируются под тем же gate, что читает Remove() — без
                    // этого Remove() мог застать позицию ещё Waiting/Running и сообщить об
                    // отмене только что успешно завершённой закачки.
                    var state = failed
                        ? QueueItemState.Failed
                        : entry.CancelRequested ? QueueItemState.Cancelled : QueueItemState.Completed;
                    stillPresent = this.items.Remove(entry);
                    entry.State = state;
                    entry.StatusText = state switch {
                        QueueItemState.Failed => "Не удалось завершить операцию.",
                        QueueItemState.Cancelled => "Снята из очереди.",
                        _ => "Готово.",
                    };
                }

                snapshot = this.SnapshotLocked();
            }

            if (requeued) {
                this.Reordered?.Invoke(snapshot);
                this.workSignal.Release();
                return;
            }

            if (!stillPresent) {
                // Remove() уже убрал позицию и разослал ItemRemoved сам — не дублируем и не
                // переопределяем то, что UI уже увидел.
                return;
            }

            this.ItemCompleted?.Invoke(entry.ToItem());

            // Уход позиции меняет «можно сдвинуть» у оставшихся: та, что была под ней,
            // становится верхней и теряет стрелку вверх.
            this.Reordered?.Invoke(snapshot);
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

                try {
                    await this.ProcessAsync(next).ConfigureAwait(false);
                }
                catch (Exception ex) {
                    // ВОРКЕР ОДИН НА ВЕСЬ ЛАУНЧЕР, И ЕГО СМЕРТЬ НЕЗАМЕТНА. ProcessAsync
                    // ловит свои исключения сам, но не всё в нём под try: поиск игры по
                    // списку — это чужой колбэк, заданный страницей. Улетев отсюда, любое
                    // исключение завершало задачу воркера, и очередь после этого молчала до
                    // перезапуска лаунчера, а позиция навсегда оставалась «качается» —
                    // ни снять, ни запустить заново.
                    Logging.Logger.Error(ex, $"DownloadQueue.RunWorkerAsync gid={next.GameId}");
                    this.Settle(next, failed: true);
                }

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
            // Всё, что может бросить исключение — включая сборку request/runner ниже, не только
            // сам RunAsync — идёт под этим try: если что-то из этого упадёт до входа в try,
            // позиция навсегда останется висеть в очереди как Running — Remove() для Running-
            // позиции не удаляет её из items сама, ждёт именно Finish().
            try {
                this.RaiseProgress(entry, "Начинаем…");

                var ui = new GameSyncUi {
                    SetStatus = text => this.RaiseProgress(entry, text),
                    // GameSyncRunner зовёт SetStatus только на границах шагов ("Загрузка манифеста…",
                    // "Сравнение файлов…"), а весь ExecuteAsync — самую долгую часть — отражает
                    // только через сюда, отчётами с полем Stage. Раньше здесь текст не менялся
                    // (оставался entry.StatusText как есть), и статус на карточке замирал на
                    // "Сравнение файлов…" на всё время реального скачивания, пока байты росли.
                    ReportProgress = (p, _) => this.RaiseProgress(entry, entry.Stage(p), p.BytesDownloaded, p.TotalBytes, p.NetworkBytes),
                    Confirm = this.confirm,
                };

                // Проверка удаляет всё, чего нет в манифесте, — моды, скриншоты,
                // сохранения в папке игры. Спрашиваем: у закачки такого шага нет, она
                // лишнего не трогает.
                var verifying = entry.Kind == QueueTaskKind.Verify;
                var syncKind = verifying
                    ? SyncKind.Repair
                    : (game.IsInstalled ? SyncKind.Update : SyncKind.Install);

                var runner = new GameSyncRunner(this.syncServiceFactory(), ui);
                var localRoot = GameLocalState.GameLocalRoot(entry.GameId);
                // Третьего вида здесь не бывает: Enqueue не принимает игру, которая
                // установлена и совпадает с последней версией, — «Проверить файлы» через
                // очередь не проходит.
                var request = new GameSyncRequest(
                    entry.GameId,
                    game.LatestVersion,
                    this.baseApiProvider(),
                    localRoot,
                    game.ExeRelativePath,
                    ConfirmDeletions: verifying,
                    Kind: syncKind,
                    // Игра целиком, а не только её идентификатор: у записи есть настройки
                    // модов, и без них обновление поставило бы сборку без модпака.
                    Game: game);

                // entry.Cts всегда назначен в RunWorkerAsync до вызова ProcessAsync — см. gate там.
                await runner.RunAsync(request, entry.Cts!.Token).ConfigureAwait(false);

                // Прервали, чтобы пропустить вперёд другую позицию или чтобы начать эту
                // заново, — тогда позиция возвращается в очередь, а не снимается. Решение
                // принимает Settle() под gate: снаружи замка его успевал обогнать Enqueue().
                this.Settle(entry, failed: false);
            }
            catch (Exception ex) {
                // GameSyncRunner.RunAsync сам не выпускает исключения наружу — сюда попадём,
                // если что-то пошло не так уже в самой очереди (например, отмена), либо если
                // упала сборка запроса выше.
                Logging.Logger.Error(ex, $"DownloadQueue.ProcessAsync gid={entry.GameId}");
                this.Settle(entry, failed: true);
            }
            finally {
                entry.Cts?.Dispose();
                entry.Cts = null;
            }
        }

        /// <summary>
        /// Переводит <see cref="SyncProgress.Stage"/> (см. значения в SimpleSyncService) в
        /// текст карточки очереди — тот же набор фраз, что раньше показывал прямой путь через
        /// StartUpdateAsync у выбранной игры.
        /// </summary>
        /// <param name="p">Отчёт синхронизации.</param>
        /// <returns>Текст для карточки очереди.</returns>
        internal static string StageText(SyncProgress p, QueueTaskKind kind = QueueTaskKind.Download) {
            var text = p.Stage switch {
                "Checking" => "Проверка…",

                // У проверки та же фаза называется иначе: она докачивает только то, что
                // разошлось с манифестом, и «Скачивание обновления…» на ней читалось бы
                // как «мне опять катят обновление», хотя игрок просил сверить файлы.
                "Downloading" => kind == QueueTaskKind.Verify ? "Восстановление файлов…" : "Скачивание обновления…",
                "Verifying" => "Проверка файлов…",
                "Activating" => "Применение обновления…",
                "Completed" => "Готово",
                _ => p.Stage,
            };

            // «Скачивание обновления…» на полутора гигабайтах модов — правда, но не
            // вся: игрок ждёт обновления ИГРЫ и не понимает, почему её столько.
            return string.IsNullOrEmpty(p.Scope) ? text : p.Scope + " · " + text;
        }

        private void RaiseProgress(
            Entry entry, string status, long bytesDownloaded = -1, long totalBytes = -1, long networkBytes = -1) {
            entry.StatusText = status;
            if (bytesDownloaded >= 0) {
                entry.BytesDownloaded = bytesDownloaded;
            }

            // Скорость — по пришедшему из сети, а не по сделанному: в сделанное идут и
            // файлы, взятые из соседней копии на диске, а копирование быстрее сети в разы.
            if (networkBytes >= 0) {
                entry.UpdateSpeed(networkBytes);
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
            IReadOnlyList<QueueItem> snapshot;
            lock (this.gate) {
                stillPresent = this.items.Remove(entry);
                entry.State = state;
                entry.StatusText = status;
                snapshot = this.SnapshotLocked();
            }

            if (!stillPresent) {
                // Remove() уже убрал позицию и разослал ItemRemoved сам — не дублируем и не
                // переопределяем то, что UI уже увидел.
                return;
            }

            this.ItemCompleted?.Invoke(entry.ToItem());

            // Уход позиции меняет «можно сдвинуть» у оставшихся: та, что была под ней,
            // становится верхней и теряет стрелку вверх.
            this.Reordered?.Invoke(snapshot);
        }

        /// <summary>Внутреннее изменяемое состояние позиции — наружу отдаём только снимки <see cref="QueueItem"/>.</summary>
        private sealed class Entry {
            private readonly Func<long> clock;

            internal Entry(string gameId, string title, string iconUrl, QueueTaskKind kind, Func<long> clock) {
                this.GameId = gameId;
                this.Title = title;
                this.IconUrl = iconUrl ?? string.Empty;
                this.Kind = kind;
                this.clock = clock;
            }

            internal string GameId { get; }

            internal string Title { get; }

            internal string IconUrl { get; }

            /// <summary>Что делаем с игрой: качаем или проверяем.</summary>
            internal QueueTaskKind Kind { get; }

            internal QueueItemState State { get; set; } = QueueItemState.Waiting;

            internal long BytesDownloaded { get; set; }

            internal long TotalBytes { get; set; }

            internal string StatusText { get; set; } = "Ждёт очереди…";

            /// <summary>Подпись стадии с учётом того, что именно делает позиция.</summary>
            /// <param name="p">Отчёт синхронизации.</param>
            /// <returns>Текст для строки очереди.</returns>
            internal string Stage(SyncProgress p) => StageText(p, this.Kind);

            internal bool CancelRequested { get; set; }

            /// <summary>
            /// Позицию прервали не для снятия, а чтобы пропустить вперёд другую: по возврату
            /// из RunAsync она обязана вернуться в очередь, а не исчезнуть из неё.
            /// </summary>
            internal bool RequeueRequested { get; set; }

            internal CancellationTokenSource? Cts { get; set; }

            /// <summary>Сглаженная скорость, Б/с. 0 — ещё не измеряли.</summary>
            internal double BytesPerSecond { get; private set; }

            /// <summary>Показания предыдущего замера — база для расчёта скорости.</summary>
            private long lastBytes;

            private long lastTicks;

            /// <summary>
            /// Пересчитывает скорость по приросту байт с прошлого отчёта, сглаживая
            /// экспоненциальным средним: мгновенная скорость скачет от чанка к чанку и в
            /// строке «12,4 МБ/с» дёргалась бы на каждом кадре.
            /// </summary>
            /// <param name="bytes">Сколько скачано всего на этот момент.</param>
            internal void UpdateSpeed(long bytes) {
                var now = this.clock();
                if (this.lastTicks == 0) {
                    this.lastTicks = now;
                    this.lastBytes = bytes;
                    return;
                }

                var elapsedMs = now - this.lastTicks;
                if (elapsedMs < 500) {
                    // Слишком короткий интервал: деление даёт шум, а не скорость.
                    return;
                }

                var delta = bytes - this.lastBytes;
                this.lastTicks = now;
                this.lastBytes = bytes;
                if (delta < 0) {
                    // Счётчик поехал назад (перезапуск файла) — прежнюю оценку не портим.
                    return;
                }

                var instant = delta * 1000.0 / elapsedMs;
                this.BytesPerSecond = this.BytesPerSecond <= 0
                    ? instant
                    : (SpeedEmaAlpha * instant) + ((1 - SpeedEmaAlpha) * this.BytesPerSecond);
            }

            /// <summary>Сбрасывает измерение скорости: после паузы прежняя оценка не про эту закачку.</summary>
            internal void ResetSpeed() {
                this.BytesPerSecond = 0;
                this.lastTicks = 0;
                this.lastBytes = 0;
            }

            internal QueueItem ToItem(bool canMoveUp = false, bool canMoveDown = false, int position = 0)
                => new(
                    this.GameId,
                    this.Title,
                    this.State,
                    this.BytesDownloaded,
                    this.TotalBytes,
                    this.StatusText,
                    this.BytesPerSecond,
                    canMoveUp,
                    canMoveDown,
                    this.IconUrl,
                    position,
                    this.Kind,

                    // Отмена уже запрошена, но движок ещё не остановился. Признак нужен
                    // именно снимку: по State такая позиция неотличима от работающей, и
                    // экран продолжал показывать её как идущую закачку.
                    this.CancelRequested && this.State == QueueItemState.Running);
        }
    }
}
