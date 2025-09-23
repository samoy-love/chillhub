// <copyright file="HomePage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Input;

    using System.Windows.Media.Imaging;
    using System.Windows.Threading;

    using ChillHub.Core;
    using ChillHub.Core.Net;
    using ChillHub.Core.Sync;

    public partial class HomePage : Page {
        private string BaseApi => ChillHub.Core.ConfigService.Current.ApiBaseUrl;

        private readonly HttpClient http = HttpClientProvider.Shared;
        private List<GameInfo> games = new();
        private List<string> builds = new();
        private CancellationTokenSource? cts;
        private bool isUpdating = false;
        private readonly ISyncService sync = new SimpleSyncService();
        private double emaSpeedMBs = 0.0; // сглаженная скорость
        private const double EmaAlpha = 0.2; // чувствительность EMA

        // Кэш оценок требуемого объёма скачивания по игре (обновляется при VerifyGameStatusAsync)
        private readonly object spaceCacheLock = new();
        private readonly Dictionary<string, long> neededBytesCache = new(StringComparer.OrdinalIgnoreCase);

        // Флаг фоновой первичной проверки статусов при старте, чтобы не дублировать тяжёлые расчёты (Plan) для выбранной игры
        private volatile bool initialVerifyRunning = false;

        // Разрешение на тяжёлые проверки файлов (Plan/Execute). На старте запрещено, включаем после первичного рендеринга
        private volatile bool allowFileChecks = false;

        // Единая кнопка действия: режим и флаги
        private enum ActionMode {
            Checking,
            Install,
            Update,
            Play,
            Cancel,
            Retry
        }

        // ===== Feedback model and queue =====
        private record FeedbackDraft(string Name, string Contact, string Type, string Comment, bool AttachLogs, Dictionary<string, string>? System);
        private List<FeedbackDraft> feedbackQueue = new();
        private DispatcherTimer? feedbackRetryTimer;
        private string FeedbackQueuePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "feedback_queue.json");

        private void FeedbackBtn_Click(object sender, RoutedEventArgs e) {
            try {
                this.FbName.Text = string.Empty;
                this.FbContact.Text = string.Empty;
                this.FbComment.Text = string.Empty;
                this.FbStatus.Text = string.Empty;
                try { this.FbType.SelectedIndex = 0; } catch { }
                this.FeedbackOverlay.Visibility = Visibility.Visible;
                // No pre-validation on open; user will see validation only on Send
            } catch { }
        }

        private void FbCancel_Click(object sender, RoutedEventArgs e) {
            try { this.FeedbackOverlay.Visibility = Visibility.Collapsed; } catch { }
        }

        private async void FbSend_Click(object sender, RoutedEventArgs e) {
            try {
                var name = this.FbName.Text?.Trim() ?? string.Empty;
                var contact = this.FbContact.Text?.Trim() ?? string.Empty;
                var comment = this.FbComment.Text?.Trim() ?? string.Empty;
                var attach = true; // always attach logs per UX change
                var type = GetFeedbackTypeString();
                if (string.IsNullOrWhiteSpace(comment) || comment.Length < 5) { this.FbStatus.Text = "Опишите проблему (мин. 5 символов)"; return; }
                var system = attach ? this.CollectSystemInfo() : null;
                var draft = new FeedbackDraft(name, contact, type, comment, attach, system);
                this.FbStatus.Text = "Отправка...";
                var ok = await this.TrySendFeedbackAsync(draft, silent: false).ConfigureAwait(true);
                if (ok) {
                    this.FbStatus.Text = "Отправлено";
                    this.ShowToast("Спасибо! Сообщение отправлено");
                    this.FeedbackOverlay.Visibility = Visibility.Collapsed;
                } else {
                    EnqueueFeedback(draft);
                    this.FbStatus.Text = "В ожидании отправки (оффлайн)";
                    this.ShowToast("Сообщение поставлено в очередь");
                    this.FeedbackOverlay.Visibility = Visibility.Collapsed;
                }
            } catch (Exception ex) {
                try { Core.Logging.Logger.Error(ex, "Feedback.Send"); } catch { }
                this.ShowToast("Ошибка отправки");
            }
        }

        // Disable live validation: do nothing on each input change
        private void FbField_Changed(object? sender, TextChangedEventArgs e) { /* no-op */ }

        private void UpdateFeedbackValidation()
        {
            try {
                var comment = this.FbComment?.Text ?? string.Empty;
                var isOk = !string.IsNullOrWhiteSpace(comment) && comment.Trim().Length >= 5;

                // pick brushes
                var okBrush = (this.TryFindResource("Brush.Border") as Brush) ?? new SolidColorBrush(Color.FromRgb(64,64,64));
                var errBrush = new SolidColorBrush(Color.FromRgb(255,107,107)); // matches danger accents
                if (this.FbComment != null) this.FbComment.BorderBrush = isOk ? okBrush : errBrush;
                if (this.FbSendBtn != null) this.FbSendBtn.IsEnabled = isOk;
                if (this.FbStatus != null) this.FbStatus.Text = isOk ? string.Empty : "Опишите проблему (мин. 5 символов)";
            } catch { }
        }

        private string GetFeedbackTypeString() {
            try {
                var item = this.FbType.SelectedItem as ComboBoxItem;
                var txt = item?.Content?.ToString()?.Trim()?.ToLowerInvariant() ?? "";
                return txt switch { "баг" => "bug", "идея" => "idea", "вопрос" => "question", _ => "other" };
            } catch { return "other"; }
        }

        private Dictionary<string, string> CollectSystemInfo() {
            var dict = new Dictionary<string, string>();
            try {
                dict["os"] = Environment.OSVersion.VersionString;
                dict["arch"] = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                dict["dotnet"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
                dict["machineName"] = Environment.MachineName;
                dict["appVersion"] = typeof(HomePage).Assembly.GetName().Version?.ToString() ?? "";
            } catch { }
            return dict;
        }

        private async Task<bool> TrySendFeedbackAsync(FeedbackDraft d, bool silent = true) {
            try {
                var baseApi = this.BaseApi.TrimEnd('/');
                var url = baseApi + "/feedback/submit";
                // Global persistent quota (shared with ErrorReporter): limit total reports per window
                try {
                    if (!ChillHub.Core.ErrorReporter.TryConsumeManual(out var retryAfter)) {
                        if (!silent) {
                            var mins = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
                            this.FbStatus.Text = $"Лимит ручных отправок исчерпан. Повторите через ~{mins} мин.";
                            this.ShowToast($"Лимит ручных отправок исчерпан. Повторите через ~{mins} мин.");
                        }
                        return false;
                    }
                } catch { }
                // Build rich diagnostics bundle if requested
                string logsPayload = string.Empty;
                Dictionary<string, string>? extraSystem = d.System;
                if (d.AttachLogs) {
                    try {
                        var bundle = await Task.Run(() => ChillHub.Core.Diagnostics.Build()).ConfigureAwait(true);
                        logsPayload = bundle.LogsMarkdown;
                        // augment system with computed hints
                        extraSystem = extraSystem ?? new Dictionary<string, string>();
                        foreach (var kv in bundle.SystemHints) { extraSystem[kv.Key] = kv.Value; }
                    } catch { /* non-fatal */ }
                }
                using var req = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(JsonSerializer.Serialize(new {
                        name = d.Name,
                        contact = d.Contact,
                        type = d.Type,
                        comment = d.Comment,
                        attachLogs = d.AttachLogs,
                        logs = logsPayload,
                        system = extraSystem,
                    }), Encoding.UTF8, "application/json")
                };
                HttpResponseMessage res;
                try {
                    res = await this.http.SendAsync(req).ConfigureAwait(false);
                } catch (Exception exSend) {
                    try { Core.Logging.Logger.Error(exSend, "Feedback.Send.HttpError"); } catch { }
                    if (!silent) { this.ShowToast("Не удалось отправить (сеть/сервер недоступны)"); }
                    // Local-dev fallback: if BaseApi is localhost and network failed, try admin port 55777
                    if (TryBuildLocalAdminUrl(baseApi, out var adminUrl)) {
                        try {
                            using var req2 = new HttpRequestMessage(HttpMethod.Post, adminUrl) { Content = req.Content };
                            var res2 = await this.http.SendAsync(req2).ConfigureAwait(false);
                            if (res2.IsSuccessStatusCode) return true;
                        } catch (Exception exSend2) { try { Core.Logging.Logger.Error(exSend2, "Feedback.Send.HttpError.Fallback"); } catch { } }
                    }
                    return false;
                }
                if (res.IsSuccessStatusCode) {
                    return true;
                }
                // capture body snippet for diagnostics
                string body = string.Empty;
                try { body = await res.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                try { Core.Logging.Logger.Warn($"Feedback.Send failed: {(int)res.StatusCode} {res.ReasonPhrase}; body='{body}'"); } catch { }
                // Local-dev fallback for wrong port (e.g., 404 on API port): retry against admin port 55777
                if (TryBuildLocalAdminUrl(baseApi, out var adminUrl2)) {
                    try {
                        using var req3 = new HttpRequestMessage(HttpMethod.Post, adminUrl2) { Content = req.Content };
                        var res3 = await this.http.SendAsync(req3).ConfigureAwait(false);
                        if (res3.IsSuccessStatusCode) return true;
                        try {
                            var b3 = await res3.Content.ReadAsStringAsync().ConfigureAwait(false);
                            Core.Logging.Logger.Warn($"Feedback.Send fallback failed: {(int)res3.StatusCode} {res3.ReasonPhrase}; body='{b3}'");
                        } catch { }
                    } catch (Exception exSend3) { try { Core.Logging.Logger.Error(exSend3, "Feedback.Send.FallbackUnexpected"); } catch { } }
                }
                if (!silent) { this.ShowToast($"Сервер отклонил отправку: {(int)res.StatusCode}"); }
                return false;
            } catch (Exception ex) {
                try { Core.Logging.Logger.Error(ex, "Feedback.Send.Unexpected"); } catch { }
                if (!silent) { this.ShowToast("Ошибка отправки"); }
                return false;
            }
        }

        private static bool TryBuildLocalAdminUrl(string baseApi, out string adminUrl)
        {
            adminUrl = string.Empty;
            try {
                if (!Uri.TryCreate(baseApi, UriKind.Absolute, out var u)) return false;
                var host = (u.Host ?? "").ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1") {
                    var ub = new UriBuilder(u);
                    ub.Port = 55777; // admin in local dev
                    adminUrl = new Uri(ub.Uri, "/feedback/submit").ToString();
                    return true;
                }
            } catch { }
            return false;
        }

        private void EnqueueFeedback(FeedbackDraft d) {
            try {
                this.feedbackQueue.Add(d);
                this.SaveFeedbackQueue();
                _ = this.FlushFeedbackQueueNowAsync();
            } catch { }
        }

        private void LoadFeedbackQueue() {
            try {
                var p = this.FeedbackQueuePath;
                var dir = System.IO.Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(p)) { this.feedbackQueue = new List<FeedbackDraft>(); return; }
                var json = File.ReadAllText(p, Encoding.UTF8);
                var items = JsonSerializer.Deserialize<List<FeedbackDraft>>(json) ?? new List<FeedbackDraft>();
                this.feedbackQueue = items;
            } catch { this.feedbackQueue = new List<FeedbackDraft>(); }
        }

        private void SaveFeedbackQueue() {
            try {
                var p = this.FeedbackQueuePath;
                var dir = System.IO.Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this.feedbackQueue, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(p, json, Encoding.UTF8);
            } catch { }
        }

        private async Task FlushFeedbackQueueNowAsync() {
            try {
                if (this.feedbackQueue.Count == 0) return;
                int i = 0; int sent = 0;
                while (i < this.feedbackQueue.Count && sent < 5) {
                    var d = this.feedbackQueue[i];
                    // quick immediate retry for robustness
                    var ok = await this.TrySendFeedbackAsync(d, silent: true).ConfigureAwait(true);
                    if (!ok) {
                        // do a short backoff retry once
                        try { await Task.Delay(800).ConfigureAwait(true); } catch { }
                        ok = await this.TrySendFeedbackAsync(d, silent: true).ConfigureAwait(true);
                    }
                    if (ok) { this.feedbackQueue.RemoveAt(i); sent++; }
                    else { i++; }
                }
                if (sent > 0) {
                    this.SaveFeedbackQueue();
                    try {
                        var msg = sent == 1 ? "Одно отложенное сообщение отправлено" : $"Отправлены отложенные сообщения: {sent}";
                        this.ShowToast(msg);
                    } catch { }
                }
            } catch { }
        }

        private void StartFeedbackRetryLoop() {
            try {
                this.feedbackRetryTimer?.Stop();
                this.feedbackRetryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
                this.feedbackRetryTimer.Tick += async (s, e) => {
                    await this.FlushFeedbackQueueNowAsync();
                };
                this.feedbackRetryTimer.Start();
            } catch { }
        }
        private ActionMode actionMode = ActionMode.Checking;
        private bool hasUpdateError = false;

        // Feedback retry loop timer reference (declared later near queue as well)
        // kept here only if needed; actual implementation declared below

        public HomePage() {
            this.InitializeComponent();

            // Самообновление обрабатывается отдельным окном UpdateWindow до показа MainWindow
            _ = this.StartupAsync();

            // Инициализация состояния единой кнопки действий
            try {
                this.UpdateActionButtonState();
            }
            catch {
            }

            // Init feedback retry loop and load queued items
            try { this.LoadFeedbackQueue(); this.StartFeedbackRetryLoop(); } catch { }
            // Do not disable Send by default; validation is performed on click
            try { if (this.FbSendBtn != null) this.FbSendBtn.IsEnabled = true; } catch { }

            // Handle ESC to close feedback overlay with confirmation
            try { this.PreviewKeyDown += HomePage_PreviewKeyDown; } catch { }

            // Subscribe to auto error reports to show a toast banner
            try {
                ChillHub.Core.ErrorReporter.AutoReported += (ctx) => {
                    try { _ = this.DispatcherInvokeAsync(() => this.ShowToast("Произошла ошибка. Отчёт автоматически отправлен")); } catch { }
                };
                ChillHub.Core.ErrorReporter.AutoReportSuppressed += (ts) => {
                    try {
                        var mins = Math.Max(1, (int)Math.Ceiling(ts.TotalMinutes));
                        _ = this.DispatcherInvokeAsync(() => this.ShowToast($"Лимит авто-репортов исчерпан. Доступно через ~{mins} мин."));
                    } catch { }
                };
            } catch { }
        }

        private void HomePage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try {
                if (e.Key == Key.Escape && this.FeedbackOverlay != null && this.FeedbackOverlay.Visibility == Visibility.Visible) {
                    e.Handled = true;
                    var res = MessageBox.Show("Закрыть форму обратной связи? Введённый текст будет сохранён только если вы отправите его.",
                        "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes) {
                        this.FeedbackOverlay.Visibility = Visibility.Collapsed;
                    }
                }
            } catch { }
        }

        // Toast helper: show non-intrusive notification in bottom-right corner with smooth animations
        private System.Threading.CancellationTokenSource? toastCts;
        private bool toastInit;

        private void EnsureToastTransform() {
            try {
                if (!toastInit) {
                    if (this.Toast.RenderTransform is not System.Windows.Media.TranslateTransform) {
                        this.Toast.RenderTransform = new System.Windows.Media.TranslateTransform(0, 20);
                    }
                    this.Toast.Opacity = 0;
                    this.Toast.Visibility = Visibility.Collapsed;
                    toastInit = true;
                }
            } catch { }
        }

        private void ShowToast(string message, TimeSpan? duration = null) {
            EnsureToastTransform();
            var dur = duration ?? TimeSpan.FromSeconds(3);

            // cancel previous animation if any (overwrite support)
            try { toastCts?.Cancel(); } catch { }
            toastCts = new System.Threading.CancellationTokenSource();
            var ct = toastCts.Token;

            async void Run()
            {
                try {
                    // If currently visible, animate out quickly before showing new text (overwrite behavior)
                    if (this.Toast.Visibility == Visibility.Visible && this.Toast.Opacity > 0.1) {
                        await AnimateToastAsync(fadeIn: false, TimeSpan.FromMilliseconds(140), ct);
                    }

                    this.ToastText.Text = message;
                    this.Toast.Visibility = Visibility.Visible;
                    // animate in
                    await AnimateToastAsync(fadeIn: true, TimeSpan.FromMilliseconds(200), ct);

                    // stay visible for duration
                    try { await Task.Delay(dur, ct).ConfigureAwait(true); } catch { }
                    if (ct.IsCancellationRequested) return;

                    // animate out
                    await AnimateToastAsync(fadeIn: false, TimeSpan.FromMilliseconds(220), ct);
                    if (!ct.IsCancellationRequested) {
                        this.Toast.Visibility = Visibility.Collapsed;
                    }
                } catch { }
            }

            Run();
        }

        private Task AnimateToastAsync(bool fadeIn, TimeSpan duration, System.Threading.CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            try {
                var translate = this.Toast.RenderTransform as System.Windows.Media.TranslateTransform;
                if (translate == null) {
                    translate = new System.Windows.Media.TranslateTransform(0, 0);
                    this.Toast.RenderTransform = translate;
                }

                // prepare animations
                var animOpacity = new System.Windows.Media.Animation.DoubleAnimation {
                    From = fadeIn ? (double?)0.0 : this.Toast.Opacity,
                    To = fadeIn ? 1.0 : 0.0,
                    Duration = new Duration(duration),
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                };
                var fromY = translate.Y;
                var animY = new System.Windows.Media.Animation.DoubleAnimation {
                    From = fadeIn ? (double?)20.0 : fromY,
                    To = fadeIn ? 0.0 : 10.0,
                    Duration = new Duration(duration),
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                };

                int completed = 0;
                void checkDone() { if (++completed >= 2) { tcs.TrySetResult(true); } }

                animOpacity.Completed += (s, e) => {
                    try { this.Toast.Opacity = fadeIn ? 1.0 : 0.0; } catch { }
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct); else checkDone();
                };
                animY.Completed += (s, e) => {
                    try { translate.Y = fadeIn ? 0.0 : 10.0; } catch { }
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct); else checkDone();
                };

                this.Toast.BeginAnimation(System.Windows.UIElement.OpacityProperty, animOpacity);
                translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, animY);

                if (ct.CanBeCanceled) {
                    ct.Register(() => {
                        try {
                            this.Toast.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
                            translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                        } catch { }
                        tcs.TrySetCanceled();
                    });
                }
            } catch (Exception ex) {
                // fallback: no animation
                try {
                    this.Toast.Opacity = fadeIn ? 1.0 : 0.0;
                    if (!fadeIn) this.Toast.Visibility = Visibility.Collapsed;
                } catch { }
                tcs.TrySetException(ex);
            }
            return tcs.Task;
        }

        // Удалено: ручной выбор сборки больше не поддерживается в UI
        private void NormalizeCoverUrls(IEnumerable<NewsItem> items) {
            foreach (var it in items) {
                if (!string.IsNullOrWhiteSpace(it.CoverUrl) && it.CoverUrl.StartsWith("/")) {
                    it.CoverUrl = this.BaseApi + it.CoverUrl;
                }
            }
        }

        private async Task StartupAsync() {
            try {
                // Give UI a chance to render before heavy async work
                await Task.Yield();

                // Самообновление проверяется в UpdateWindow. Здесь не блокируем UI: запускаем загрузку в фоне
                _ = this.LoadInitialAsync();
            }
            catch (Exception ex) {
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.StartupAsync");
                }
                catch {
                }
            }
        }

        private void DisableMainUi() {
            try {
                this.ActionBtn.IsEnabled = false;
                this.GameList.IsEnabled = false;
            }
            catch {
            }
        }

        // Удалена legacy-проверка самообновления: ею занимается UpdateWindow
        private async Task LoadInitialAsync() {
            try {
                // Показ скелетонов по секциям: Игры видимые, список скрыт до загрузки
                try {
                    this.GamesSkeleton.Visibility = System.Windows.Visibility.Visible;
                }
                catch {
                }
                try {
                    this.GameList.Visibility = System.Windows.Visibility.Collapsed;
                }
                catch {
                }

                // Проверка доступа к папке для игр и предложение выбрать другую при отсутствии прав
                try {
                    this.EnsureGamesPathAccessibleOrPrompt();
                }
                catch {
                }

                // Быстрая параллельная загрузка игр и новостей лаунчера
                var gamesUrl = $"{this.BaseApi}/api/games";
                var newsUrl = $"{this.BaseApi}/news/index.json";

                GamesResponse? gamesResp = null;
                NewsIndex? newsResp = null;
                try {
                    gamesResp = await this.http.GetFromJsonAsync<GamesResponse>(gamesUrl).ConfigureAwait(false);
                }
                catch {
                }
                try {
                    newsResp = await this.http.GetFromJsonAsync<NewsIndex>(newsUrl).ConfigureAwait(false);
                }
                catch {
                }

                var games = gamesResp?.Items ?? new List<GameInfo>();

                // Нормализация URL и локального состояния до биндинга в UI
                try {
                    this.NormalizeGameIconsAndLocalState(games);
                }
                catch {
                }

                // Сортировка: установленные сначала, затем порядок из полученного списка
                var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < games.Count; i++) {
                    var id = games[i]?.GameId ?? string.Empty;
                    if (!orderMap.ContainsKey(id)) {
                        orderMap[id] = i;
                    }
                }

                var ordered = games
                    .OrderBy(g => g.IsInstalled ? 0 : 1)
                    .ThenBy(g => orderMap.TryGetValue(g.GameId ?? string.Empty, out var idx) ? idx : int.MaxValue)
                    .ToList();

                await this.DispatcherInvokeAsync(() => {
                    try {
                        this.games = ordered;
                        this.GameList.ItemsSource = this.games;

                        // Выбор при старте: последняя запущенная, иначе первая установленная, иначе первая
                        if (this.games.Count > 0) {
                            var lastId = ChillHub.Core.ConfigService.Current.LastGameId;
                            int idx = -1;
                            if (!string.IsNullOrWhiteSpace(lastId)) {
                                idx = this.games.FindIndex(g => string.Equals(g.GameId, lastId, StringComparison.OrdinalIgnoreCase));
                            }

                            if (idx < 0) {
                                idx = this.games.FindIndex(g => g.IsInstalled);
                            }

                            if (idx < 0) {
                                idx = 0;
                            }

                            this.GameList.SelectedIndex = idx;
                        }

                        // Скелетоны -> список
                        try {
                            this.GamesSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                        }
                        catch {
                        }
                        try {
                            this.GameList.Visibility = System.Windows.Visibility.Visible;
                        }
                        catch {
                        }
                        try {
                            this.UpdateActionButtonState();
                        }
                        catch {
                        }
                    }
                    catch {
                    }
                });

                // Новости лаунчера
                var launcherNews = newsResp?.Items ?? new List<NewsItem>();
                this.NormalizeCoverUrls(launcherNews);
                await this.DispatcherInvokeAsync(() => {
                    try {
                        this.LauncherNewsList.ItemsSource = launcherNews;
                        this.LauncherNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                        this.LauncherNewsList.Visibility = System.Windows.Visibility.Visible;
                    }
                    catch {
                    }
                });

                // Загрузка сборок и новостей выбранной игры (легковесно для UI)
                var gid0 = this.GetSelectedGameId();
                if (!string.IsNullOrWhiteSpace(gid0)) {
                    await this.LoadBuildsAndGameNewsAsync(gid0);
                }

                // После первичного рендеринга — разрешаем тяжёлые проверки и запускаем в фоне
                this.allowFileChecks = true;
                this.initialVerifyRunning = true;

                // Сразу обновим состояние кнопки: показать "Проверка…" на время первичной проверки
                try {
                    await this.DispatcherInvokeAsync(() => {
                        try {
                            this.UpdateActionButtonState();
                        }
                        catch {
                        }
                    });
                }
                catch {
                }
                _ = Task.Run(async () => {
                    await this.VerifyAllGamesStatusesAsync();
                    this.initialVerifyRunning = false;
                    try {
                        var gid = this.GetSelectedGameId();
                        if (!string.IsNullOrWhiteSpace(gid)) {
                            await this.DispatcherInvokeAsync(() => this.UpdateSpaceHintFromCache(gid));
                            await this.DispatcherInvokeAsync(() => {
                                try {
                                    this.UpdateActionButtonState();
                                }
                                catch {
                                }
                            });
                        }
                    }
                    catch {
                    }
                });
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Ошибка загрузки данных (GET {this.BaseApi}/api/games, /news/index.json): {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.LoadInitialAsync");
                }
                catch {
                }
            }
        }

        // --- Фактическая проверка статуса игры по манифесту (полное сравнение) ---
        private async Task VerifyAllGamesStatusesAsync() {
            try {
                if (this.games == null || this.games.Count == 0) {
                    return;
                }

                await this.DispatcherInvokeAsync(() => {
                    try {
                        this.GamesVerifyIndicator.Visibility = Visibility.Visible;
                    }
                    catch {
                    }
                });

                // Лёгкий прогресс: processed/total в StatusText, чтобы пользователь видел процесс
                int total = this.games.Count;
                int processed = 0;
                var sem = new SemaphoreSlim(3); // параллельность проверки = 3

                var lastUi = System.Diagnostics.Stopwatch.StartNew();

                // Явно покажем старт проверки
                try {
                    await this.DispatcherInvokeAsync(() => {
                        try {
                            this.StatusText.Text = $"Проверка игр: {processed}/{total}";
                        }
                        catch {
                        }
                    });
                }
                catch {
                }
                var tasks = new List<Task>();
                foreach (var g in this.games) {
                    await sem.WaitAsync();
                    var task = Task.Run(async () => {
                        try {
                            await this.VerifyGameStatusAsync(g);
                        }
                        catch (Exception ex) {
                            try {
                                Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync({g.GameId})");
                            }
                            catch {
                            }
                        }
                        finally {
                            Interlocked.Increment(ref processed);
                            try {
                                // Обновляем текст прогресса не слишком часто (не чаще ~5 раз/сек)
                                if (lastUi.ElapsedMilliseconds >= 200) {
                                    lastUi.Restart();
                                    await this.DispatcherInvokeAsync(() => {
                                        try {
                                            this.StatusText.Text = $"Проверка игр: {processed}/{total}";
                                        }
                                        catch {
                                        }
                                    });
                                }
                            }
                            catch {
                            }
                            sem.Release();
                        }
                    });
                    tasks.Add(task);
                }

                // не блокируем UI-поток, но ждём в фоне
                await Task.WhenAll(tasks);

                // После завершения всех — освежим список с приоритетом установленных
                try {
                    var selectedId = this.GetSelectedGameId();

                    // Preserve registry order for non-installed, keep installed first
                    var order2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < this.games.Count; i++) {
                        var id = this.games[i]?.GameId ?? string.Empty;
                        if (!order2.ContainsKey(id)) {
                            order2[id] = i;
                        }
                    }

                    this.games = this.games
                        .OrderBy(x => x.IsInstalled ? 0 : 1)
                        .ThenBy(x => order2.TryGetValue(x.GameId ?? string.Empty, out var idx) ? idx : int.MaxValue)
                        .ToList();
                    await this.DispatcherInvokeAsync(() => {
                        this.GameList.ItemsSource = this.games; this.GameList.Items.Refresh();
                        if (!string.IsNullOrWhiteSpace(selectedId)) {
                            var idx = this.games.FindIndex(x => x.GameId == selectedId);
                            if (idx >= 0) {
                                this.GameList.SelectedIndex = idx;
                            }
                        }
                    });
                }
                catch {
                }
            }
            catch {
            }
            finally {
                // Safety: ensure we always clear the initial verification flag even if outer callers fail to do so
                this.initialVerifyRunning = false;
                await this.DispatcherInvokeAsync(() => {
                    try {
                        this.GamesVerifyIndicator.Visibility = Visibility.Collapsed;
                    }
                    catch {
                    }

                    // После завершения всегда выставляем финальный статус, чтобы не зависало "Проверка игр X/Y"
                    try {
                        this.StatusText.Text = "Готов";
                    }
                    catch {
                    }
                    try {
                        this.UpdateActionButtonState();
                    }
                    catch {
                    }
                });
            }
        }

        private async Task VerifyGameStatusAsync(GameInfo game) {
            if (game == null) {
                return;
            }

            try {
                // Если нет latest версии или идентификатора — определим по наличию локальных файлов
                if (string.IsNullOrWhiteSpace(game.GameId)) {
                    return;
                }

                var gid = game.GameId;
                var latest = game.LatestVersion;
                var hasLatest = !string.IsNullOrWhiteSpace(latest);
                var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);
                var hasLocalFiles = HasAnyLocalGameFiles(localRoot);

                if (!hasLatest) {
                    // Нет эталона для сравнения — считаем не установленной, если нет локальных файлов; иначе установленной без статуса обновления
                    game.IsInstalled = hasLocalFiles;
                    game.NeedsUpdate = false; // нет способа сравнить
                    try {
                        Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} latest=<none> hasLocalFiles={hasLocalFiles} -> IsInstalled={game.IsInstalled} NeedsUpdate={game.NeedsUpdate}");
                    }
                    catch {
                    }

                    // Отложим Refresh до завершения всех проверок, чтобы не трясти UI на каждую игру
                    return;
                }

                // Получаем манифест latest и план сравнения
                var manifestUrl = $"{this.BaseApi}/manifests/{gid}/{latest}.json";
                try {
                    Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} fetching manifest {manifestUrl}");
                }
                catch {
                }
                var manifest = await this.sync.GetManifestAsync(manifestUrl, CancellationToken.None);
                var contentBase = $"{this.BaseApi}/content/{gid}/{latest}/files";
                var plan = await this.sync.PlanAsync(manifest, localRoot, contentBase, CancellationToken.None);
                try {
                    Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} plan: downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count} emptyDirs={plan.EmptyDirsToCreate.Count}");
                }
                catch {
                }
                try {
                    LogPlanDownloads(gid, "verify", plan, localRoot);
                }
                catch {
                }

                // Обновим кэш требуемого объёма скачивания
                try {
                    lock (this.spaceCacheLock) {
                        this.neededBytesCache[gid] = plan.TotalDownloadBytes;
                    }
                }
                catch {
                }

                // Для статуса учитываем только недостающие/изменённые файлы.
                // Удаления (лишние локальные файлы, например логи/кэш) не считаем признаком "требуется обновление".
                var upToDate = plan.Downloads.Count == 0;
                if (!hasLocalFiles) {
                    // Пустая локальная папка — как не установлено, даже если план пуст (маловероятно)
                    game.IsInstalled = false;
                    game.NeedsUpdate = false;
                }
                else if (upToDate) {
                    game.IsInstalled = true;
                    game.NeedsUpdate = false;
                }
                else {
                    game.IsInstalled = true;
                    game.NeedsUpdate = true;
                }

                try {
                    Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} result: IsInstalled={game.IsInstalled} NeedsUpdate={game.NeedsUpdate}");
                }
                catch {
                }

                // Отложим Refresh до завершения всех проверок
            }
            catch (Exception ex) {
                // В случае ошибки проверки — не меняем текущий статус, только логируем
                try {
                    Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync({game?.GameId})");
                }
                catch {
                }
            }
        }

        private static bool HasAnyLocalGameFiles(string localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot)) {
                    return false;
                }

                foreach (var path in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)) {
                    var rel = Path.GetRelativePath(localRoot, path).Replace('\\', '/');
                    if (rel.StartsWith(".staging/", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (string.Equals(rel, ".version", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    return true; // нашли хотя бы один полезный файл
                }
            }
            catch {
            }
            return false;
        }

        private Task DispatcherInvokeAsync(Action action) {
            try {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) {
                    action();
                    return Task.CompletedTask;
                }
                var tcs = new TaskCompletionSource<object?>();
                dispatcher.BeginInvoke(new Action(() => {
                    try {
                        action();
                        tcs.TrySetResult(null);
                    }
                    catch (Exception ex) {
                        tcs.TrySetException(ex);
                    }
                }));
                return tcs.Task;
            }
            catch {
                action();
                return Task.CompletedTask;
            }
        }

        // Обновляет заголовок секции новостей игры: "Новости (название игры)"
        private void UpdateGameNewsHeader() {
            try {
                var title = "Новости игры";
                var gid = this.GetSelectedGameId();
                if (!string.IsNullOrWhiteSpace(gid)) {
                    var game = this.games?.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
                    var gtitle = game?.Title;
                    if (!string.IsNullOrWhiteSpace(gtitle)) {
                        title = $"Новости {gtitle}";
                    }
                }

                try {
                    this.GameNewsHeader.Text = title;
                }
                catch {
                }
            }
            catch {
            }
        }

        // Проверяет, доступна ли для записи папка с играми. Если нет прав (например, D:\\Games\\ChillHub под ограниченной учётной записью),
        // предлагает пользователю выбрать другую папку и сохраняет выбор в конфиг.
        private bool EnsureGamesPathAccessibleOrPrompt() {
            try {
                var cfg = ConfigService.Current;
                var path = cfg.GamesPath;
                if (string.IsNullOrWhiteSpace(path)) {
                    path = AppConfig.DefaultGamesPath();
                }

                // Попробуем создать папку и временный файл для проверки записи
                try {
                    Directory.CreateDirectory(path);
                }
                catch {
                }
                var testFile = System.IO.Path.Combine(path, ".write_test.tmp");
                try {
                    using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write, FileShare.None)) {
                        fs.WriteByte(0);
                    }

                    try {
                        File.Delete(testFile);
                    }
                    catch {
                    }
                    return true; // доступ есть
                }
                catch (UnauthorizedAccessException) {
                    // Нет прав: предложим выбрать другую папку
                }
                catch (IOException ioex) {
                    // Некоторые IO ошибки тоже могут означать запрет записи (например, Access denied)
                    var msg = ioex.Message ?? string.Empty;
                    if (!msg.Contains("доступ", StringComparison.OrdinalIgnoreCase) &&
                        !msg.Contains("access", StringComparison.OrdinalIgnoreCase)) {
                        // Не похоже на отказ в доступе — не беспокоим пользователя
                        return true;
                    }

                    // Иначе упадём в диалог выбора
                }

                var currentPath = path;
                var question = $"Нет доступа к папке для игр:\n{currentPath}\n\nВыбрать другую папку сейчас?";
                var res = MessageBox.Show(question, "Нет доступа", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res == MessageBoxResult.Yes) {
                    try {
                        using (var dlg = new System.Windows.Forms.FolderBrowserDialog()) {
                            dlg.Description = "Выберите папку для игр";
                            dlg.ShowNewFolderButton = true;
                            dlg.SelectedPath = AppConfig.DefaultGamesPath();
                            var dres = dlg.ShowDialog();
                            if (dres == System.Windows.Forms.DialogResult.OK) {
                                var newPath = dlg.SelectedPath;
                                try {
                                    Directory.CreateDirectory(newPath);
                                }
                                catch {
                                }

                                // Повторная быстрая проверка записи
                                var test2 = System.IO.Path.Combine(newPath, ".write_test.tmp");
                                try {
                                    using (var fs = new FileStream(test2, FileMode.Create, FileAccess.Write, FileShare.None)) {
                                        fs.WriteByte(0);
                                    }

                                    try {
                                        File.Delete(test2);
                                    }
                                    catch {
                                    }
                                    cfg.GamesPath = newPath;
                                    ConfigService.Save(cfg);
                                    return true;
                                }
                                catch {
                                    MessageBox.Show($"Нет доступа к выбранной папке: {newPath}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                                }
                            }
                        }
                    }
                    catch {
                    }
                }

                return false;
            }
            catch {
                return true;
            }
        }

        private async Task LoadBuildsAndGameNewsAsync(string gameId) {
            try {
                // Очистим новости игры сразу, чтобы не мигали новости другой игры
                this.GameNewsList.ItemsSource = Array.Empty<NewsItem>();
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.GameNewsList.Visibility = System.Windows.Visibility.Collapsed;

                // Сборки
                var buildsUrl = $"{this.BaseApi}/api/games/{gameId}/builds";
                var buildsResp = await this.http.GetFromJsonAsync<BuildsResponse>(buildsUrl);
                this.builds = buildsResp?.Items ?? new List<string>();

                // Обновим локальные поля, но не трогаем NeedsUpdate здесь — его ставит проверка по манифесту
                var game = this.games.FirstOrDefault(g => g.GameId == gameId);

                // Чтение версии с диска выполняем в фоновом потоке
                var localVer = await Task.Run(() => this.ReadLocalVersion(gameId));
                var localTrimmed = string.IsNullOrWhiteSpace(localVer) ? string.Empty : localVer.Trim();
                try {
                    Core.Logging.Logger.Info($"LoadBuildsAndGameNewsAsync gid={gameId} local='{localTrimmed}'");
                }
                catch {
                }
                if (game != null) {
                    game.IsInstalled = !string.IsNullOrWhiteSpace(localTrimmed);
                    game.InstalledVersion = localTrimmed ?? string.Empty;
                }

                this.GameList.Items.Refresh();

                // Новости игры
                var gameNewsUrl = $"{this.BaseApi}/news/games/{gameId}/index.json";
                var gameNews = await this.http.GetFromJsonAsync<NewsIndex>(gameNewsUrl);
                var items = gameNews?.Items ?? new List<NewsItem>();
                this.NormalizeCoverUrls(items);
                this.GameNewsList.ItemsSource = items;
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsList.Visibility = System.Windows.Visibility.Visible;

                // После загрузки — обновим заголовок (на случай, если он ещё не обновлён)
                try {
                    this.UpdateGameNewsHeader();
                }
                catch {
                }
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Ошибка загрузки сборок/новостей игры (GET {this.BaseApi}/api/games/{gameId}/builds, /news/games/{gameId}/index.json): {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.LoadBuildsAndGameNewsAsync");
                }
                catch {
                }

                // В случае ошибки не оставляем старые новости от предыдущей игры
                this.GameNewsList.ItemsSource = Array.Empty<NewsItem>();
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsList.Visibility = System.Windows.Visibility.Visible;

                // Обновим заголовок до дефолтного/актуального
                try {
                    this.UpdateGameNewsHeader();
                }
                catch {
                }
            }
        }

        // Обновление новостей лаунчера по кнопке
        private async void RefreshLauncherNews_Click(object sender, RoutedEventArgs e) {
            await this.ReloadLauncherNewsAsync();
        }

        private async Task ReloadLauncherNewsAsync() {
            try {
                this.LauncherNewsSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.LauncherNewsList.Visibility = System.Windows.Visibility.Collapsed;
                var newsUrl = $"{this.BaseApi}/news/index.json";
                var news = await this.http.GetFromJsonAsync<NewsIndex>(newsUrl);
                var launcherNews = news?.Items ?? new List<NewsItem>();
                this.NormalizeCoverUrls(launcherNews);
                this.LauncherNewsList.ItemsSource = launcherNews;
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Не удалось обновить новости лаунчера: {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.ReloadLauncherNewsAsync");
                }
                catch {
                }
            }
            finally {
                this.LauncherNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.LauncherNewsList.Visibility = System.Windows.Visibility.Visible;
            }
        }

        // Обновление новостей игры по кнопке
        private async void RefreshGameNews_Click(object sender, RoutedEventArgs e) {
            await this.ReloadGameNewsAsync();
        }

        private async Task ReloadGameNewsAsync() {
            if (this.GetSelectedGameId() is not string gid || string.IsNullOrWhiteSpace(gid)) {
                this.StatusText.Text = "Не выбрана игра для обновления новостей";
                return;
            }

            try {
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.GameNewsList.Visibility = System.Windows.Visibility.Collapsed;
                var gameNewsUrl = $"{this.BaseApi}/news/games/{gid}/index.json";
                var gameNews = await this.http.GetFromJsonAsync<NewsIndex>(gameNewsUrl);
                var items = gameNews?.Items ?? new List<NewsItem>();
                this.NormalizeCoverUrls(items);
                this.GameNewsList.ItemsSource = items;
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Не удалось обновить новости игры: {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.ReloadGameNewsAsync");
                }
                catch {
                }
            }
            finally {
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsList.Visibility = System.Windows.Visibility.Visible;
            }
        }

        private async void GameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (this.GetSelectedGameId() is string gid && !string.IsNullOrWhiteSpace(gid)) {
                // Если сейчас не выполняется обновление, сбросим состояние прогресса и статусы
                if (!this.isUpdating) {
                    this.StatusText.Text = "Готов";
                    this.UpdateProgress.IsIndeterminate = false;
                    this.UpdateProgress.Value = 0;
                    this.SpeedEtaText.Text = string.Empty;
                    this.FilesSizeText.Text = string.Empty;
                }

                // Обновим локальный статус выбранной игры для списка
                var g = this.games.FirstOrDefault(x => x.GameId == gid);
                var localVer = await Task.Run(() => this.ReadLocalVersion(gid));
                var localTrimmed = string.IsNullOrWhiteSpace(localVer) ? string.Empty : localVer.Trim();
                if (g != null) {
                    g.IsInstalled = !string.IsNullOrWhiteSpace(localTrimmed);
                    g.InstalledVersion = localTrimmed ?? string.Empty;
                }

                this.GameList.Items.Refresh();

                // Обновим заголовок новостей игры под выбранную игру
                try {
                    this.UpdateGameNewsHeader();
                }
                catch {
                }

                // Показать имеющийся кэш сразу (мгновенно), затем уточнить расчётом
                this.UpdateSpaceHintFromCache(gid);
                await this.LoadBuildsAndGameNewsAsync(gid);

                // На старте запрещаем тяжёлые проверки. Разрешаем только после первичного рендеринга (когда _allowFileChecks = true)
                if (this.allowFileChecks && !this.initialVerifyRunning) {
                    _ = this.UpdateSpaceHintAsync(gid);
                }

                // Всегда обновляем состояние кнопки при смене выбора
                try {
                    this.UpdateActionButtonState();
                }
                catch {
                }
            }
            else {
                if (!this.isUpdating) {
                    this.StatusText.Text = "Готов";
                    this.UpdateProgress.IsIndeterminate = false;
                    this.UpdateProgress.Value = 0;
                    this.SpeedEtaText.Text = string.Empty;
                    this.FilesSizeText.Text = string.Empty;
                }

                // Сброс заголовка при отсутствии выбранной игры
                try {
                    this.UpdateGameNewsHeader();
                }
                catch {
                }
            }
        }

        // Показывает строку вида: "Нужно: <size> (<available> доступно)", если для установки/обновления требуется загрузка
        private async Task UpdateSpaceHintAsync(string gid) {
            try {
                if (this.isUpdating) {
                    return; // не вмешиваемся в активный процесс
                }

                // Если игра установлена и не требует обновления — показываем соответствующее сообщение и выходим
                var g = this.games?.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
                if (g != null && g.IsInstalled && !g.NeedsUpdate) {
                    this.FilesSizeText.Text = "Последняя версия игры уже установлена";
                    return;
                }

                if (string.IsNullOrWhiteSpace(gid)) {
                    return;
                }

                // Сначала попытаемся взять кэш
                long cachedNeed = -1;
                try {
                    lock (this.spaceCacheLock) {
                        if (this.neededBytesCache.TryGetValue(gid, out var v)) {
                            cachedNeed = v;
                        }
                    }
                }
                catch {
                }
                if (cachedNeed >= 0) {
                    long haveFast = 0;
                    try {
                        var localRootFast = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);
                        var rootFast = Path.GetPathRoot(Path.GetFullPath(localRootFast)) ?? localRootFast;
                        var driveFast = new DriveInfo(rootFast);
                        haveFast = driveFast.AvailableFreeSpace;
                    }
                    catch {
                    }
                    if (cachedNeed > 0) {
                        this.FilesSizeText.Text = $"Нужно: {FormatSize(cachedNeed)} ({FormatSize(haveFast)} доступно)";
                    }
                    else {
                        this.FilesSizeText.Text = string.Empty;
                    }

                    return;
                }
                // Определим версию: используем latest из списка игр или первый элемент из _builds
                var game = this.games?.FirstOrDefault(g => g.GameId == gid);
                var version = game?.LatestVersion;
                if (string.IsNullOrWhiteSpace(version)) {
                    if (this.builds != null && this.builds.Count > 0) {
                        version = this.builds[0];
                    }
                }

                if (string.IsNullOrWhiteSpace(version)) {
                    this.FilesSizeText.Text = string.Empty;
                    return;
                }

                var manifestUrl = $"{this.BaseApi}/manifests/{gid}/{version}.json";
                var contentBase = $"{this.BaseApi}/content/{gid}/{version}/files";
                var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);

                var manifest = await this.sync.GetManifestAsync(manifestUrl, CancellationToken.None);
                var plan = await this.sync.PlanAsync(manifest, localRoot, contentBase, CancellationToken.None);

                long need = plan.TotalDownloadBytes;
                try {
                    lock (this.spaceCacheLock) {
                        this.neededBytesCache[gid] = need;
                    }
                }
                catch {
                }
                long have = 0;
                try {
                    var root = Path.GetPathRoot(Path.GetFullPath(localRoot)) ?? localRoot;
                    var drive = new DriveInfo(root);
                    have = drive.AvailableFreeSpace;
                }
                catch {
                }

                if (need > 0) {
                    this.FilesSizeText.Text = $"Нужно: {FormatSize(need)} ({FormatSize(have)} доступно)";
                }
                else {
                    this.FilesSizeText.Text = string.Empty;
                }
            }
            catch {
                // В случае ошибки расчёта не ломаем UI
                try {
                    this.FilesSizeText.Text = string.Empty;
                }
                catch {
                }
            }
        }

        // Обновляет FilesSizeText только из кэша, не выполняя сетевых запросов
        private void UpdateSpaceHintFromCache(string gid) {
            try {
                if (this.isUpdating) {
                    return;
                }

                // Если игра установлена и не требует обновления — показываем соответствующее сообщение и выходим
                var g = this.games?.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
                if (g != null && g.IsInstalled && !g.NeedsUpdate) {
                    this.FilesSizeText.Text = "Последняя версия игры уже установлена";
                    return;
                }

                if (string.IsNullOrWhiteSpace(gid)) {
                    return;
                }

                long need;
                lock (this.spaceCacheLock) {
                    if (!this.neededBytesCache.TryGetValue(gid, out need)) {
                        this.FilesSizeText.Text = string.Empty;
                        return;
                    }
                }

                long have = 0;
                try {
                    var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);
                    var root = Path.GetPathRoot(Path.GetFullPath(localRoot)) ?? localRoot;
                    var drive = new DriveInfo(root);
                    have = drive.AvailableFreeSpace;
                }
                catch {
                }
                this.FilesSizeText.Text = (need > 0) ? $"Нужно: {FormatSize(need)} ({FormatSize(have)} доступно)" : string.Empty;
            }
            catch {
            }
        }

        private void LauncherNewsList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (this.LauncherNewsList.SelectedItem is NewsItem it) {
                try {
                    var url = $"{this.BaseApi}/news/{it.Slug}.md";
                    var win = Window.GetWindow(this) as ChillHub.MainWindow;
                    win?.ContentFrame.Navigate(new NewsDetailPage(it.Title, url));
                }
                finally {
                    this.LauncherNewsList.SelectedItem = null;
                }
            }
        }

        private void GameNewsList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (this.GameNewsList.SelectedItem is NewsItem it && this.GetSelectedGameId() is string gid && !string.IsNullOrWhiteSpace(gid)) {
                try {
                    var url = $"{this.BaseApi}/news/games/{gid}/{it.Slug}.md";
                    var win = Window.GetWindow(this) as ChillHub.MainWindow;
                    win?.ContentFrame.Navigate(new NewsDetailPage(it.Title, url));
                }
                finally {
                    this.GameNewsList.SelectedItem = null;
                }
            }
        }

        private void LauncherNewsReadMore_Click(object sender, RoutedEventArgs e) {
            if ((sender as FrameworkElement)?.DataContext is NewsItem it) {
                var url = $"{this.BaseApi}/news/{it.Slug}.md";
                var win = Window.GetWindow(this) as ChillHub.MainWindow;
                win?.ContentFrame.Navigate(new NewsDetailPage(it.Title, url));
            }
        }

        // Обновление списка игр по кнопке в заголовке секции
        private async void RefreshGames_Click(object sender, RoutedEventArgs e) {
            // Сохраним текущее выделение, чтобы не потерять контекст страницы игры
            var prevSelectedId = this.GetSelectedGameId();
            try {
                try {
                    this.GamesSkeleton.Visibility = Visibility.Visible;
                }
                catch {
                }
                try {
                    this.GameList.Visibility = Visibility.Collapsed;
                }
                catch {
                }

                var gamesUrl = $"{this.BaseApi}/api/games";
                var gamesResp = await this.http.GetFromJsonAsync<GamesResponse>(gamesUrl);
                this.games = gamesResp?.Items ?? new List<GameInfo>();
                this.NormalizeGameIconsAndLocalState(this.games);

                // Sorting: installed first, then by registry/API order received
                var orderR = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < this.games.Count; i++) {
                    var id = this.games[i]?.GameId ?? string.Empty;
                    if (!orderR.ContainsKey(id)) {
                        orderR[id] = i;
                    }
                }

                this.games = this.games
                    .OrderBy(g => g.IsInstalled ? 0 : 1)
                    .ThenBy(g => orderR.TryGetValue(g.GameId ?? string.Empty, out var idx) ? idx : int.MaxValue)
                    .ToList();
                this.GameList.ItemsSource = this.games;

                // Восстановим выбранную игру, если она осталась в списке
                try {
                    if (!string.IsNullOrWhiteSpace(prevSelectedId)) {
                        var idxSel = this.games.FindIndex(g => string.Equals(g.GameId, prevSelectedId, StringComparison.OrdinalIgnoreCase));
                        if (idxSel >= 0) {
                            this.GameList.SelectedIndex = idxSel;
                        }
                    }
                }
                catch {
                }
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Ошибка обновления списка игр: {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.RefreshGames_Click");
                }
                catch {
                }
            }
            finally {
                try {
                    this.GamesSkeleton.Visibility = Visibility.Collapsed;
                }
                catch {
                }
                try {
                    this.GameList.Visibility = Visibility.Visible;
                }
                catch {
                }
                try {
                    this.UpdateActionButtonState();
                }
                catch {
                }
            }

            // Запустить асинхронную проверку статусов по манифесту
            try {
                this.GamesVerifyIndicator.Visibility = Visibility.Visible;
            }
            catch {
            }
            await this.VerifyAllGamesStatusesAsync();

            // После обновления статусов — освежим подсказку по текущей игре из кэша
            try {
                // Если выделение потеряно после верификации — восстановим прежнее
                var gid = this.GetSelectedGameId();
                if (string.IsNullOrWhiteSpace(gid) && !string.IsNullOrWhiteSpace(prevSelectedId)) {
                    var idxSel2 = this.games.FindIndex(g => string.Equals(g.GameId, prevSelectedId, StringComparison.OrdinalIgnoreCase));
                    if (idxSel2 >= 0) {
                        try {
                            this.GameList.SelectedIndex = idxSel2;
                        }
                        catch {
                        }
                        gid = prevSelectedId;
                    }
                }

                if (!string.IsNullOrWhiteSpace(gid)) {
                    // Выполним полный пересчёт требуемого места, чтобы сразу увидеть оценку
                    await this.UpdateSpaceHintAsync(gid);
                    try {
                        this.UpdateActionButtonState();
                    }
                    catch {
                    }
                }
            }
            catch {
            }
        }

        private void GameNewsReadMore_Click(object sender, RoutedEventArgs e) {
            if ((sender as FrameworkElement)?.DataContext is NewsItem it && this.GetSelectedGameId() is string gid && !string.IsNullOrWhiteSpace(gid)) {
                var url = $"{this.BaseApi}/news/games/{gid}/{it.Slug}.md";
                var win = Window.GetWindow(this) as ChillHub.MainWindow;
                win?.ContentFrame.Navigate(new NewsDetailPage(it.Title, url));
            }
        }

        private async void RefreshStatuses_Click(object sender, RoutedEventArgs e) {
            try {
                this.GamesVerifyIndicator.Visibility = Visibility.Visible;
            }
            catch {
            }
            await this.VerifyAllGamesStatusesAsync();
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e) {
            // Открываем страницу настроек в главной рамке окна
            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            win?.ContentFrame.Navigate(new SettingsPage());
        }

        // Theme toggle and icon are now managed in MainWindow header
        private void ActionBtn_Click(object sender, RoutedEventArgs e) {
            // Во время фоновой проверки статусов игр блокируем любые действия (установка/обновление/запуск)
            if (this.initialVerifyRunning) {
                try {
                    this.StatusText.Text = "Идёт проверка игр…";
                    this.UpdateProgress.IsIndeterminate = true;
                    this.SetActionMode(ActionMode.Checking);
                }
                catch {
                }
                return;
            }
            if (this.isUpdating) {
                // В режиме обновления кнопка всегда работает как Отмена
                this.cts?.Cancel();
                return;
            }

            switch (this.actionMode) {
                case ActionMode.Play:
                    this.PlaySelectedGame();
                    break;
                case ActionMode.Install:
                case ActionMode.Update:
                case ActionMode.Retry:
                default:
                    this.cts = new CancellationTokenSource();
                    _ = this.StartUpdateAsync(this.cts.Token);
                    break;
            }
        }

        private async Task StartUpdateAsync(CancellationToken token) {
            try {
                if (this.GetSelectedGameId() is not string gid || string.IsNullOrWhiteSpace(gid)) {
                    this.StatusText.Text = "Не выбрана игра";
                    return;
                }

                // Всегда используем latest; список версий доступен только для просмотра
                var game = this.games.FirstOrDefault(g => g.GameId == gid);
                var version = game?.LatestVersion;
                if (string.IsNullOrWhiteSpace(version)) {
                    // Фолбэк: возьмём первый элемент из списка, если latest неизвестен
                    version = (this.builds != null && this.builds.Count > 0) ? this.builds[0] : null;
                }

                if (string.IsNullOrWhiteSpace(version)) {
                    this.StatusText.Text = "Нет доступных сборок для установки";
                    return;
                }
                try {
                    Core.Logging.Logger.Info($"StartUpdateAsync gid={gid} version={version}");
                }
                catch {
                }

                this.isUpdating = true;
                this.hasUpdateError = false;
                this.SetActionMode(ActionMode.Cancel);
                this.GameList.IsEnabled = false;
                this.UpdateProgress.Value = 0;
                this.FilesSizeText.Text = string.Empty;
                this.SpeedEtaText.Text = string.Empty;
                this.emaSpeedMBs = 0.0;

                var manifestUrl = $"{this.BaseApi}/manifests/{gid}/{version}.json";
                var contentBase = $"{this.BaseApi}/content/{gid}/{version}/files";

                this.StatusText.Text = "Загрузка манифеста...";
                this.UpdateProgress.IsIndeterminate = true;
                this.UpdateProgress.Value = 0;
                this.SpeedEtaText.Text = string.Empty;
                this.FilesSizeText.Text = string.Empty;
                try {
                    Core.Logging.Logger.Info($"StartUpdateAsync fetching manifest {manifestUrl}");
                }
                catch {
                }
                var manifest = await this.sync.GetManifestAsync(manifestUrl, token);
                this.StatusText.Text = "Проверка...";
                this.UpdateProgress.IsIndeterminate = true;
                var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);
                var plan = await this.sync.PlanAsync(manifest, localRoot, contentBase, token);
                try {
                    Core.Logging.Logger.Info($"StartUpdateAsync plan: downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count} emptyDirs={plan.EmptyDirsToCreate.Count}");
                }
                catch {
                }
                try {
                    LogPlanDownloads(gid, "update", plan, localRoot);
                }
                catch {
                }

                // Оценка требуемого места и проверка доступного до скачивания
                try {
                    var root = Path.GetPathRoot(Path.GetFullPath(localRoot)) ?? localRoot;
                    var drive = new DriveInfo(root);
                    var need = plan.TotalDownloadBytes;
                    var have = drive.AvailableFreeSpace;

                    // Показываем оценку места только если требуется скачать что-то
                    if (need > 0) {
                        this.FilesSizeText.Text = $"Нужно: {FormatSize(need)} ({FormatSize(have)} доступно)";
                    }
                    else {
                        this.FilesSizeText.Text = string.Empty; // последняя версия — ничего не показываем
                    }

                    if (need > 0 && have < need) {
                        this.StatusText.Text = "Недостаточно свободного места для обновления.";

                        // Оставляем строку с числами для ясности
                        this.isUpdating = false;
                        this.GameList.IsEnabled = true;
                        this.UpdateProgress.IsIndeterminate = false;
                        this.UpdateProgress.Value = 0;

                        // Обновим подписи/видимость кнопок согласно текущему состоянию
                        try {
                            this.UpdateActionButtonState();
                        }
                        catch {
                        }
                        return;
                    }
                }
                catch {
                }

                // Guard: если игра запущена — блокируем обновление
                game = this.games.FirstOrDefault(g => g.GameId == gid);
                if (game != null && !string.IsNullOrWhiteSpace(game.ExeRelativePath)) {
                    var exeName = System.IO.Path.GetFileNameWithoutExtension(game.ExeRelativePath);
                    if (!string.IsNullOrWhiteSpace(exeName)) {
                        var running = Process.GetProcessesByName(exeName);
                        if (running?.Length > 0) {
                            this.StatusText.Text = $"Игра запущена ({exeName}). Закройте игру перед обновлением.";

                            // Сбросим состояние обновления, чтобы не завис случай отмены
                            this.isUpdating = false;
                            this.GameList.IsEnabled = true;
                            this.UpdateProgress.IsIndeterminate = false;
                            this.UpdateProgress.Value = 0;
                            this.SpeedEtaText.Text = string.Empty;
                            this.FilesSizeText.Text = string.Empty;

                            // Обновим подписи/видимость кнопок согласно текущему состоянию
                            try {
                                this.UpdateActionButtonState();
                            }
                            catch {
                            }
                            return;
                        }
                    }
                }

                var start = DateTime.UtcNow;
                var prog = new Progress<SyncProgress>(p => {
                    // Обновляем статус и режим прогресс-бара в зависимости от стадии
                    switch (p.Stage) {
                        case "Checking":
                            this.StatusText.Text = "Проверка...";
                            this.UpdateProgress.IsIndeterminate = true;
                            this.SpeedEtaText.Text = string.Empty;
                            this.FilesSizeText.Text = string.Empty;
                            break;
                        case "Downloading":
                            this.StatusText.Text = "Скачивание обновления...";
                            this.UpdateProgress.IsIndeterminate = false;
                            if (p.TotalBytes > 0) {
                                this.UpdateProgress.Value = Math.Min(100, Math.Max(0, (p.BytesDownloaded * 100.0) / p.TotalBytes));
                                var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                                var instantSpeed = elapsed > 0 ? (p.BytesDownloaded / 1024.0 / 1024.0) / elapsed : 0; // МБ/с по всем потокам
                                this.emaSpeedMBs = (this.emaSpeedMBs <= 0) ? instantSpeed : ((EmaAlpha * instantSpeed) + ((1 - EmaAlpha) * this.emaSpeedMBs));
                                var remainBytes = p.TotalBytes - p.BytesDownloaded;
                                var etaSec = this.emaSpeedMBs > 0 ? (remainBytes / 1024.0 / 1024.0) / this.emaSpeedMBs : 0;
                                this.SpeedEtaText.Text = $"Скорость: {this.emaSpeedMBs:0.0} МБ/с • Осталось: {etaSec:0}s";
                                this.FilesSizeText.Text = $"{p.FilesDownloaded}/{p.TotalFiles} • {FormatSize(p.BytesDownloaded)}/{FormatSize(p.TotalBytes)}";
                            }

                            break;
                        case "Verifying":
                            // Явно показываем стадию проверки файлов после скачивания
                            this.StatusText.Text = "Проверка файлов...";

                            // Отразим, что скачивание завершено
                            this.UpdateProgress.Value = 100;
                            this.UpdateProgress.IsIndeterminate = true;
                            this.SpeedEtaText.Text = string.Empty;

                            // После скачивания проценты больше не релевантны
                            break;
                        case "Activating":
                            this.StatusText.Text = "Применение обновления...";

                            // Отразим, что скачивание завершено
                            this.UpdateProgress.Value = 100;
                            this.UpdateProgress.IsIndeterminate = true;
                            this.SpeedEtaText.Text = string.Empty;
                            break;
                        case "Completed":
                            // Финальное уведомление от службы синхронизации
                            this.UpdateProgress.IsIndeterminate = false;
                            this.UpdateProgress.Value = 100;
                            this.StatusText.Text = "Готово";
                            this.SpeedEtaText.Text = string.Empty;
                            this.FilesSizeText.Text = "Последняя версия игры уже установлена";
                            break;
                        default:
                            this.StatusText.Text = p.Stage;
                            break;
                    }
                });

                await this.sync.ExecuteAsync(plan, prog, token);
                try {
                    Core.Logging.Logger.Info($"StartUpdateAsync execute done gid={gid} version={version}");
                }
                catch {
                }

                this.StatusText.Text = "Готово";
                this.SpeedEtaText.Text = string.Empty;
                this.FilesSizeText.Text = "Последняя версия игры уже установлена"; // показываем итоговый статус

                // Сохраним версию в локальный маркер и отметим игру установленной
                this.WriteLocalVersion(gid, version);
                this.MarkInstalled(gid, version);

                // Обновим кэш: для установленной последней версии скачивание не требуется
                try {
                    lock (this.spaceCacheLock) {
                        this.neededBytesCache[gid] = 0;
                    }
                }
                catch {
                }
                this.GameList.Items.Refresh();

                // Зафиксируем текущий выбор на UI-потоке
                var selectedIdAfterUpdate = this.GetSelectedGameId();

                // Дополнительно перепроверим статус игры по манифесту и обновим список в фоне,
                // чтобы не блокировать UI-поток сразу после завершения обновления
                _ = Task.Run(async () => {
                    try {
                        await this.VerifyGameStatusAsync(this.games.FirstOrDefault(x => x.GameId == gid) ?? new GameInfo { GameId = gid, LatestVersion = version ?? string.Empty });
                        var reordered = this.games
                            .OrderByDescending(x => x.IsInstalled)
                            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                        await this.DispatcherInvokeAsync(() => {
                            try {
                                this.games = reordered;
                                this.GameList.ItemsSource = this.games; this.GameList.Items.Refresh();
                                if (!string.IsNullOrWhiteSpace(selectedIdAfterUpdate)) {
                                    var idx = this.games.FindIndex(x => x.GameId == selectedIdAfterUpdate);
                                    if (idx >= 0) {
                                        this.GameList.SelectedIndex = idx;
                                    }
                                }

                                // После изменения источника данных — обновим состояние кнопки
                                try {
                                    this.UpdateActionButtonState();
                                }
                                catch {
                                }
                            }
                            catch {
                            }
                        });
                    }
                    catch {
                    }
                });

                // Создание ярлыка: вычислим параметры на UI-потоке, а COM-вызов выполним в STA-потоке
                try {
                    string? shortcutTitle = null; string? shortcutExe = null;
                    var gLocal = this.games.FirstOrDefault(g => g.GameId == gid);
                    if (gLocal != null && !string.IsNullOrWhiteSpace(gLocal.ExeRelativePath)) {
                        var rel = gLocal.ExeRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);
                        var exePath = System.IO.Path.Combine(localRoot, rel);
                        shortcutExe = exePath;
                        shortcutTitle = string.IsNullOrWhiteSpace(gLocal.Title) ? gid : gLocal.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(shortcutExe) && File.Exists(shortcutExe)) {
                        var t = new System.Threading.Thread(() => {
                            try {
                                TryCreateDesktopShortcut(shortcutTitle!, shortcutExe!);
                            }
                            catch {
                            }
                        });
                        t.IsBackground = true;
                        try {
                            t.SetApartmentState(System.Threading.ApartmentState.STA);
                        }
                        catch {
                        }
                        t.Start();
                    }
                }
                catch {
                }
            }
            catch (OperationCanceledException) {
                this.StatusText.Text = "Операция отменена пользователем.";
                this.SpeedEtaText.Text = string.Empty;
                this.UpdateProgress.IsIndeterminate = false;
                this.UpdateProgress.Value = 0;
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Ошибка обновления: {ex.Message}";
                this.hasUpdateError = true;
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.StartUpdateAsync");
                }
                catch {
                }
            }
            finally {
                this.isUpdating = false;
                this.GameList.IsEnabled = true;
                this.UpdateProgress.IsIndeterminate = false;
                this.UpdateProgress.Value = 0;

                // Не очищаем нижний статус при успешном завершении, чтобы показать "Последняя версия игры уже установлена"
                if (this.hasUpdateError) {
                    this.FilesSizeText.Text = string.Empty;
                }
                try {
                    this.UpdateActionButtonState();
                }
                catch {
                }
            }
        }

        // --- Управление видимостью кнопок действий (Обновить/Играть) ---
        private GameInfo? GetSelectedGame() {
            try {
                if (this.GetSelectedGameId() is string gid && !string.IsNullOrWhiteSpace(gid)) {
                    return this.games.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch {
            }
            return null;
        }

        private void SetActionMode(ActionMode mode) {
            this.actionMode = mode;
            try {
                switch (mode) {
                    case ActionMode.Cancel:
                        this.ActionBtn.Content = "Отмена";
                        this.ActionBtn.IsEnabled = true;
                        try {
                            this.ActionBtn.Style = (Style)this.FindResource("Style.ActionButton.Cancel");
                        }
                        catch {
                        }
                        break;
                    case ActionMode.Checking:
                        this.ActionBtn.Content = "Проверка…";
                        this.ActionBtn.IsEnabled = false;
                        try {
                            this.ActionBtn.Style = (Style)this.FindResource("Style.ActionButton.Checking");
                        }
                        catch {
                        }
                        break;
                    case ActionMode.Play:
                        this.ActionBtn.Content = "Играть";
                        this.ActionBtn.IsEnabled = true;
                        try {
                            this.ActionBtn.Style = (Style)this.FindResource("Style.ActionButton.Play");
                        }
                        catch {
                        }
                        break;
                    case ActionMode.Retry:
                        this.ActionBtn.Content = "Повторить";
                        this.ActionBtn.IsEnabled = true;
                        try {
                            this.ActionBtn.Style = (Style)this.FindResource("Style.ActionButton.Retry");
                        }
                        catch {
                        }
                        break;
                    case ActionMode.Install:
                        this.ActionBtn.Content = "Установить";
                        this.ActionBtn.IsEnabled = true;
                        try {
                            this.ActionBtn.Style = (Style)this.FindResource("Style.ActionButton.Install");
                        }
                        catch {
                        }
                        break;
                    case ActionMode.Update:
                    default:
                        this.ActionBtn.Content = "Обновить";
                        this.ActionBtn.IsEnabled = true;
                        try {
                            this.ActionBtn.Style = (Style)this.FindResource("Style.ActionButton.Update");
                        }
                        catch {
                        }
                        break;
                }
            }
            catch {
            }
        }

        private void UpdateActionButtonState() {
            try {
                var g = this.GetSelectedGame();
                var isInstalled = g?.IsInstalled == true;
                var needsUpdate = g?.NeedsUpdate == true;

                if (this.isUpdating) {
                    this.SetActionMode(ActionMode.Cancel);
                    return;
                }

                if (this.initialVerifyRunning) {
                    this.SetActionMode(ActionMode.Checking);
                    return;
                }

                if (this.hasUpdateError) {
                    this.SetActionMode(ActionMode.Retry);
                    return;
                }

                if (isInstalled && !needsUpdate) {
                    this.SetActionMode(ActionMode.Play);
                    return;
                }

                // Не установлена или требует обновления
                this.SetActionMode(isInstalled ? ActionMode.Update : ActionMode.Install);
            }
            catch {
            }
        }

        private void PlaySelectedGame() {
            try {
                if (this.GetSelectedGameId() is not string gid || string.IsNullOrWhiteSpace(gid)) {
                    this.StatusText.Text = "Не выбрана игра";
                    return;
                }

                var game = this.games.FirstOrDefault(g => g.GameId == gid);
                if (game == null) {
                    this.StatusText.Text = "Игра не найдена в списке";
                    return;
                }

                if (string.IsNullOrWhiteSpace(game.ExeRelativePath)) {
                    this.StatusText.Text = "Для игры не указан путь к исполняемому файлу. Настройте его в админ-панели.";
                    return;
                }

                // Запомним последнюю запущенную игру
                var cfg = ChillHub.Core.ConfigService.Current;
                cfg.LastGameId = gid;
                ChillHub.Core.ConfigService.Save(cfg);
                var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);
                var rel = game.ExeRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);
                var exePath = System.IO.Path.Combine(localRoot, rel);
                if (!System.IO.File.Exists(exePath)) {
                    this.StatusText.Text = $"Файл не найден: {exePath}";
                    return;
                }

                var psi = new ProcessStartInfo {
                    FileName = exePath,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? localRoot,
                    UseShellExecute = true,
                };
                Process.Start(psi);
                try {
                    this.UpdateActionButtonState();
                }
                catch {
                }
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Не удалось запустить игру: {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.PlaySelectedGame");
                }
                catch {
                }
            }
        }

        private async Task FakeUpdateAsync(CancellationToken token) {
            try {
                this.isUpdating = true;
                this.SetActionMode(ActionMode.Cancel);
                this.GameList.IsEnabled = false;

                this.StatusText.Text = "Проверка версии...";
                await Task.Delay(500, token);

                this.StatusText.Text = "Скачивание обновления...";
                var totalFiles = 34; // псевдо-кол-во файлов
                long totalBytes = 512L * 1024 * 1024; // 512 МБ для примера
                long downloadedBytes = 0;
                var start = DateTime.UtcNow;
                for (int i = 0; i <= 100; i++) {
                    token.ThrowIfCancellationRequested();
                    this.UpdateProgress.Value = i;
                    downloadedBytes = (long)(totalBytes * (i / 100.0));
                    var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                    var speedMBs = elapsed > 0 ? (downloadedBytes / 1024.0 / 1024.0) / elapsed : 0; // МБ/с
                    var remainBytes = totalBytes - downloadedBytes;
                    var etaSec = speedMBs > 0 ? (remainBytes / 1024.0 / 1024.0) / speedMBs : 0;
                    this.SpeedEtaText.Text = $"Скорость: {speedMBs:0.0} МБ/с • Осталось: {etaSec:0}s";

                    var downloadedFiles = (int)Math.Round(totalFiles * (i / 100.0));
                    this.FilesSizeText.Text = $"{downloadedFiles}/{totalFiles} • {FormatSize(downloadedBytes)}/{FormatSize(totalBytes)}";
                    await Task.Delay(50, token);
                }

                this.StatusText.Text = "Верификация файлов...";
                await Task.Delay(800, token);

                this.StatusText.Text = "Активация версии...";
                await Task.Delay(400, token);

                this.StatusText.Text = "Готово. Установлена последняя версия.";
                this.SpeedEtaText.Text = string.Empty;
            }
            catch (OperationCanceledException) {
                this.StatusText.Text = "Операция отменена пользователем.";
                this.SpeedEtaText.Text = string.Empty;
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Ошибка обновления: {ex.Message}";
            }
            finally {
                this.isUpdating = false;
                this.GameList.IsEnabled = true;
                this.UpdateProgress.Value = 0;
                this.FilesSizeText.Text = string.Empty;
                try {
                    this.UpdateActionButtonState();
                }
                catch {
                }
            }
        }

        private string? GetSelectedGameId() {
            try {
                var gi = this.GameList?.SelectedItem as GameInfo;
                return gi?.GameId;
            }
            catch {
                return null;
            }
        }

        private static void TryCreateDesktopShortcut(string title, string exePath) {
            try {
                if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath)) {
                    return;
                }

                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var name = string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(exePath) : title;
                var linkPath = System.IO.Path.Combine(desktop, SanitizeFileName(name) + ".lnk");

                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) {
                    return;
                }

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(linkPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
                shortcut.Description = name;
                shortcut.IconLocation = exePath + ",0";
                shortcut.Save();
            }
            catch {
            }
        }

        private static string SanitizeFileName(string name) {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var arr = name.ToCharArray();
            for (int i = 0; i < arr.Length; i++) {
                if (Array.IndexOf(invalid, arr[i]) >= 0) {
                    arr[i] = '_';
                }
            }

            var s = new string(arr).Trim();
            return string.IsNullOrEmpty(s) ? "Game" : s;
        }

        private static string FormatSize(long bytes) {
            const double KB = 1024.0;
            const double MB = KB * 1024.0;
            const double GB = MB * 1024.0;
            if (bytes >= (long)GB) {
                return $"{bytes / GB:0.0} ГБ";
            }

            if (bytes >= (long)MB) {
                return $"{bytes / MB:0.0} МБ";
            }

            if (bytes >= (long)KB) {
                return $"{bytes / KB:0.0} КБ";
            }

            return $"{bytes} Б";
        }

        // Diagnostic: log detailed info about files that are planned to download, files to delete, and empty dirs to create
        private static void LogPlanDownloads(string gid, string stage, ChillHub.Core.Sync.DiffPlan plan, string localRoot) {
            try {
                int total = plan.Downloads.Count;
                int limit = Math.Min(total, 10);
                for (int i = 0; i < limit; i++) {
                    var t = plan.Downloads[i];
                    var rel = t.RelativePath;
                    var size = t.Size;
                    var hasSha = !string.IsNullOrWhiteSpace(t.Sha256);
                    var hasB3 = !string.IsNullOrWhiteSpace(t.Blake3);
                    var localPath = System.IO.Path.Combine(localRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    bool exists = System.IO.File.Exists(localPath);
                    long len = 0;
                    if (exists) {
                        try {
                            len = new System.IO.FileInfo(localPath).Length;
                        }
                        catch {
                        }
                    }

                    ChillHub.Core.Logging.Logger.Info($"Plan[{stage}] gid={gid} file='{rel}' size={size} hasSha={hasSha} hasB3={hasB3} localExists={exists} localLen={len}");
                }

                if (total > limit) {
                    ChillHub.Core.Logging.Logger.Info($"Plan[{stage}] gid={gid} ... and {total - limit} more files");
                }

                // Log deletions
                int delTotal = plan.ToDelete.Count;
                int delLimit = Math.Min(delTotal, 10);
                for (int i = 0; i < delLimit; i++) {
                    var rel = plan.ToDelete[i];
                    var path = System.IO.Path.Combine(localRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    bool exists = System.IO.File.Exists(path);
                    ChillHub.Core.Logging.Logger.Info($"Plan[{stage}] gid={gid} toDelete='{rel}' localExists={exists}");
                }

                if (delTotal > delLimit) {
                    ChillHub.Core.Logging.Logger.Info($"Plan[{stage}] gid={gid} ... and {delTotal - delLimit} more deletions");
                }

                // Log empty dirs to create
                int dirTotal = plan.EmptyDirsToCreate.Count;
                int dirLimit = Math.Min(dirTotal, 10);
                for (int i = 0; i < dirLimit; i++) {
                    var rel = plan.EmptyDirsToCreate[i];
                    var path = System.IO.Path.Combine(localRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    bool exists = System.IO.Directory.Exists(path);
                    ChillHub.Core.Logging.Logger.Info($"Plan[{stage}] gid={gid} emptyDir='{rel}' localExists={exists}");
                }

                if (dirTotal > dirLimit) {
                    ChillHub.Core.Logging.Logger.Info($"Plan[{stage}] gid={gid} ... and {dirTotal - dirLimit} more empty dirs");
                }
            }
            catch {
            }
        }

        // Helper: find sibling skeleton placeholder by name under the same parent container
        private static Border? FindImgSkeleton(DependencyObject? parent) {
            try {
                if (parent == null) {
                    return null;
                }

                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++) {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is Border b && b.Name == "ImgSkeleton") {
                        return b;
                    }
                }
            }
            catch {
            }
            return null;
        }

        // HttpClient tuned for many small image fetches in parallel
        private static readonly System.Net.Http.HttpClient s_httpClient =
            new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                UseCookies = true,
#if NET462 || NET48 || NET5_0_OR_GREATER
                MaxConnectionsPerServer = 16,
#endif
            });
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_imgInflight = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> s_imgCache = new System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        private static void DebugLog(string msg)
        {
            try { ChillHub.Core.Logging.Logger.Info(msg); } catch { }
            try { System.Diagnostics.Debug.WriteLine(msg); } catch { }
            try { Console.WriteLine(msg); } catch { }
        }

        private async Task LoadImageAsync(Image img, string url) {
            try {
                // Cache hit: apply immediately
                if (s_imgCache.TryGetValue(url, out var cached)) {
                    DebugLog($"[ImgLoad] cache hit url='{url}'");
                    await img.Dispatcher.InvokeAsync(() => {
                        try { img.Source = cached; img.Visibility = Visibility.Visible; } catch { img.Visibility = Visibility.Visible; }
                    });
                    return;
                }

                // Deduplicate in-flight loads for the same URL
                if (!s_imgInflight.TryAdd(url, 1)) {
                    DebugLog($"[ImgLoad] skip duplicate inflight url='{url}'");
                    return;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                DebugLog($"[ImgLoad] HTTP GET start url='{url}'");
                using var resp = await s_httpClient.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    var ct = resp.Content?.Headers?.ContentType?.ToString() ?? "";
                    DebugLog($"[ImgLoad] HTTP non-200 status={(int)resp.StatusCode} contentType='{ct}' url='{url}'");
                    throw new Exception("HTTP " + (int)resp.StatusCode + " " + resp.StatusCode);
                }
                await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                // Copy to memory to fully detach from HTTP stream before moving to UI thread
                using var ms = new System.IO.MemoryStream();
                await stream.CopyToAsync(ms).ConfigureAwait(false);
                ms.Position = 0;
                sw.Stop();
                DebugLog($"[ImgLoad] HTTP ok bytes={ms.Length} elapsedMs={sw.ElapsedMilliseconds} url='{url}'");
                await img.Dispatcher.InvokeAsync(() => {
                    try {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                        // Decode to roughly the visual size to speed up load and reduce memory
                        try {
                            int targetH = 0;
                            if (img.Height > 0 && !double.IsNaN(img.Height)) targetH = (int)Math.Round(img.Height);
                            // Fallback to desired size if Height is not set
                            if (targetH <= 0 && img.DesiredSize.Height > 0) targetH = (int)Math.Round(img.DesiredSize.Height);
                            if (targetH <= 0) targetH = 88; // common icon height in UI
                            bi.DecodePixelHeight = targetH;
                        } catch { }
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        // Put into cache (frozen, safe to reuse across threads)
                        try { s_imgCache[url] = bi; } catch { }
                        img.Source = bi;
                        img.Visibility = Visibility.Visible;
                        DebugLog($"[ImgLoad] image applied url='{url}'");
                    } catch (Exception ex) { img.Visibility = Visibility.Collapsed; DebugLog($"[ImgLoad] apply error url='{url}' err='{ex.Message}'"); }
                });
            }
            catch (Exception ex) {
                DebugLog($"[ImgLoad] error url='{url}' err='{ex.Message}'");
                await img.Dispatcher.InvokeAsync(() => { img.Visibility = Visibility.Collapsed; });
            }
            finally {
                s_imgInflight.TryRemove(url, out _);
            }
        }

        private void CoverImg_Loaded(object sender, RoutedEventArgs e) {
            if (sender is not Image img) {
                return;
            }

            // 1) Получаем сырой URL из Tag, иначе из DataContext (IconUrl для GameInfo / CoverUrl для NewsItem), иначе из текущего Source.Uri
            string raw = (img.Tag as string) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) {
                try {
                    if (img.DataContext is ChillHub.Core.GameInfo gi) {
                        // GameInfo имеет только IconUrl
                        raw = gi.IconUrl ?? string.Empty;
                    }
                    else if (img.DataContext is ChillHub.Core.NewsItem ni) {
                        // NewsItem использует CoverUrl
                        raw = ni.CoverUrl;
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(raw)) {
                try {
                    if (img.Source is BitmapImage bi && bi.UriSource != null) {
                        raw = bi.UriSource.OriginalString;
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(raw)) {
                img.Visibility = Visibility.Collapsed;
                try {
                    var sk0 = FindImgSkeleton(VisualTreeHelper.GetParent(img));
                    if (sk0 != null) {
                        sk0.Visibility = Visibility.Collapsed;
                    }
                }
                catch {
                }
                return;
            }
            try {
                // Нормализуем URL и жёстко привязываем к origin из BaseApi (scheme+host[:port])
                var apiUri = new Uri(this.BaseApi.TrimEnd('/') + "/", UriKind.Absolute);
                string url;
                DebugLog($"[ImgLoad] raw='{raw}' baseApi='{this.BaseApi}' origin='{apiUri.Scheme}://{apiUri.Authority}'");

                // Поддержка протокол-относительных URL (//host/path)
                if (raw.StartsWith("//")) {
                    url = new Uri(apiUri.Scheme + ":" + raw, UriKind.Absolute).ToString();
                    DebugLog($"[ImgLoad] case='protocol-relative' url='{url}'");
                }
                // Абсолютные URL (http/https)
                else if (Uri.TryCreate(raw, UriKind.Absolute, out var abs)) {
                    url = abs.ToString();
                    DebugLog($"[ImgLoad] case='absolute' url='{url}'");
                }
                else {
                    // Относительные URL: принудительно делаем корневыми к origin
                    // Пример: "manifests/game/icon.png" -> "/manifests/game/icon.png"
                    var rel = raw.StartsWith("/") ? raw : ("/" + raw);
                    url = new Uri(apiUri, rel).ToString();
                    DebugLog($"[ImgLoad] case='relative' rel='{rel}' url='{url}'");
                }

                // Не добавляем динамический cache-busting параметр — это вызывало мигание при переключении игр.
                // Полагайтесь на кеширование и проверку равенства URL ниже, чтобы не перезагружать изображение без надобности.

                DebugLog($"[ImgLoad] resolved url='{url}'");

                // Если уже есть валидный источник с тем же URL — просто показать и скрыть скелетон
                try {
                    if (img.Source is BitmapImage existing && existing.UriSource != null &&
                        string.Equals(existing.UriSource.OriginalString, url, StringComparison.OrdinalIgnoreCase)) {
                        img.Visibility = Visibility.Visible;
                        var parent0 = VisualTreeHelper.GetParent(img);
                        var sk0 = FindImgSkeleton(parent0);
                        if (sk0 != null) {
                            sk0.Visibility = Visibility.Collapsed;
                        }

                        return;
                    }
                }
                catch {
                }

                // Загрузка через HttpClient -> Stream, чтобы избежать проблем с относительными путями/кэшем
                _ = LoadImageAsync(img, url);

                // Скрыть скелетон сразу
                try {
                    var parent = VisualTreeHelper.GetParent(img);
                    var sk = FindImgSkeleton(parent);
                    if (sk != null) {
                        sk.Visibility = Visibility.Collapsed;
                    }
                }
                catch {
                }
            }
            catch {
                img.Visibility = Visibility.Collapsed;
                try {
                    ChillHub.Core.Logging.Logger.Info("[ImgLoad] failed to set image source");
                }
                catch {
                }
                try {
                    var sk = FindImgSkeleton(VisualTreeHelper.GetParent(img));
                    if (sk != null) {
                        sk.Visibility = Visibility.Collapsed;
                    }
                }
                catch {
                }
            }
        }

        private void CoverImg_ImageFailed(object sender, ExceptionRoutedEventArgs e) {
            if (sender is not Image img) {
                return;
            }

            img.Visibility = Visibility.Collapsed;

            // Также скрыть скелетон, чтобы не висел вечно
            try {
                var parent = VisualTreeHelper.GetParent(img);
                var sk = FindImgSkeleton(parent);
                if (sk != null) {
                    sk.Visibility = Visibility.Collapsed;
                }
            }
            catch {
            }
            try {
                ChillHub.Core.Logging.Logger.Info($"[ImgLoad] ImageFailed: {e.ErrorException?.Message}");
            }
            catch {
            }
        }

        // Блокируем контекстное меню для неустановленных игр (или если нет папки игры)
        private void GameItem_ContextMenuOpening(object sender, ContextMenuEventArgs e) {
            try {
                var fe = sender as FrameworkElement;
                var gi = fe?.DataContext as GameInfo;
                var gid = gi?.GameId;
                if (string.IsNullOrWhiteSpace(gid)) {
                    e.Handled = true;
                    return;
                }
                var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);

                // Скрываем контекстное меню, если нет папки или нет содержимых файлов (кроме служебных)
                if (!Directory.Exists(localRoot) || !HasAnyLocalGameFiles(localRoot)) {
                    e.Handled = true;
                }
            }
            catch {
                e.Handled = true;
            }
        }

        // --- Delete confirmation dialog (themed) and helpers ---
        private static string NormalizeDisplayPath(string path) {
            try {
                if (string.IsNullOrWhiteSpace(path)) {
                    return string.Empty;
                }

                var s = path.Replace('\\', '/');
                while (s.Contains("//")) {
                    s = s.Replace("//", "/");
                }

                return s;
            }
            catch {
                return path;
            }
        }

        private bool ShowDeleteConfirmationDialog(string title, string folderPath) {
            try {
                var wnd = new Window {
                    Title = "Удаление локальных файлов",
                    Owner = Application.Current?.MainWindow,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ShowInTaskbar = false,
                    Background = this.TryFindResource("Brush.Surface") as Brush ?? new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                    BorderBrush = this.TryFindResource("Brush.Border") as Brush,
                    BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(16),
                    Foreground = this.TryFindResource("Brush.Text") as Brush ?? Brushes.White,
                };

                try {
                    // Apply themed title bar like main windows
                    wnd.SourceInitialized += (_, __) => {
                        try { Core.UI.AcrylicHelper.ApplyTitleBarTheme(wnd, true); } catch { }
                    };
                } catch { }

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var tb1 = new TextBlock {
                    Text = $"Удалить локальные файлы игры \"{title}\"?",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = this.TryFindResource("Brush.Title") as Brush ?? Brushes.White,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                Grid.SetRow(tb1, 0);

                var normPath = NormalizeDisplayPath(folderPath);
                var tb2 = new TextBlock {
                    Text = $"Будет удалена папка: {normPath}",
                    Foreground = this.TryFindResource("Brush.TextSecondary") as Brush ?? new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                    Margin = new Thickness(0, 0, 0, 16),
                };
                Grid.SetRow(tb2, 1);

                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var cancelBtn = new Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
                var deleteBtn = new Button { Content = "Удалить", MinWidth = 120 };
                try { deleteBtn.Style = (Style)this.FindResource("Style.Button.Primary"); } catch { }
                try { cancelBtn.Style = (Style)this.FindResource("Style.Button.GhostNeutral"); } catch { }
                panel.Children.Add(cancelBtn);
                panel.Children.Add(deleteBtn);
                Grid.SetRow(panel, 2);

                grid.Children.Add(tb1);
                grid.Children.Add(tb2);
                grid.Children.Add(panel);
                wnd.Content = new Border {
                    CornerRadius = new CornerRadius(8),
                    Background = this.TryFindResource("Brush.Surface") as Brush ?? new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                    BorderBrush = this.TryFindResource("Brush.Border") as Brush,
                    BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(12),
                    Child = grid,
                };

                bool result = false;
                cancelBtn.Click += (s, e) => { result = false; wnd.DialogResult = false; };
                deleteBtn.Click += (s, e) => { result = true; wnd.DialogResult = true; };

                wnd.ShowDialog();
                return result;
            }
            catch {
                // Fallback
                var norm = NormalizeDisplayPath(folderPath);
                var res = MessageBox.Show($"Удалить локальные файлы игры \"{title}\"?\nБудет удалена папка: {norm}", "Удаление локальных файлов", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                return res == MessageBoxResult.Yes;
            }
        }

        // DTOs moved to ChillHub.Core.Models

        // --- Local helpers for icons and local installation state ---
        private void NormalizeGameIconsAndLocalState(IEnumerable<GameInfo> games) {
            if (games == null) {
                return;
            }

            foreach (var g in games) {
                try {
                    // Normalize icon URL if server returned a root-relative path
                    if (!string.IsNullOrWhiteSpace(g.IconUrl) && g.IconUrl.StartsWith("/")) {
                        g.IconUrl = this.BaseApi + g.IconUrl;
                    }

                    // Normalize API version string
                    if (!string.IsNullOrWhiteSpace(g.LatestVersion)) {
                        g.LatestVersion = g.LatestVersion.Trim();
                    }

                    // Determine local state from version marker
                    var ver = this.ReadLocalVersion(g.GameId);
                    var verTrimmed = string.IsNullOrWhiteSpace(ver) ? string.Empty : ver.Trim();
                    g.IsInstalled = !string.IsNullOrWhiteSpace(verTrimmed);
                    g.InstalledVersion = verTrimmed ?? string.Empty;

                    // Compute needs update: installed and latest known but different
                    g.NeedsUpdate = g.IsInstalled && !string.IsNullOrWhiteSpace(g.LatestVersion) &&
                                     !string.Equals(g.InstalledVersion?.Trim(), g.LatestVersion?.Trim(), StringComparison.OrdinalIgnoreCase);
                    try {
                        ChillHub.Core.Logging.Logger.Info($"NormalizeState gid={g.GameId} latest='{g.LatestVersion}' local='{g.InstalledVersion}' isInstalled={g.IsInstalled} needsUpdate={g.NeedsUpdate}");
                    }
                    catch {
                    }
                }
                catch {
                }
            }
        }

        private string ReadLocalVersion(string gameId) {
            try {
                if (string.IsNullOrWhiteSpace(gameId)) {
                    return string.Empty;
                }

                var root = Path.Combine(ConfigService.Current.GamesPath, gameId);
                var marker = Path.Combine(root, ".version");
                if (File.Exists(marker)) {
                    var text = File.ReadAllText(marker).Trim();
                    try {
                        ChillHub.Core.Logging.Logger.Info($"ReadLocalVersion gid={gameId} value='{text}'");
                    }
                    catch {
                    }
                    return text;
                }
            }
            catch {
            }
            return string.Empty;
        }

        private void WriteLocalVersion(string gameId, string? version) {
            try {
                if (string.IsNullOrWhiteSpace(gameId)) {
                    return;
                }

                var root = Path.Combine(ConfigService.Current.GamesPath, gameId);
                Directory.CreateDirectory(root);
                var marker = Path.Combine(root, ".version");
                var toWrite = (version ?? string.Empty).Trim();
                File.WriteAllText(marker, toWrite);
                try {
                    ChillHub.Core.Logging.Logger.Info($"WriteLocalVersion gid={gameId} value='{toWrite}'");
                }
                catch {
                }
            }
            catch {
            }
        }

        private void MarkInstalled(string gameId, string? version) {
            try {
                var g = this.games.FirstOrDefault(x => x.GameId == gameId);
                if (g != null) {
                    g.IsInstalled = true;
                    g.InstalledVersion = (version ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(g.LatestVersion)) {
                        g.LatestVersion = g.LatestVersion.Trim();
                    }

                    g.NeedsUpdate = !string.IsNullOrWhiteSpace(g.LatestVersion) &&
                                     !string.Equals(g.InstalledVersion?.Trim(), g.LatestVersion?.Trim(), StringComparison.OrdinalIgnoreCase);
                }

                // Лёгкое обновление UI без пересортировки и смены ItemsSource — это сделает фоновой шаг
                this.GameList.Items.Refresh();
            }
            catch {
            }
        }

        private void MarkUninstalled(string gameId) {
            try {
                var selectedId = this.GetSelectedGameId();
                var g = this.games.FirstOrDefault(x => x.GameId == gameId);
                if (g != null) {
                    g.IsInstalled = false;
                    g.InstalledVersion = string.Empty;

                    // После удаления считаем, что обновление не требуется до повторной проверки
                    g.NeedsUpdate = false;
                }

                this.games = this.games
                    .OrderByDescending(x => x.IsInstalled)
                    .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                this.GameList.ItemsSource = this.games;
                this.GameList.Items.Refresh();
                if (!string.IsNullOrWhiteSpace(selectedId)) {
                    var idx = this.games.FindIndex(x => x.GameId == selectedId);
                    if (idx >= 0) {
                        this.GameList.SelectedIndex = idx;
                    }
                }
            }
            catch {
            }
        }

        private void OpenGameFolder_Click(object sender, RoutedEventArgs e) {
            try {
                var gi = (sender as FrameworkElement)?.GetValue(MenuItem.CommandParameterProperty) as GameInfo
                         ?? (sender as FrameworkElement)?.DataContext as GameInfo;
                var gid = gi?.GameId;
                if (string.IsNullOrWhiteSpace(gid)) {
                    this.StatusText.Text = "Не удалось определить игру";
                    return;
                }
                var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);
                if (!Directory.Exists(localRoot)) {
                    this.StatusText.Text = "Папка игры не найдена";
                    return;
                }

                var psi = new ProcessStartInfo {
                    FileName = localRoot,
                    UseShellExecute = true,
                };
                Process.Start(psi);
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Не удалось открыть папку игры: {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.OpenGameFolder_Click");
                }
                catch {
                }
            }
        }

        private async void DeleteGame_Click(object sender, RoutedEventArgs e) {
            try {
                var gi = (sender as FrameworkElement)?.GetValue(MenuItem.CommandParameterProperty) as GameInfo
                         ?? (sender as FrameworkElement)?.DataContext as GameInfo;
                var gid = gi?.GameId;
                if (string.IsNullOrWhiteSpace(gid)) {
                    this.StatusText.Text = "Не удалось определить игру";
                    return;
                }
                var localRoot = System.IO.Path.Combine(ConfigService.Current.GamesPath, gid);

                // Переключаем текущий выбор на удаляемую игру, чтобы область действий и статусы относились к ней
                try {
                    if (gi != null) {
                        this.GameList.SelectedItem = gi;
                    }
                }
                catch {
                }

                // Подтверждение удаления (кастомный диалог в стиле темы)
                var title = string.IsNullOrWhiteSpace(gi?.Title) ? gid : gi!.Title;
                if (!this.ShowDeleteConfirmationDialog(title!, localRoot)) {
                    return;
                }

                // Проверим, не запущен ли процесс игры
                try {
                    var exeName = System.IO.Path.GetFileNameWithoutExtension(gi?.ExeRelativePath ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(exeName)) {
                        var running = Process.GetProcessesByName(exeName);
                        if (running?.Length > 0) {
                            MessageBox.Show($"Игра запущена ({exeName}). Закройте игру перед удалением.", "Удаление локальных файлов", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                    }
                }
                catch {
                }

                // Пытаемся удалить папку целиком
                try {
                    if (Directory.Exists(localRoot)) {
                        Directory.Delete(localRoot, true);
                    }

                    // Очистим кэш требуемого места
                    try {
                        lock (this.spaceCacheLock) {
                            this.neededBytesCache[gid] = 0;
                        }
                    }
                    catch {
                    }

                    // Обновим маркеры/UI
                    this.FilesSizeText.Text = string.Empty;
                    this.MarkUninstalled(gid);
                    try {
                        this.UpdateActionButtonState();
                    }
                    catch {
                    }

                    // Перепроверим статусы игр (легко и асинхронно)
                    await this.VerifyAllGamesStatusesAsync();

                    // Покажем ненавязчивый Toast вместо изменения строки статуса
                    this.ShowToast($"Локальные файлы {title} удалены");
                }
                catch (Exception exDel) {
                    this.StatusText.Text = $"Не удалось удалить локальные файлы: {exDel.Message}";
                    try {
                        Core.Logging.Logger.Error(exDel, "HomePage.DeleteGame_Click");
                    }
                    catch {
                    }
                }
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Ошибка удаления: {ex.Message}";
                try {
                    Core.Logging.Logger.Error(ex, "HomePage.DeleteGame_Click");
                }
                catch {
                }
            }
        }


    }
}
