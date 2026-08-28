// <copyright file="MaintenanceService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Maintenance {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;

    using ChillHub.Core.Net;

    /// <summary>
    /// Опрос режима технических работ на сервере.
    /// <para>
    /// Задача сервиса — знать актуальное состояние и сообщать о его смене.
    /// Что именно блокировать и как рисовать баннер, решают страницы: см.
    /// <see cref="Current"/> и <see cref="Changed"/>.
    /// </para>
    /// <para>
    /// Отказоустойчивость важнее свежести: сервер недоступен, эндпоинта нет (старая
    /// версия сервера), ответ не разобрался — считаем, что режим выключен, и НИЧЕГО
    /// не показываем пользователю. Лаунчер обязан работать со старым сервером как раньше.
    /// </para>
    /// <para>
    /// Выход из режима автоматический: как только сервер ответит <c>enabled: false</c>,
    /// поднимется <see cref="Changed"/> и UI разблокируется — без перезапуска клиента.
    /// </para>
    /// </summary>
    public static class MaintenanceService {
        /// <summary>
        /// Путь эндпоинта состояния относительно <c>ApiBaseUrl</c>.
        /// <para>
        /// Путь и форма ответа сверены с реализацией сервера
        /// (<c>server/internal/maintenance</c>) и проверены на проде: эндпоинт
        /// отвечает 200 всегда, в том числе когда техработ нет.
        /// </para>
        /// </summary>
        public const string EndpointPath = "/api/maintenance";

        /// <summary>Как часто перепроверяем состояние. Достаточно редко, чтобы не шуметь в сети.</summary>
        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);

        /// <summary>Таймаут одного запроса: висеть на нём в фоне смысла нет.</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private static readonly object StartLock = new object();

        private static CancellationTokenSource? loopCts;

        /// <summary>Идущий сейчас внеочередной опрос — чтобы параллельные <see cref="RefreshNowAsync"/> делили один запрос.</summary>
        private static Task<MaintenanceState>? refreshInFlight;

        /// <summary>Сколько раз подряд опрос не удался — чтобы не засорять лог одинаковыми записями.</summary>
        private static int consecutiveFailures;

        /// <summary>Сколько циклов опроса сейчас живо. Больше одного означало бы двойной опрос.</summary>
        private static int runningLoops;

        /// <summary>
        /// Состояние изменилось. Подписчикам приходит уже актуальное состояние;
        /// событие поднимается в UI-потоке, если приложение живо.
        /// </summary>
        public static event Action<MaintenanceState>? Changed;

        /// <summary>Gets текущее известное состояние. До первого успешного опроса — «работы не идут».</summary>
        public static MaintenanceState Current { get; private set; } = MaintenanceState.Off;

        /// <summary>
        /// Gets or sets интервал между опросами. В приложении всегда
        /// <see cref="DefaultPollInterval"/>; отдельная точка нужна тестам, чтобы проверять
        /// поведение цикла, не ожидая минуту реального времени.
        /// </summary>
        internal static TimeSpan PollInterval { get; set; } = DefaultPollInterval;

        /// <summary>
        /// Gets or sets клиента, которым выполняется запрос. null — общий клиент приложения.
        /// Единственный шов, позволяющий проверить разбор ответов сервера без сети.
        /// </summary>
        internal static HttpClient? HttpOverride { get; set; }

        /// <summary>Gets число живых циклов опроса: повторный <see cref="Start"/> не должен его увеличивать.</summary>
        internal static int RunningLoops => Volatile.Read(ref runningLoops);

        /// <summary>Gets длину текущей серии неудачных опросов.</summary>
        internal static int ConsecutiveFailures => Volatile.Read(ref consecutiveFailures);

        /// <summary>
        /// Запускает фоновый опрос. Повторные вызовы игнорируются, поэтому метод можно
        /// звать из любого места старта приложения.
        /// </summary>
        /// <summary>
        /// Gets or sets a value indicating whether опрос приостановлен: окно спрятано, и
        /// показывать баннер режима работ некому.
        /// <para>
        /// Лаунчер живёт в трее часами, и всё это время опрос уходил в сеть раз в минуту —
        /// шестьдесят запросов в час ради баннера, которого никто не видит. Возврат окна
        /// на экран сам дёргает <see cref="RefreshNowAsync"/>, так что свежесть при этом
        /// не теряется: она восстанавливается ровно тогда, когда становится нужна.
        /// </para>
        /// </summary>
        public static bool Suspended { get; set; }

        public static void Start() {
            lock (StartLock) {
                if (loopCts != null) {
                    return;
                }

                loopCts = new CancellationTokenSource();
                var token = loopCts.Token;
                _ = Task.Run(() => PollLoopAsync(token), token);
            }
        }

        /// <summary>Останавливает фоновый опрос (выход из приложения).</summary>
        public static void Stop() {
            lock (StartLock) {
                try {
                    loopCts?.Cancel();
                    loopCts?.Dispose();
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"MaintenanceService.Stop: {ex.Message}");
                }

                loopCts = null;
            }
        }

        /// <summary>
        /// Разовый внеочередной опрос — при разворачивании окна из трея и возврате фокуса
        /// (см. <c>MainWindow</c>): человек, вернувшийся к лаунчеру, должен увидеть актуальное
        /// состояние сразу, а не через остаток интервала фонового цикла.
        /// <para>
        /// Параллельные вызовы схлопываются в один запрос: разворачивание из трея поднимает
        /// и <c>RestoreFromTray</c>, и <c>Activated</c>, а бить по серверу дважды за одно
        /// действие незачем. Ошибок наружу не выпускает — вызывающие зовут его без await.
        /// </para>
        /// </summary>
        /// <returns>Актуальное состояние (или прежнее, если опрос не удался).</returns>
        public static Task<MaintenanceState> RefreshNowAsync() {
            TaskCompletionSource<MaintenanceState> mine;
            lock (StartLock) {
                if (refreshInFlight is { IsCompleted: false } running) {
                    return running;
                }

                mine = new TaskCompletionSource<MaintenanceState>(TaskCreationOptions.RunContinuationsAsynchronously);
                refreshInFlight = mine.Task;
            }

            // Сам запрос — уже вне замка: синхронная часть HttpClient не должна держать
            // его, иначе второй вызов ждал бы не результата, а начала первого запроса.
            _ = RefreshCoreAsync().ContinueWith(
                t => mine.TrySetResult(t.IsCompletedSuccessfully ? t.Result : Current),
                TaskContinuationOptions.ExecuteSynchronously);
            return mine.Task;
        }

        /// <summary>
        /// Собирает адрес эндпоинта из базового адреса сервера. Вынесено отдельно от чтения
        /// конфигурации, потому что ошибиться здесь можно только со слешами, а проверять это
        /// удобнее без подмены настроек пользователя.
        /// </summary>
        /// <param name="baseUrl">Базовый адрес API (может быть пустым или с хвостовым слешем).</param>
        /// <returns>Полный адрес запроса.</returns>
        internal static string BuildUrl(string? baseUrl)
            => (baseUrl ?? string.Empty).TrimEnd('/') + EndpointPath;

        /// <summary>
        /// Возвращает сервис в исходное состояние. Нужно тестам: состояние статическое,
        /// и без сброса результат одного теста утекал бы в следующий.
        /// </summary>
        internal static void ResetForTests() {
            Stop();
            Changed = null;
            refreshInFlight = null;
            Current = MaintenanceState.Off;
            Volatile.Write(ref consecutiveFailures, 0);
            HttpOverride = null;
            PollInterval = DefaultPollInterval;
        }

        private static async Task<MaintenanceState> RefreshCoreAsync() {
            try {
                var state = await FetchAsync(CancellationToken.None).ConfigureAwait(false);
                if (state != null) {
                    Apply(state);
                }
            }
            catch (Exception ex) {
                // FetchAsync сам глотает сетевые ошибки; сюда долетает разве что сбой
                // подписчика или отмена — и то и другое не повод ронять вызывающего.
                Logging.Logger.Warn($"MaintenanceService.RefreshNow: {ex.Message}");
            }

            return Current;
        }

        private static async Task PollLoopAsync(CancellationToken token) {
            Interlocked.Increment(ref runningLoops);
            try {
                while (!token.IsCancellationRequested) {
                    try {
                        // Спрятанному окну свежий режим работ не нужен: баннер показывать
                        // некому, а окно, вернувшееся на экран, спрашивает само.
                        var state = Suspended ? null : await FetchAsync(token).ConfigureAwait(false);
                        if (state != null) {
                            Apply(state);
                        }
                    }
                    catch (OperationCanceledException) {
                        return;
                    }
                    catch (Exception ex) {
                        // Цикл обязан пережить любую ошибку: иначе клиент навсегда останется
                        // с последним известным состоянием и не выйдет из режима автоматически.
                        Logging.Logger.Warn($"MaintenanceService.PollLoop: {ex.Message}");
                    }

                    try {
                        await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) {
                        return;
                    }
                }
            }
            finally {
                Interlocked.Decrement(ref runningLoops);
            }
        }

        /// <summary>
        /// Один запрос к серверу. Возвращает null, если состояние определить не удалось
        /// (тогда прежнее состояние сохраняется), и <see cref="MaintenanceState.Off"/>,
        /// если сервер явно сказал «работ нет» либо эндпоинта у него вовсе нет.
        /// </summary>
        private static async Task<MaintenanceState?> FetchAsync(CancellationToken token) {
            var url = BuildUrl();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);

            try {
                using var resp = await (HttpOverride ?? HttpClientProvider.Shared)
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                // Старый сервер без эндпоинта: это не ошибка, режим просто выключен.
                if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.NotImplemented) {
                    if (consecutiveFailures == 0) {
                        Logging.Logger.Info($"MaintenanceService: эндпоинт {url} не найден ({(int)resp.StatusCode}) — режим обслуживания считаем выключенным");
                    }

                    consecutiveFailures++;
                    return MaintenanceState.Off;
                }

                resp.EnsureSuccessStatusCode();
                var state = await resp.Content
                    .ReadFromJsonAsync<MaintenanceState>(cancellationToken: timeout.Token)
                    .ConfigureAwait(false);

                consecutiveFailures = 0;
                return state ?? MaintenanceState.Off;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            }
            catch (Exception ex) {
                // Сеть недоступна, таймаут, мусор вместо JSON. Пользователю об этом знать
                // незачем: режим обслуживания — вспомогательная информация.
                // В лог пишем только первое падение серии, чтобы не забивать файл.
                if (consecutiveFailures == 0) {
                    Logging.Logger.Warn($"MaintenanceService: опрос {url} не удался ({ex.GetType().Name}: {ex.Message}); считаем, что работ нет");
                }

                consecutiveFailures++;

                // Прежнее состояние не трогаем: одиночный сетевой сбой не повод
                // снимать баннер, который сервер только что показывал.
                return null;
            }
        }

        private static string BuildUrl() => BuildUrl(ConfigService.Current?.ApiBaseUrl);

        private static void Apply(MaintenanceState state) {
            if (state.SameAs(Current)) {
                return;
            }

            var previous = Current;
            Current = state;
            Logging.Logger.Info(
                $"MaintenanceService: состояние изменилось enabled={previous.Enabled}->{state.Enabled} "
                + $"install={state.BlocksInstall} update={state.BlocksUpdate} play={state.BlocksPlay} reason='{state.Reason}' endsAt={state.EndsAt}");

            RaiseChanged(state);
        }

        private static void RaiseChanged(MaintenanceState state) {
            var handler = Changed;
            if (handler == null) {
                return;
            }

            try {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) {
                    handler(state);
                    return;
                }

                dispatcher.BeginInvoke(new Action(() => {
                    try {
                        handler(state);
                    }
                    catch (Exception ex) {
                        Logging.Logger.Error(ex, "MaintenanceService.Changed(UI)");
                    }
                }));
            }
            catch (Exception ex) {
                // Подписчик упал или диспетчер уже мёртв — опрос из-за этого прерывать нельзя
                Logging.Logger.Error(ex, "MaintenanceService.RaiseChanged");
            }
        }
    }
}
