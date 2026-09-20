// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.

namespace ChillHub.Core {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;

    using ChillHub.Core.Net;

    /// <summary>
    /// Centralized error reporter that sends a bug feedback with diagnostics to the server.
    /// Non-blocking: reports are sent fire-and-forget.
    /// </summary>
    public static class ErrorReporter {
        /// <summary>
        /// Переменная окружения, глушащая автоотчёты: CHILLHUB_ERROR_REPORTS=0.
        /// Это не настройка пользователя, а рубильник для тестов и отладочных
        /// прогонов — парный к CHILLHUB_METRICS у статистики.
        /// </summary>
        internal const string EnvVar = "CHILLHUB_ERROR_REPORTS";

        private static readonly object rlLock = new object();
        private static readonly Dictionary<string, (int Count, DateTime WindowStart, DateTime LastSent)> rate = new();
        private const int RL_WindowSeconds = 180;  // signature throttle window (3 minutes)
        private const int RL_MaxPerWindow = 3;     // max reports per signature per window

        public static event Action<string>? AutoReported;
        public static event Action<TimeSpan>? AutoReportSuppressed; // fired when global quota exceeded

        // Auto-report global persistent quota: 3 per 3 minutes
        private const int GLOBAL_MAX_PER_WINDOW = 3;
        private static readonly TimeSpan GLOBAL_WINDOW = TimeSpan.FromMinutes(3);
        private static readonly object gqLock = new object();
        private static string GlobalQuotaPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "report_rl.json");

        private sealed class GlobalQuotaState { public int Count { get; set; } public DateTime WindowStartUtc { get; set; } }

