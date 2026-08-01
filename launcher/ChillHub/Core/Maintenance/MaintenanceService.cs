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
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

        /// <summary>Таймаут одного запроса: висеть на нём в фоне смысла нет.</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private static readonly object StartLock = new object();

        private static CancellationTokenSource? loopCts;

        /// <summary>Сколько раз подряд опрос не удался — чтобы не засорять лог одинаковыми записями.</summary>
        private static int consecutiveFailures;

        /// <summary>
        /// Состояние изменилось. Подписчикам приходит уже актуальное состояние;
        /// событие поднимается в UI-потоке, если приложение живо.
        /// </summary>
        public static event Action<MaintenanceState>? Changed;

        /// <summary>Gets текущее известное состояние. До первого успешного опроса — «работы не идут».</summary>
        public static MaintenanceState Current { get; private set; } = MaintenanceState.Off;

        /// <summary>
        /// Запускает фоновый опрос. Повторные вызовы игнорируются, поэтому метод можно
        /// звать из любого места старта приложения.
        /// </summary>
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
        /// Разовый внеочередной опрос: пригодится, когда пользователь сам жмёт «Повторить».
        /// </summary>
        /// <returns>Актуальное состояние (или прежнее, если опрос не удался).</returns>
        public static async Task<MaintenanceState> RefreshNowAsync() {
            var state = await FetchAsync(CancellationToken.None).ConfigureAwait(false);
            if (state != null) {
                Apply(state);
            }

            return Current;
        }

        private static async Task PollLoopAsync(CancellationToken token) {
            while (!token.IsCancellationRequested) {
                try {
                    var state = await FetchAsync(token).ConfigureAwait(false);
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
                using var resp = await HttpClientProvider.Shared
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

        private static string BuildUrl() {
            var baseUrl = (ConfigService.Current?.ApiBaseUrl ?? string.Empty).TrimEnd('/');
            return baseUrl + EndpointPath;
        }

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
