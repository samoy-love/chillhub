// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.

namespace ChillHub.Core {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using System.Windows;

    using ChillHub.Core.Net;

    /// <summary>
    /// Centralized error reporter that sends a bug feedback with diagnostics to the server.
    /// Non-blocking: reports are sent fire-and-forget.
    /// </summary>
    public static class ErrorReporter {
        private static readonly HttpClient http = HttpClientProvider.Shared;
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
        /// Fire-and-forget error report.
        /// Работа целиком уходит в пул потоков: сбор диагностики синхронный и тяжёлый
        /// (SHA-256 файлов лаунчера, обход дерева папки игр, чтение логов), а Report вызывается
        /// из Logger.Error — то есть часто с UI-потока. Раньше запуск без интернета подвешивал
        /// интерфейс на секунды ещё до первого сетевого await.
        /// </summary>
        public static void Report(Exception ex, string context, bool includeDiagnostics = true) {
            try {
                _ = Task.Run(() => ReportCoreAsync(ex, context, includeDiagnostics));
            }
            catch (Exception scheduleEx) {
                // Пул потоков недоступен (выгрузка приложения) — отчёт не важнее живучести
                System.Diagnostics.Debug.WriteLine("ErrorReporter.Report: " + scheduleEx.Message);
            }
        }

        /// <summary>
        /// Sends error report asynchronously. Does not throw; failures are swallowed.
        /// Вызывать только из <see cref="Report"/>: метод рассчитывает, что уже находится
        /// не на UI-потоке.
        /// </summary>
        private static async Task ReportCoreAsync(Exception ex, string context, bool includeDiagnostics = true) {
            try {
                // Автоотчёты отправляем только с согласия пользователя (тумблер в настройках).
                // На ручную отправку обратной связи (TryConsumeManual / формы фидбэка) это не влияет.
                if (!ChillHub.Core.ConfigService.Current.AutoErrorReports) {
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

                // Global persistent quota
                if (!TryConsumeGlobal(out var retryAfter)) { OnAutoReportSuppressed(retryAfter); return; }

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

                using var req = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                HttpResponseMessage res;
                try {
                    res = await http.SendAsync(req).ConfigureAwait(false);
                }
                catch {
                    // Fallback for local dev to admin port 55777
                    if (TryBuildLocalAdminUrl(baseApi, out var adminUrl)) {
                        try {
                            using var req2 = new HttpRequestMessage(HttpMethod.Post, adminUrl) { Content = req.Content };
                            var r2 = await http.SendAsync(req2).ConfigureAwait(false);
                            if (r2.IsSuccessStatusCode) { OnAutoReported(context); }
                        }
                        catch { }
                    }
                    return;
                }

                if (!res.IsSuccessStatusCode) {
                    // Try admin fallback if API rejected (port mismatch etc.)
                    if (TryBuildLocalAdminUrl(baseApi, out var adminUrl2)) {
                        try {
                            using var req3 = new HttpRequestMessage(HttpMethod.Post, adminUrl2) { Content = req.Content };
                            var r3 = await http.SendAsync(req3).ConfigureAwait(false);
                            if (r3.IsSuccessStatusCode) { OnAutoReported(context); }
                        }
                        catch { }
                    }
                }
                else { OnAutoReported(context); }
            }
            catch { }
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

        private static bool TryBuildLocalAdminUrl(string baseApi, out string adminUrl) {
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

        private static Dictionary<string, string> CollectSystemInfo() {
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

        private static string BuildSignature(Exception ex, string context) {
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

        private static bool ShouldThrottle(string sig) {
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
