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
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
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

        // Событие завершения первичной проверки файлов и флаг состояния
        public event Action? InitialVerificationCompleted;
        private volatile bool initialVerifyCompleted = false;
        public bool IsInitialVerificationCompleted => this.initialVerifyCompleted;

        // Единая кнопка действия: режим и флаги
        private enum ActionMode {
            Checking,
            Install,
            Update,
            Play,
            Cancel,
            Retry
        }

        private ActionMode actionMode = ActionMode.Checking;
        private bool hasUpdateError = false;

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
        }

        // Toast helper: show non-intrusive notification in bottom-right corner
        private DispatcherTimer? toastTimer;

        private void ShowToast(string message, TimeSpan? duration = null) {
            try {
                var dur = duration ?? TimeSpan.FromSeconds(3);
                this.ToastText.Text = message;
                this.Toast.Visibility = Visibility.Visible;
                this.toastTimer?.Stop();
                this.toastTimer = new DispatcherTimer(DispatcherPriority.Background) {
                    Interval = dur,
                };
                this.toastTimer.Tick += (s, e) => {
                    try {
                        this.Toast.Visibility = Visibility.Collapsed;
                    }
                    catch {
                    }
                    try {
                        (s as DispatcherTimer)?.Stop();
                    }
                    catch {
                    }
                };
                this.toastTimer.Start();
            }
            catch {
            }
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

                    // Помечаем завершение первичной проверки и уведомляем подписчиков
                    this.initialVerifyCompleted = true;
                    try {
                        await this.DispatcherInvokeAsync(() => {
                            try {
                                this.InitialVerificationCompleted?.Invoke();
                            }
                            catch {
                            }
                        });
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
                var sem = new SemaphoreSlim(2); // ещё мягче по нагрузке на диск/проц
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
                // Дополнительная защита: если событие не было поднято ранее, поднимем его здесь
                if (!this.initialVerifyCompleted) {
                    this.initialVerifyCompleted = true;
                    try {
                        await this.DispatcherInvokeAsync(() => {
                            try {
                                this.InitialVerificationCompleted?.Invoke();
                            }
                            catch {
                            }
                        });
                    }
                    catch {
                    }
                }
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
                var game = this.games.FirstOrDefault(g => g.GameId == gid);
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

        private void CoverImg_Loaded(object sender, RoutedEventArgs e) {
            if (sender is not Image img) {
                return;
            }

            // 1) Получаем сырой URL из Tag, иначе из DataContext (IconUrl), иначе из текущего Source.Uri
            string raw = (img.Tag as string) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) {
                try {
                    if (img.DataContext is ChillHub.Core.GameInfo gi) {
                        raw = gi.IconUrl ?? string.Empty;
                    }
                }
                catch {
                }
            }

            if (string.IsNullOrWhiteSpace(raw)) {
                try {
                    if (img.Source is BitmapImage bi && bi.UriSource != null) {
                        raw = bi.UriSource.OriginalString;
                    }
                }
                catch {
                }
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
                // Преобразуем в абсолютный URL при необходимости на базе BaseApi
                string url;

                // Поддержка протокол-относительных URL (//host/path)
                if (raw.StartsWith("//")) {
                    var baseUri = new Uri(this.BaseApi.TrimEnd('/') + "/", UriKind.Absolute);
                    url = new Uri(baseUri.Scheme + ":" + raw, UriKind.Absolute).ToString();
                }
                else if (!Uri.TryCreate(raw, UriKind.Absolute, out var abs)) {
                    var baseUri = new Uri(this.BaseApi.TrimEnd('/') + "/", UriKind.Absolute);
                    url = new Uri(baseUri, raw).ToString();
                }
                else {
                    url = abs.ToString();
                }

                try {
                    ChillHub.Core.Logging.Logger.Info($"[ImgLoad] resolved url='{url}'");
                }
                catch {
                }

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

                // Неблокирующая загрузка: пусть WPF подтянет изображение асинхронно
                img.Source = new BitmapImage(new Uri(url, UriKind.Absolute));
                img.Visibility = Visibility.Visible;

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
                };

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
                try {
                    deleteBtn.Style = (Style)this.FindResource("Style.Button.Primary");
                }
                catch {
                }
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
                    this.ShowToast("Локальные файлы удалены. Можно установить заново.");
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