        // Manual feedback quota: 5 per 5 minutes
        private const int MANUAL_MAX_PER_WINDOW = 5;
        private static readonly TimeSpan MANUAL_WINDOW = TimeSpan.FromMinutes(5);
        private static readonly object mqLock = new object();
        private static string ManualQuotaPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "report_manual_rl.json");

        private sealed class ManualQuotaState { public int Count { get; set; } public DateTime WindowStartUtc { get; set; } }

        // Не readonly только ради OverrideHttpForTests: в приложении значение задаётся один раз.
        private static HttpClient http = HttpClientProvider.Shared;

        /// <summary>
        /// Уводит отправку отчётов на подставной транспорт на время теста.
        /// <para>
        /// Без этого шва <see cref="ReportCoreAsync"/> нечем проверить: единственный способ
        /// убедиться, что выключенный тумблер автоотчётов не выпускает НИ ОДНОГО запроса,
        /// а имя пользователя не утекает в тело письма, — посмотреть на то, что реально
        /// ушло бы в сеть.
        /// </para>
        /// </summary>
        /// <param name="client">Клиент, которым отправлять отчёты.</param>
        /// <returns>Объект, возвращающий настоящий транспорт.</returns>
        internal static IDisposable OverrideHttpForTests(HttpClient client) => new HttpOverride(client);

        /// <summary>
        /// Даёт тесту дождаться отправки отчёта. <see cref="Report"/> сознательно
        /// «выстрелил и забыл», поэтому наблюдать за его результатом нечем.
        /// </summary>
        /// <param name="ex">Исключение, о котором сообщаем.</param>
        /// <param name="context">Место, где оно случилось.</param>
        /// <param name="includeDiagnostics">Прикладывать ли логи и диагностику.</param>
        /// <returns>Задача отправки.</returns>
        internal static Task ReportForTestsAsync(Exception ex, string context, bool includeDiagnostics = false)
            => ReportCoreAsync(ex, context, includeDiagnostics);

        /// <summary>
        /// Забывает счётчики повторов. Окно дедупликации живёт в статике и переживает
        /// границы тестов: без сброса второй тест получал бы чужой счётчик.
        /// </summary>
        internal static void ResetThrottleForTests() {
            lock (rlLock) {
                rate.Clear();
            }
        }

        /// <summary>Возвращает настоящий транспорт после <see cref="OverrideHttpForTests"/>.</summary>
        private sealed class HttpOverride : IDisposable {
            private readonly HttpClient previous;

            internal HttpOverride(HttpClient client) {
                this.previous = http;
                http = client;
            }

            public void Dispose() => http = this.previous;
        }

        public static bool TryConsumeGlobal(out TimeSpan retryAfter) {
            retryAfter = TimeSpan.Zero;
            try {
                lock (gqLock) {
                    var path = GlobalQuotaPath;
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir)) {
                        System.IO.Directory.CreateDirectory(dir);
                    }

                    GlobalQuotaState st = new GlobalQuotaState { Count = 0, WindowStartUtc = DateTime.UtcNow };
                    try {
                        if (System.IO.File.Exists(path)) {
                            var json = System.IO.File.ReadAllText(path, Encoding.UTF8);
                            st = System.Text.Json.JsonSerializer.Deserialize<GlobalQuotaState>(json) ?? st;
                        }
                    }
                    catch { }

                    var now = DateTime.UtcNow;
                    if (st.WindowStartUtc == default || (now - st.WindowStartUtc) >= GLOBAL_WINDOW) {
                        st.WindowStartUtc = now;
                        st.Count = 0;
                    }
                    if (st.Count >= GLOBAL_MAX_PER_WINDOW) {
                        var end = st.WindowStartUtc + GLOBAL_WINDOW;
                        retryAfter = (end > now) ? (end - now) : TimeSpan.Zero;
                        return false;
                    }
                    st.Count++;
                    try { System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(st), Encoding.UTF8); } catch { }
                    return true;
                }
            }
            catch { return true; }
        }

        /// <summary>
        /// Возвращает слот, списанный <see cref="TryConsumeGlobal"/>, когда отчёт до
        /// сервера так и не дошёл.
        /// <para>
        /// Квота считает отчёты, ПРИНЯТЫЕ сервером, а не попытки их отправить. Без
        /// возврата три исключения на машине без сети выжигали её целиком, и первый же
        /// отчёт, который дошёл бы, пользователь видел заглушённым: очереди у
        /// автоотчётов нет, непринятый отчёт теряется навсегда.
        /// </para>
        /// </summary>
        private static void RefundGlobal() {
            try {
                lock (gqLock) {
                    var path = GlobalQuotaPath;
                    if (!File.Exists(path)) {
                        return;
                    }

                    var st = JsonSerializer.Deserialize<GlobalQuotaState>(File.ReadAllText(path, Encoding.UTF8));
                    if (st == null || st.Count <= 0) {
                        return;
                    }

                    // Окно успело смениться, пока ждали ответа: списанный слот остался в
                    // прошлом окне, а уменьшать счётчик нового значило бы выдать лишнюю
                    // попытку сверх лимита.
                    if (st.WindowStartUtc == default || (DateTime.UtcNow - st.WindowStartUtc) >= GLOBAL_WINDOW) {
                        return;
                    }

                    st.Count--;
                    File.WriteAllText(path, JsonSerializer.Serialize(st), Encoding.UTF8);
                }
            }
            catch {
                // Не смогли вернуть слот — отправку это ронять не должно
            }
        }

        public static bool TryConsumeManual(out TimeSpan retryAfter) {
            retryAfter = TimeSpan.Zero;
            try {
                lock (mqLock) {
                    var path = ManualQuotaPath;
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir)) {
                        System.IO.Directory.CreateDirectory(dir);
                    }

                    ManualQuotaState st = new ManualQuotaState { Count = 0, WindowStartUtc = DateTime.UtcNow };
                    try {
                        if (System.IO.File.Exists(path)) {
                            var json = System.IO.File.ReadAllText(path, Encoding.UTF8);
                            st = System.Text.Json.JsonSerializer.Deserialize<ManualQuotaState>(json) ?? st;
                        }
                    }
                    catch { }

                    var now = DateTime.UtcNow;
                    if (st.WindowStartUtc == default || (now - st.WindowStartUtc) >= MANUAL_WINDOW) {
                        st.WindowStartUtc = now;
                        st.Count = 0;
                    }
                    if (st.Count >= MANUAL_MAX_PER_WINDOW) {
                        var end = st.WindowStartUtc + MANUAL_WINDOW;
                        retryAfter = (end > now) ? (end - now) : TimeSpan.Zero;
                        return false;
                    }
                    st.Count++;
                    try { System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(st), Encoding.UTF8); } catch { }
                    return true;
                }
            }
            catch { return true; }
        }

        /// <summary>
        /// Hook global exception handlers to auto-report unhandled exceptions.
        /// Safe to call multiple times.
        /// </summary>
        public static void InitGlobalHandlers() {
            try {
                AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            }
            catch { }

            try {
                if (Application.Current != null) {
                    Application.Current.DispatcherUnhandledException -= Current_DispatcherUnhandledException;
                    Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
                }
            }
            catch { }

            try {
                TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
                TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            }
            catch { }
        }

        /// <summary>
        /// Сколько отчётов сейчас в полёте: <see cref="Report"/> их не ждёт, а
        /// тестам без этого счётчика не отличить «ничего не отправлено» от
        /// «ещё не доехало». Подробности — у <see cref="WaitForIdleForTests"/>.
        /// </summary>
        private static int inFlight;

        /// <summary>
        /// Fire-and-forget error report.
        /// Работа целиком уходит в пул потоков: сбор диагностики синхронный и тяжёлый
        /// (SHA-256 файлов лаунчера, обход дерева папки игр, чтение логов), а Report вызывается
        /// из Logger.Error — то есть часто с UI-потока. Раньше запуск без интернета подвешивал
        /// интерфейс на секунды ещё до первого сетевого await.
        /// </summary>
        public static void Report(Exception ex, string context, bool includeDiagnostics = true) {
            try {
                Interlocked.Increment(ref inFlight);
                _ = Task.Run(async () => {
                    try {
                        await ReportCoreAsync(ex, context, includeDiagnostics).ConfigureAwait(false);
                    }
                    finally {
                        Interlocked.Decrement(ref inFlight);
                    }
                });
            }
            catch (Exception scheduleEx) {
                Interlocked.Decrement(ref inFlight);
                // Пул потоков недоступен (выгрузка приложения) — отчёт не важнее живучести
                System.Diagnostics.Debug.WriteLine("ErrorReporter.Report: " + scheduleEx.Message);
            }
        }

        /// <summary>
        /// Ждёт, пока разойдутся отчёты, запущенные <see cref="Report"/>.
        /// <para>
        /// ОТЧЁТ ПЕРЕЖИВАЕТ ВЫЗОВ, КОТОРЫЙ ЕГО ЗАКАЗАЛ. Report сознательно
        /// «выстрелил и забыл»: он зовётся из Logger.Error, часто с UI-потока,
        /// и ждать на нём сбор диагностики нельзя. В тестах у этого есть цена:
        /// отчёт, заказанный одним тестом, доезжает в середине следующего — и
        /// уходит через подменённый транспорт ЕГО области, попадая в чужие
        /// ожидания и в чужой файл квоты.
        /// </para>
        /// <para>
        /// Ловилось это только на загруженной машине CI и выглядело как
        /// случайное падение то одного теста, то другого: то лишний запрос в
        /// проверке, то «файл занят другим процессом» на report_rl.json.
        /// </para>
        /// </summary>
        /// <param name="timeout">Сколько ждать; по истечении просто возвращает управление.</param>
        /// <returns>true, если в полёте больше ничего нет.</returns>
        internal static bool WaitForIdleForTests(TimeSpan timeout) {
            var until = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref inFlight) > 0) {
                if (DateTime.UtcNow >= until) {
                    return false;
                }

                Thread.Sleep(5);
            }

            return true;
        }

        /// <summary>
        /// Sends error report asynchronously. Does not throw; failures are swallowed.
        /// Вызывать только из <see cref="Report"/>: метод рассчитывает, что уже находится
        /// не на UI-потоке.
        /// </summary>
        private static async Task ReportCoreAsync(Exception ex, string context, bool includeDiagnostics = true) {
            var quotaConsumed = false;
            try {
                // Тумблера в настройках у автоотчётов больше нет: они всегда включены.
                // Осталась переменная окружения — тем же приёмом, что у метрик
                // (CHILLHUB_METRICS): ею глушат отправку тесты и отладочные прогоны,
                // которым в сеть ходить нечего. Пользователю она не показывается.
                if (Environment.GetEnvironmentVariable(EnvVar)?.Trim() == "0") {
                    return;
                }

                var baseApi = (ChillHub.Core.ConfigService.Current.ApiBaseUrl ?? string.Empty).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(baseApi)) {
                    return;
                }

                var url = baseApi + "/feedback/submit";

                // Rate limit identical errors
                var sig = BuildSignature(ex, context);
                if (ShouldThrottle(sig)) {
                    return;
                }

                // Global persistent quota. Слот списывается авансом, а на любом исходе,
                // кроме принятого сервером отчёта, возвращается через RefundGlobal:
                // считаем доставленные отчёты, а не попытки.
                if (!TryConsumeGlobal(out var retryAfter)) { OnAutoReportSuppressed(retryAfter); return; }
                quotaConsumed = true;

                string logs = string.Empty;
                Dictionary<string, string>? system = CollectSystemInfo();
                try { system["auto"] = "1"; } catch { }
                if (includeDiagnostics) {
                    try {
                        var bundle = Diagnostics.Build();
                        logs = bundle.LogsMarkdown;
                        foreach (var kv in bundle.SystemHints) {
                            system[kv.Key] = kv.Value;
                        }
                    }
                    catch { }
                }

                var payload = new {
                    name = "auto",
                    contact = string.Empty,
                    type = "bug",
                    // Через Redact: в тексте исключения регулярно лежат пути вида
                    // C:\Users\<имя>\..., а логи в этом же отчёте уже редактируются.
                    comment = Diagnostics.Redact($"[AUTO] Context: {context}\n\n" + ex.ToString()),
                    attachLogs = includeDiagnostics,
                    logs = logs,
                    system = system,
                };

                // Тело сериализуем один раз, а объект контента у каждого запроса свой:
                // запасные запросы переиспользовали контент первого и освобождали его
                // повторно вместе со своим using. Для StringContent это сходило с рук,
                // но с потоковым контентом запасной путь молча отправил бы пустое тело.
                var json = JsonSerializer.Serialize(payload);

                using var req = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                HttpResponseMessage res;
                try {
                    res = await http.SendAsync(req).ConfigureAwait(false);
                }
                catch {
                    // Fallback for local dev to admin port 55777
                    if (TryBuildLocalAdminUrl(baseApi, out var adminUrl)) {
                        try {
                            using var req2 = new HttpRequestMessage(HttpMethod.Post, adminUrl) {
                                Content = new StringContent(json, Encoding.UTF8, "application/json")
                            };
                            var r2 = await http.SendAsync(req2).ConfigureAwait(false);
                            if (r2.IsSuccessStatusCode) { OnAutoReported(context); return; }
                        }
                        catch { }
                    }

                    RefundGlobal();
                    return;
                }

                if (!res.IsSuccessStatusCode) {
                    // Try admin fallback if API rejected (port mismatch etc.)
                    if (TryBuildLocalAdminUrl(baseApi, out var adminUrl2)) {
                        try {
                            using var req3 = new HttpRequestMessage(HttpMethod.Post, adminUrl2) {
                                Content = new StringContent(json, Encoding.UTF8, "application/json")
                            };
                            var r3 = await http.SendAsync(req3).ConfigureAwait(false);
                            if (r3.IsSuccessStatusCode) { OnAutoReported(context); return; }
                        }
                        catch { }
                    }

                    RefundGlobal();
                }
                else { OnAutoReported(context); }
            }
            catch {
                // Сорвались, не отправив: слот квоты возвращаем — иначе её выжигают
                // попытки, ни одна из которых до сервера не дошла.
                if (quotaConsumed) { RefundGlobal(); }
            }
        }

        private static void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e) {
            try {
                var ex = e.ExceptionObject as Exception;
                if (ex != null) {
                    Report(ex, "AppDomain.UnhandledException", includeDiagnostics: true);
                }
            }
            catch { }
        }

        private static void Current_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
            try {
                if (e?.Exception != null) {
                    Report(e.Exception, "DispatcherUnhandledException", includeDiagnostics: true);
                }
            }
            catch { }
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) {
            try {
                if (e?.Exception != null) {
                    Report(e.Exception, "TaskScheduler.UnobservedTaskException", includeDiagnostics: true);
                }
            }
            catch { }
        }

        private static void OnAutoReported(string context) { try { AutoReported?.Invoke(context); } catch { } }
        private static void OnAutoReportSuppressed(TimeSpan retryAfter) { try { AutoReportSuppressed?.Invoke(retryAfter); } catch { } }

        internal static bool TryBuildLocalAdminUrl(string baseApi, out string adminUrl) {
            adminUrl = string.Empty;
            try {
                if (!Uri.TryCreate(baseApi, UriKind.Absolute, out var u)) {
                    return false;
                }

                var host = (u.Host ?? string.Empty).ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1") {
                    var ub = new UriBuilder(u) { Port = 55777 };
                    adminUrl = new Uri(ub.Uri, "/feedback/submit").ToString();
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static Dictionary<string, string> CollectSystemInfo() {
            var dict = new Dictionary<string, string>();
            try {
                dict["os"] = Environment.OSVersion.VersionString;
                dict["arch"] = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                dict["dotnet"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

                // machineName убран намеренно: см. FeedbackService.CollectSystemInfo
                dict["appVersion"] = typeof(ErrorReporter).Assembly.GetName().Version?.ToString() ?? string.Empty;
            }
            catch { }
            return dict;
        }

        internal static string BuildSignature(Exception ex, string context) {
            try {
                var type = ex.GetType().FullName ?? "";
                var msg = ex.Message ?? "";
                string top = "";
                try {
                    var st = ex.StackTrace ?? "";
                    int nl = st.IndexOf('\n');
                    top = nl > 0 ? st.Substring(0, nl) : st;
                }
                catch { }
                var raw = (context ?? "") + "|" + type + "|" + msg + "|" + top;
                // simple stable hash
                unchecked {
                    int h = 17; foreach (var ch in raw) {
                        h = h * 31 + ch;
                    }

                    return h.ToString("x8");
                }
            }
            catch { return "sig"; }
        }

        internal static bool ShouldThrottle(string sig) {
            lock (rlLock) {
                var now = DateTime.UtcNow;
                if (!rate.TryGetValue(sig, out var st)) {
                    rate[sig] = (1, now, now);
                    return false;
                }
                if ((now - st.WindowStart).TotalSeconds > RL_WindowSeconds) {
                    rate[sig] = (1, now, now);
                    return false;
                }
                if (st.Count >= RL_MaxPerWindow) {
                    return true;
                }
                rate[sig] = (st.Count + 1, st.WindowStart, now);
                return false;
            }
        }
    }
}
