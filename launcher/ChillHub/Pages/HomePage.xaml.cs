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
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Threading;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Net;
    using ChillHub.Core.Sync;
    // Вспомогательная логика вынесена в Core/Home/*: форматирование, локальное состояние игр,
    // загрузка картинок, диагностика плана синхронизации. using static — чтобы не менять места вызова.
    using static ChillHub.Core.Home.GameLocalState;
    using static ChillHub.Core.Home.HomeFormat;
    using static ChillHub.Core.Home.SyncPlanLog;

    public partial class HomePage : Page {
        private string BaseApi => ChillHub.Core.ConfigService.Current.ApiBaseUrl;

        private readonly HttpClient http = HttpClientProvider.Shared;
        private List<GameInfo> games = new();
        private List<string> builds = new();
        private CancellationTokenSource? cts;
        private bool isUpdating = false;

        // Идёт удаление локальных файлов игры: блокирует повторный запуск и установку
        private bool isDeleting = false;

        // 1, пока идёт проход VerifyAllGamesStatusesAsync. Взводится через Interlocked:
        // метод зовут и из UI-потока, и из фоновых задач.
        private int verifyRunning;

        // Загрузка данных выбранной игры: сериализуется воротами, предыдущая отменяется токеном.
        private readonly SemaphoreSlim selectionGate = new(1, 1);
        private CancellationTokenSource? selectionCts;

        // Порядок игр, полученный от API. Нужен, чтобы список сортировался одинаково
        // во всех местах: раньше часть кода упорядочивала по API, часть — по названию,
        // и список прыгал после установки или удаления игры.
        private readonly Dictionary<string, int> apiOrder = new(StringComparer.OrdinalIgnoreCase);
        private readonly ISyncService sync = new SimpleSyncService();
        private double emaSpeedMBs = 0.0; // сглаженная скорость
        private const double EmaAlpha = 0.2; // чувствительность EMA

        // Кэш оценок требуемого объёма скачивания по игре (обновляется при VerifyGameStatusAsync)
        private readonly SpaceHint spaceHint = new();

        // Раньше здесь был флаг initialVerifyRunning, блокировавший любые действия на время
        // полной проверки всех игр. Теперь блокировка точечная: см. verifiedGameIds (C4).

        // Разрешение на тяжёлые проверки файлов (Plan/Execute). На старте запрещено, включаем после первичного рендеринга
        private volatile bool allowFileChecks = false;

        // Игры, чей статус уже проверен в этой сессии. Блокируем действия только по играм с неизвестным статусом,
        // чтобы не держать кнопку в режиме «Проверка…» пока проверяются остальные игры (C4).
        private readonly object verifiedLock = new();
        private readonly HashSet<string> verifiedGameIds = new(StringComparer.OrdinalIgnoreCase);

        // Технические подробности последней ошибки: в статусе показываем короткий текст,
        // детали уходят в лог и в подсказку к строке статуса (C5).
        private string lastErrorDetails = string.Empty;

        // Единая кнопка действия: режим и флаги
        private enum ActionMode {
            Checking,
            Install,
            Update,
            Play,
            Cancel,
            Retry,

            // Действие запрещено режимом технических работ на сервере (задача 25)
            Maintenance
        }

        // ===== Обратная связь =====
        // Отправка, оффлайн-очередь и ретраи живут в Core/Home/FeedbackService.
        // Здесь остаются только обработчики, на имена которых ссылается XAML.
        private FeedbackService? feedback;

        private FeedbackService Feedback => this.feedback ??= new FeedbackService(
            this.http,
            () => this.BaseApi,
            msg => this.ShowToast(msg),
            text => {
                if (this.FbStatus != null) {
                    this.FbStatus.Text = text;
                }
            });

        private void FeedbackBtn_Click(object sender, RoutedEventArgs e) {
            try {
                this.FbName.Text = string.Empty;
                this.FbContact.Text = string.Empty;
                this.FbComment.Text = string.Empty;
                this.FbStatus.Text = string.Empty;
                this.FbType.SelectedIndex = 0;
                this.UpdateFeedbackDiagnosticsInfo();
                this.FeedbackOverlay.Visibility = Visibility.Visible;

                // Валидация только по нажатию «Отправить»: на открытии ничего не подсвечиваем
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "Feedback.OpenForm");
                this.ShowToast("Не удалось открыть форму обратной связи");
            }
        }

        private void FbCancel_Click(object sender, RoutedEventArgs e) {
            this.FeedbackOverlay.Visibility = Visibility.Collapsed;
        }

        // Пользователь передумал прикладывать диагностику (или наоборот) — обновим пояснение
        private void FbAttachDiagnostics_Click(object sender, RoutedEventArgs e) => this.UpdateFeedbackDiagnosticsInfo();

        /// <summary>
        /// Показывает, что именно уйдёт вместе с сообщением. Раньше конфиг, пути установки
        /// и дерево папки игр прикладывались молча и без возможности отказаться.
        /// </summary>
        private void UpdateFeedbackDiagnosticsInfo() {
            try {
                if (this.FbDiagnosticsInfo == null) {
                    return;
                }

                if (this.FbAttachDiagnostics?.IsChecked != true) {
                    this.FbDiagnosticsInfo.Text = "Будут отправлены только ваше сообщение и указанные контакты.";
                    return;
                }

                this.FbDiagnosticsInfo.Text = "Вместе с сообщением уйдёт: "
                    + string.Join("; ", Core.Diagnostics.BundleContents)
                    + ". Имя пользователя Windows в путях заменяется на %USER%.";
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"Feedback.UpdateDiagnosticsInfo: {ex.Message}");
            }
        }

        private async void FbSend_Click(object sender, RoutedEventArgs e) {
            try {
                var comment = this.FbComment.Text?.Trim() ?? string.Empty;
                if (comment.Length < 5) {
                    this.FbStatus.Text = "Опишите проблему (мин. 5 символов)";
                    return;
                }

                // Диагностику прикладываем только с явного согласия: галочка стоит по умолчанию,
                // но её видно и её можно снять (см. форму под кнопками).
                var attachDiagnostics = this.FbAttachDiagnostics?.IsChecked == true;
                var draft = new FeedbackService.FeedbackDraft(
                    this.FbName.Text?.Trim() ?? string.Empty,
                    this.FbContact.Text?.Trim() ?? string.Empty,
                    this.GetFeedbackTypeString(),
                    comment,
                    attachDiagnostics,
                    attachDiagnostics ? FeedbackService.CollectSystemInfo() : null);

                this.FbStatus.Text = "Отправка...";
                var ok = await this.Feedback.TrySendAsync(draft, silent: false).ConfigureAwait(true);
                if (ok) {
                    this.FbStatus.Text = "Отправлено";
                    this.ShowToast("Спасибо! Сообщение отправлено");
                }
                else {
                    this.Feedback.Enqueue(draft);
                    this.FbStatus.Text = "В ожидании отправки (оффлайн)";
                    this.ShowToast("Сообщение поставлено в очередь");
                }

                this.FeedbackOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "Feedback.Send");
                this.ShowToast("Ошибка отправки");
            }
        }

        // Живая валидация отключена: проверяем только по нажатию «Отправить»
        private void FbField_Changed(object? sender, TextChangedEventArgs e) { /* no-op */ }

        private string GetFeedbackTypeString() {
            try {
                var item = this.FbType.SelectedItem as ComboBoxItem;
                var txt = item?.Content?.ToString()?.Trim()?.ToLowerInvariant() ?? string.Empty;
                return txt switch { "баг" => "bug", "идея" => "idea", "вопрос" => "question", _ => "other" };
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"Feedback.GetType: {ex.Message}; используем 'other'");
                return "other";
            }
        }

        private ActionMode actionMode = ActionMode.Checking;
        private bool hasUpdateError = false;

        /// <summary>
        /// Игра, на которой сорвалось обновление. «Повторить» относится только к ней:
        /// раньше флаг сбрасывался лишь в начале StartUpdateAsync, поэтому после неудачи
        /// на игре A выбор игры B тоже показывал «Повторить» вместо «Играть».
        /// </summary>
        private string? updateErrorGameId;

        // Loaded срабатывает и при первом показе, и при возврате со страницы игры/новости.
        // Первый раз пропускаем: там уже отрабатывает полная загрузка.
        private bool loadedOnce = false;

        /// <summary>
        /// Папка для игр, к которой относятся текущие статусы. Её могли сменить в настройках,
        /// и тогда всё показанное состояние относится к другому каталогу.
        /// </summary>
        private string knownGamesPath = ChillHub.Core.ConfigService.Current.GamesPath ?? string.Empty;

        public HomePage() {
            this.InitializeComponent();

            // Самообновление обрабатывается отдельным окном UpdateWindow до показа MainWindow
            _ = this.StartupAsync();

            // Ни одна из инициализаций ниже не должна ронять конструктор страницы:
            // при сбое любой из них лаунчер обязан открыться, пусть и без части удобств.
            try {
                // Начальное состояние единой кнопки действий
                this.UpdateActionButtonState();

                // Оффлайн-очередь обратной связи: поднимаем с диска и запускаем ретраи
                this.Feedback.Start();

                // Кнопку «Отправить» не блокируем: валидация выполняется по клику
                if (this.FbSendBtn != null) {
                    this.FbSendBtn.IsEnabled = true;
                }

                // ESC закрывает форму обратной связи (с подтверждением)
                this.PreviewKeyDown += this.HomePage_PreviewKeyDown;

                // Возврат со страницы игры: подхватываем изменившееся локальное состояние
                this.Loaded += this.HomePage_Loaded;

                // Режим технических работ: следим за ним, только пока страница показана
                this.SubscribeMaintenance();
                this.Loaded += (s, e) => this.SubscribeMaintenance();
                this.Unloaded += (s, e) => this.UnsubscribeMaintenance();

                // Фоновый ретрай очереди обратной связи держим только пока страница на экране:
                // таймер, переживший страницу, перезаписывает feedback_queue.json своей копией
                // очереди и способен затереть только что добавленное сообщение.
                this.Loaded += (s, e) => this.feedback?.Resume();
                this.Unloaded += (s, e) => this.feedback?.Stop();

                // Баннер о том, что отчёт об ошибке ушёл автоматически.
                // ErrorReporter — статический класс, его события переживают страницу: подписка
                // лямбдой без отписки удерживала бы HomePage в памяти навсегда (как и MaintenanceService).
                this.SubscribeErrorReporter();
                this.Loaded += (s, e) => this.SubscribeErrorReporter();
                this.Unloaded += (s, e) => this.UnsubscribeErrorReporter();
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "HomePage.ctor");
            }
        }

        private void HomePage_PreviewKeyDown(object sender, KeyEventArgs e) {
            try {
                if (e.Key == Key.Escape && this.FeedbackOverlay != null && this.FeedbackOverlay.Visibility == Visibility.Visible) {
                    e.Handled = true;
                    var res = MessageBox.Show("Закрыть форму обратной связи? Введённый текст будет сохранён только если вы отправите его.",
                        "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes) {
                        this.FeedbackOverlay.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex) {
                // Обработчик клавиш не имеет права ронять окно
                Core.Logging.Logger.Error(ex, "HomePage.PreviewKeyDown");
            }
        }

        // Всплывающие уведомления в правом нижнем углу: анимации и таймеры — в Core/Home/ToastHost.
        private ToastHost? toastHost;

        private ToastHost Toaster => this.toastHost ??= new ToastHost(this.Toast, this.ToastText);

        private void ShowToast(string message, TimeSpan? duration = null) => this.Toaster.Show(message, duration);

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
                // Дадим UI отрисоваться до тяжёлой асинхронной работы
                await Task.Yield();

                // Самообновление проверяется в UpdateWindow. Здесь не блокируем UI: запускаем загрузку в фоне
                _ = this.LoadInitialAsync();
            }
            catch (Exception ex) {
                // Страница уже показана — падать нельзя, пользователь увидит пустой список и кнопку «Повторить»
                Core.Logging.Logger.Error(ex, "HomePage.StartupAsync");
            }
        }

        // Удалена legacy-проверка самообновления: ею занимается UpdateWindow
        private async Task LoadInitialAsync() {
            try {
                // Показ скелетонов по секциям: Игры видимые, список скрыт до загрузки
                this.GamesSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.GameList.Visibility = System.Windows.Visibility.Collapsed;

                // Проверка доступа к папке для игр и предложение выбрать другую при отсутствии прав
                HomeDialogs.EnsureGamesPathAccessibleOrPrompt();

                // Быстрая параллельная загрузка игр и новостей лаунчера
                var gamesUrl = $"{this.BaseApi}/api/games";
                var newsUrl = $"{this.BaseApi}/news/index.json";

                GamesResponse? gamesResp = null;
                NewsIndex? newsResp = null;
                Exception? gamesError = null;
                try {
                    gamesResp = await this.http.GetFromJsonAsync<GamesResponse>(gamesUrl).ConfigureAwait(false);
                }
                catch (Exception ex) {
                    // Ошибку показываем ниже как empty-state «сервер недоступен», а не как исключение
                    gamesError = ex;
                    Core.Logging.Logger.ErrorNoReport(ex, $"LoadInitialAsync: GET {gamesUrl}");
                }

                try {
                    newsResp = await this.http.GetFromJsonAsync<NewsIndex>(newsUrl).ConfigureAwait(false);
                }
                catch (Exception ex) {
                    // Новости второстепенны: без них лаунчер полностью работоспособен
                    Core.Logging.Logger.ErrorNoReport(ex, $"LoadInitialAsync: GET {newsUrl}");
                }

                var games = gamesResp?.Items ?? new List<GameInfo>();

                // Сервер недоступен и списка игр нет — показываем пустое состояние с кнопкой «Повторить» (C5)
                if (gamesResp == null && games.Count == 0) {
                    await this.DispatcherInvokeAsync(() => {
                        this.ShowServerUnavailableState(gamesError);
                    });
                    return;
                }

                await this.DispatcherInvokeAsync(() => this.HideServerUnavailableState());

                // Нормализация URL и локального состояния до биндинга в UI
                this.NormalizeGameIconsAndLocalState(games);

                // Сортировка: установленные сначала, затем порядок из полученного списка
                this.RememberApiOrder(games);
                var ordered = this.SortGames(games);

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
                        this.GamesSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                        this.GameList.Visibility = System.Windows.Visibility.Visible;
                        this.UpdateActionButtonState();
                    }
                    catch (Exception ex) {
                        // Список получили, но привязать не смогли: сообщаем и оставляем страницу живой
                        this.ShowUserError("Не удалось отобразить список игр.", ex, "LoadInitialAsync.BindGames");
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
                    catch (Exception ex) {
                        // Новости — необязательная секция, из-за неё не мешаем работать с играми
                        Core.Logging.Logger.Error(ex, "LoadInitialAsync.BindLauncherNews");
                    }
                });

                // Загрузка сборок и новостей выбранной игры (легковесно для UI).
                // Мы здесь уже в пуле потоков (после ConfigureAwait(false) на HTTP-запросах),
                // а и выделение, и сам LoadBuildsAndGameNewsAsync работают с контролами —
                // поэтому и чтение выбора, и вызов выполняем на UI-потоке.
                string? gid0 = null;
                await this.DispatcherInvokeAsync(() => gid0 = this.GetSelectedGameId());
                if (!string.IsNullOrWhiteSpace(gid0)) {
                    await this.DispatcherInvokeAsync(() => this.LoadBuildsAndGameNewsAsync(gid0));
                }

                // После первичного рендеринга — разрешаем тяжёлые проверки и запускаем в фоне
                this.allowFileChecks = true;
                this.ResetVerifiedStatuses();

                // Сразу обновим состояние кнопки: показать "Проверка…" на время первичной проверки
                await this.DispatcherInvokeAsync(() => this.UpdateActionButtonState());

                string? priorityGid = null;
                await this.DispatcherInvokeAsync(() => priorityGid = this.GetSelectedGameId());
                _ = Task.Run(async () => {
                    // Выбранная игра проверяется первой — кнопка для неё разблокируется раньше остальных (C4)
                    await this.VerifyAllGamesStatusesAsync(priorityGid);
                    try {
                        string? gid = null;
                        await this.DispatcherInvokeAsync(() => gid = this.GetSelectedGameId());
                        if (!string.IsNullOrWhiteSpace(gid)) {
                            await this.DispatcherInvokeAsync(() => this.UpdateSpaceHintFromCache(gid));
                            await this.DispatcherInvokeAsync(() => this.UpdateActionButtonState());
                        }
                    }
                    catch (Exception ex) {
                        // Фоновая доводка UI после проверки статусов: сбой не должен ронять фоновую задачу
                        Core.Logging.Logger.Error(ex, "LoadInitialAsync.PostVerifyUi");
                    }
                });
            }
            catch (Exception ex) {
                await this.DispatcherInvokeAsync(() =>
                    this.ShowUserError("Не удалось загрузить данные. Проверьте подключение к интернету.", ex, "HomePage.LoadInitialAsync"));
            }
        }

        // --- Пустое состояние «сервер недоступен» (C5) ---
        private void ShowServerUnavailableState(Exception? ex) {
            try {
                this.games = new List<GameInfo>();
                this.GameList.ItemsSource = this.games;
                this.GamesSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameList.Visibility = System.Windows.Visibility.Collapsed;
                this.GamesEmptyState.Visibility = System.Windows.Visibility.Visible;

                // Новости тоже не пришли — уберём вечные скелетоны
                this.LauncherNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch (Exception uiEx) {
                // Даже если не удалось перерисовать секции, сообщение об ошибке ниже показать обязаны
                Core.Logging.Logger.Error(uiEx, "HomePage.ShowServerUnavailableState");
            }

            this.ShowUserError("Не удалось связаться с сервером.", ex, "HomePage.LoadInitialAsync");
            try {
                this.SetActionMode(ActionMode.Retry);
                this.ActionBtn.IsEnabled = false; // действия недоступны: список игр пуст
            }
            catch (Exception btnEx) {
                Core.Logging.Logger.Error(btnEx, "HomePage.ShowServerUnavailableState.ActionButton");
            }
        }

        private void HideServerUnavailableState() {
            this.GamesEmptyState.Visibility = System.Windows.Visibility.Collapsed;
            this.ClearErrorDetails();
        }

        // Кнопка «Повторить» в пустом состоянии: переигрываем первичную загрузку
        private async void RetryLoad_Click(object sender, RoutedEventArgs e) {
            try {
                this.HideServerUnavailableState();
                this.StatusText.Text = "Повторная попытка…";
                this.GamesSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.GameList.Visibility = System.Windows.Visibility.Collapsed;
                await this.LoadInitialAsync();
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось связаться с сервером.", ex, "HomePage.RetryLoad_Click");
            }
        }

        // --- Фактическая проверка статуса игры по манифесту (полное сравнение) ---
        // priorityGameId: игру с этим id проверяем первой и сразу разблокируем для неё кнопку действия,
        // остальные догоняются в фоне и лишь обновляют свои бейджи (C4).
        private async Task VerifyAllGamesStatusesAsync(string? priorityGameId = null) {
            if (this.games == null || this.games.Count == 0) {
                return;
            }

            // Проход считает хеши всех файлов всех игр. Второй такой же, запущенный
            // двойным кликом по «обновить» (или удалением игры во время первичной проверки),
            // просто удваивает дисковую нагрузку и наперегонки правит те же GameInfo.
            if (Interlocked.CompareExchange(ref this.verifyRunning, 1, 0) != 0) {
                Core.Logging.Logger.Info("VerifyAllGamesStatusesAsync: проверка уже идёт, повторный запуск пропущен");
                return;
            }

            try {
                await this.DispatcherInvokeAsync(() => {
                    this.GamesVerifyIndicator.Visibility = Visibility.Visible;
                    this.RefreshGamesBtn.IsEnabled = false;
                });

                // Лёгкий прогресс: processed/total в StatusText, чтобы пользователь видел процесс
                int total = this.games.Count;
                int processed = 0;
                var sem = new SemaphoreSlim(3); // параллельность проверки = 3

                var lastUi = System.Diagnostics.Stopwatch.StartNew();

                // Явно покажем старт проверки
                await this.DispatcherInvokeAsync(() => this.StatusText.Text = $"Проверка игр: {processed}/{total}");

                // Сначала — выбранная игра, чтобы кнопка действия стала доступной как можно раньше
                var pending = new List<GameInfo>(this.games);
                if (!string.IsNullOrWhiteSpace(priorityGameId)) {
                    var first = pending.FirstOrDefault(x => string.Equals(x.GameId, priorityGameId, StringComparison.OrdinalIgnoreCase));
                    if (first != null) {
                        pending.Remove(first);
                        try {
                            await this.VerifyGameStatusAsync(first);
                        }
                        catch (Exception ex) {
                            // Одна игра не проверилась — остальные проверяем дальше, статус этой останется прежним
                            Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync(priority {first.GameId})");
                        }

                        Interlocked.Increment(ref processed);
                        await this.DispatcherInvokeAsync(() => {
                            this.GameList.Items.Refresh();
                            this.UpdateActionButtonState();
                            this.StatusText.Text = $"Проверка игр: {processed}/{total}";
                        });
                    }
                }

                var tasks = new List<Task>();
                foreach (var g in pending) {
                    await sem.WaitAsync();
                    var task = Task.Run(async () => {
                        try {
                            await this.VerifyGameStatusAsync(g);
                        }
                        catch (Exception ex) {
                            Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync({g.GameId})");
                        }
                        finally {
                            Interlocked.Increment(ref processed);
                            try {
                                // Бейдж проверенной игры обновляем сразу, чтобы статусы появлялись по мере готовности
                                await this.DispatcherInvokeAsync(() => {
                                    this.GameList.Items.Refresh();

                                    // Если это выбранная игра — кнопка действия должна разблокироваться немедленно
                                    if (string.Equals(this.GetSelectedGameId(), g.GameId, StringComparison.OrdinalIgnoreCase)) {
                                        this.UpdateActionButtonState();
                                    }
                                });

                                // Обновляем текст прогресса не слишком часто (не чаще ~5 раз/сек)
                                if (lastUi.ElapsedMilliseconds >= 200) {
                                    lastUi.Restart();
                                    await this.DispatcherInvokeAsync(() => this.StatusText.Text = $"Проверка игр: {processed}/{total}");
                                }
                            }
                            catch (Exception exUi) {
                                // Обновление индикаторов не должно ронять фоновую проверку остальных игр
                                Core.Logging.Logger.Error(exUi, "VerifyAllGamesStatusesAsync.ProgressUi");
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
                    // Выделение живёт в GameList и читается только с UI-потока: сюда мы приходим
                    // из Task.Run, и прямое обращение бросало бы исключение. Раньше оно гасилось,
                    // GetSelectedGameId возвращал null — и выделение терялось после смены ItemsSource.
                    string? selectedId = null;
                    await this.DispatcherInvokeAsync(() => selectedId = this.GetSelectedGameId());

                    // Порядок из ответа API сохраняем, установленные держим сверху
                    this.games = this.SortGames(this.games);
                    await this.DispatcherInvokeAsync(() => {
                        this.GameList.ItemsSource = this.games;
                        this.GameList.Items.Refresh();
                        if (!string.IsNullOrWhiteSpace(selectedId)) {
                            var idx = this.games.FindIndex(x => x.GameId == selectedId);
                            if (idx >= 0) {
                                this.GameList.SelectedIndex = idx;
                            }
                        }
                    });
                }
                catch (Exception ex) {
                    // Пересортировка — косметика: статусы уже проверены и показаны
                    Core.Logging.Logger.Error(ex, "VerifyAllGamesStatusesAsync.Reorder");
                }
            }
            catch (Exception ex) {
                // Проверка статусов фоновая: не показываем модальных ошибок, но фиксируем в логе
                Core.Logging.Logger.Error(ex, "VerifyAllGamesStatusesAsync");
            }
            finally {
                // Подстраховка: после завершения прохода статус любой игры считается известным,
                // иначе кнопка действия для непроверенной игры осталась бы заблокированной
                try {
                    foreach (var g in this.games ?? new List<GameInfo>()) {
                        this.MarkGameStatusKnown(g?.GameId);
                    }
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Error(ex, "VerifyAllGamesStatusesAsync.finally");
                }

                Interlocked.Exchange(ref this.verifyRunning, 0);
                await this.DispatcherInvokeAsync(() => {
                    this.GamesVerifyIndicator.Visibility = Visibility.Collapsed;
                    this.RefreshGamesBtn.IsEnabled = true;

                    // После завершения всегда выставляем финальный статус, чтобы не зависало "Проверка игр X/Y".
                    // Сообщение об ошибке при этом не затираем — оно важнее.
                    if (string.IsNullOrWhiteSpace(this.lastErrorDetails)) {
                        this.StatusText.Text = "Готово";
                    }

                    this.UpdateActionButtonState();
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
                var localRoot = GameLocalRoot(gid);
                var hasLocalFiles = HasAnyLocalGameFiles(localRoot);
                var unfinished = ChillHub.Core.Sync.SimpleSyncService.HasUpdateMarker(localRoot);
                if (unfinished) {
                    // Обновление прерывали посередине: часть файлов новая, часть старая.
                    // Игру нельзя считать готовой — предлагаем докатить обновление.
                    game.IsInstalled = hasLocalFiles;
                    game.NeedsUpdate = true;
                    Core.Logging.Logger.Warn($"VerifyGameStatusAsync gid={gid} найден маркер незавершённого обновления: {ChillHub.Core.Sync.SimpleSyncService.ReadUpdateMarker(localRoot)}");

                    return;
                }

                if (!hasLatest) {
                    // Нет эталона для сравнения — считаем не установленной, если нет локальных файлов; иначе установленной без статуса обновления
                    game.IsInstalled = hasLocalFiles;
                    game.NeedsUpdate = false; // нет способа сравнить
                    Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} latest=<none> hasLocalFiles={hasLocalFiles} -> IsInstalled={game.IsInstalled} NeedsUpdate={game.NeedsUpdate}");

                    // Отложим Refresh до завершения всех проверок, чтобы не трясти UI на каждую игру
                    return;
                }

                // Получаем манифест latest и план сравнения
                var manifestUrl = IntegrityChecker.ManifestUrl(this.BaseApi, gid, latest);
                Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} fetching manifest {manifestUrl}");
                var manifest = await this.sync.GetManifestAsync(manifestUrl, CancellationToken.None);
                var contentBase = IntegrityChecker.ContentBaseUrl(this.BaseApi, gid, latest);
                var plan = await this.PlanOffUiThreadAsync(manifest, localRoot, contentBase, CancellationToken.None);
                Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} plan: downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count} emptyDirs={plan.EmptyDirsToCreate.Count}");
                LogPlanDownloads(gid, "verify", plan, localRoot);

                // Обновим кэш требуемого объёма скачивания
                this.spaceHint.Remember(gid, plan.TotalDownloadBytes);

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

                Core.Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} result: IsInstalled={game.IsInstalled} NeedsUpdate={game.NeedsUpdate}");

                // Отложим Refresh до завершения всех проверок
            }
            catch (ManifestValidationException ex) {
                // Фоновая проверка статуса: молча статус не меняем, но в логе фиксируем именно
                // отклонённый манифест, а не «какую-то ошибку сети». Пользователь увидит явный
                // текст, когда нажмёт «Установить»/«Обновить» — см. StartUpdateAsync.
                Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync({game?.GameId}): манифест не прошёл проверку");
            }
            catch (Exception ex) {
                // В случае ошибки проверки — не меняем текущий статус, только логируем
                Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync({game?.GameId})");
            }
            finally {
                // Статус игры считается известным даже при ошибке проверки:
                // иначе кнопка действия останется заблокированной навсегда (C4)
                this.MarkGameStatusKnown(game?.GameId);
            }
        }

        private bool IsGameStatusKnown(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return true; // нет выбора — нечего блокировать
            }

            lock (this.verifiedLock) {
                return this.verifiedGameIds.Contains(gameId);
            }
        }

        private void MarkGameStatusKnown(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            lock (this.verifiedLock) {
                this.verifiedGameIds.Add(gameId);
            }
        }

        private void ResetVerifiedStatuses() {
            lock (this.verifiedLock) {
                this.verifiedGameIds.Clear();
            }
        }

        /// <summary>
        /// Запоминает порядок игр, пришедший от API: он задаёт положение игр внутри групп.
        /// </summary>
        private void RememberApiOrder(IEnumerable<GameInfo> games) {
            this.apiOrder.Clear();
            var i = 0;
            foreach (var g in games) {
                var id = g?.GameId ?? string.Empty;
                if (!this.apiOrder.ContainsKey(id)) {
                    this.apiOrder[id] = i;
                }

                i++;
            }
        }

        /// <summary>
        /// Единственное правило сортировки списка игр: установленные сверху, внутри группы —
        /// порядок из ответа API, при его отсутствии — по названию. Раньше правил было два
        /// (одно после загрузки, другое после установки и удаления), и список перетасовывался
        /// сам собой прямо под курсором.
        /// </summary>
        /// <param name="games">Исходный список.</param>
        /// <returns>Новый отсортированный список.</returns>
        private List<GameInfo> SortGames(IEnumerable<GameInfo> games) =>
            games.OrderBy(g => g.IsInstalled ? 0 : 1)
                 .ThenBy(g => this.apiOrder.TryGetValue(g.GameId ?? string.Empty, out var idx) ? idx : int.MaxValue)
                 .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                 .ToList();

        // Короткое сообщение пользователю в статусе; технические детали — в лог и в подсказку (C5)

        /// <summary>
        /// Удаляет содержимое папки игры, доводя проход до конца.
        /// <para>
        /// <see cref="Directory.Delete(string, bool)"/> обрывается на ПЕРВОМ занятом файле,
        /// когда остальное уже удалено. Пользователь видел «не удалось удалить», игра
        /// оставалась помеченной установленной, а на диске лежали её остатки, неспособные
        /// запуститься. Здесь занятый файл не прерывает работу: он попадает в список,
        /// который вызывающий код показывает пользователю.
        /// </para>
        /// </summary>
        /// <param name="root">Корень папки игры.</param>
        /// <returns>Файлы, которые удалить не удалось (пустой список — всё снесено).</returns>
        private static List<string> DeleteGameFiles(string root) {
            var blocked = new List<string>();
            if (!Directory.Exists(root)) {
                return blocked;
            }

            // Списки материализуем заранее: удалять во время ленивого обхода нельзя.
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            foreach (var file in files) {
                try {
                    var info = new FileInfo(file);
                    if (info.IsReadOnly) {
                        info.IsReadOnly = false;
                    }

                    File.Delete(file);
                }
                catch (Exception ex) {
                    blocked.Add(file);
                    Core.Logging.Logger.Warn($"DeleteGameFiles: файл занят и не удалён: '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            // Каталоги — от самых глубоких к корню. Непустые (из-за занятых файлов)
            // просто не удалятся, и это ожидаемо.
            var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                .OrderByDescending(d => d.Length).ToList();
            foreach (var dir in dirs) {
                try {
                    Directory.Delete(dir, false);
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Warn($"DeleteGameFiles: каталог не удалён: '{dir}': {ex.Message}");
                }
            }

            if (blocked.Count == 0) {
                try {
                    Directory.Delete(root, true);
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Warn($"DeleteGameFiles: корень игры не удалён: {ex.Message}");
                }
            }

            return blocked;
        }

        private void ShowUserError(string userMessage, Exception? ex = null, string? context = null) {
            try {
                if (ex != null) {
                    Core.Logging.Logger.Error(ex, context ?? "HomePage");
                }
                else if (!string.IsNullOrWhiteSpace(context)) {
                    Core.Logging.Logger.Error($"{context}: {userMessage}");
                }
            }
            catch (Exception logEx) {
                // Сам обработчик ошибок бросать не должен: показать сообщение пользователю важнее записи в лог
                System.Diagnostics.Debug.WriteLine("ShowUserError: не удалось записать лог: " + logEx.Message);
            }

            var details = ex?.Message ?? string.Empty;
            this.lastErrorDetails = string.IsNullOrWhiteSpace(context) ? details : $"{context}: {details}";
            try {
                this.StatusText.Text = userMessage;
                this.StatusText.ToolTip = string.IsNullOrWhiteSpace(this.lastErrorDetails)
                    ? null
                    : "Подробнее: " + this.lastErrorDetails;
            }
            catch (Exception uiEx) {
                Core.Logging.Logger.Error(uiEx, "HomePage.ShowUserError");
            }
        }

        // Сброс подсказки с техническими деталями при переходе к обычному статусу
        private void ClearErrorDetails() {
            this.lastErrorDetails = string.Empty;
            this.StatusText.ToolTip = null;
        }

        /// <summary>
        /// Строит план различий, гарантированно не занимая UI-поток.
        /// <see cref="ISyncService.PlanAsync(Manifest, string, string, CancellationToken)"/> только выглядит
        /// асинхронным: внутри полный обход папки игры с пересчётом хешей, а результат возвращается
        /// через уже завершённый Task. При вызове с UI-потока окно замирает на всё время обхода
        /// (гигабайты SHA-256/BLAKE3), Windows рисует «Не отвечает», и даже «Отмена» не нажимается.
        /// Тот же приём применён в <see cref="IntegrityChecker"/>.
        /// </summary>
        private Task<DiffPlan> PlanOffUiThreadAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken token) =>
            Task.Run(() => this.sync.PlanAsync(manifest, localRoot, contentBaseUrl, token), token);

        private Task DispatcherInvokeAsync(Action action) {
            Dispatcher? dispatcher;
            try {
                dispatcher = Application.Current?.Dispatcher;
            }
            catch (Exception ex) {
                // Приложение уже выгружается — выполним действие прямо здесь.
                // Важно: сам action() наружу не оборачиваем, иначе при его падении
                // он выполнялся бы дважды (старое поведение этого метода).
                Core.Logging.Logger.Warn($"DispatcherInvokeAsync: диспетчер недоступен: {ex.Message}");
                dispatcher = null;
            }

            if (dispatcher == null || dispatcher.CheckAccess()) {
                action();
                return Task.CompletedTask;
            }

            // RunContinuationsAsynchronously обязателен: TrySetResult вызывается ИЗНУТРИ BeginInvoke,
            // то есть на UI-потоке. Без этого флага продолжение await'а тоже выполнялось бы инлайн
            // на UI-потоке — и фоновая проверка статусов (VerifyAllGamesStatusesAsync в Task.Run)
            // после первого же await «переезжала» на UI вместе со всем хешированием файлов.
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        /// <summary>
        /// То же, что и <see cref="DispatcherInvokeAsync(Action)"/>, но для асинхронной работы:
        /// операция ЗАПУСКАЕТСЯ на UI-потоке (и продолжается на нём, так как захватывает его контекст),
        /// а вызывающий ждёт её завершения, не занимая UI.
        /// </summary>
        private async Task DispatcherInvokeAsync(Func<Task> action) {
            Task? started = null;

            // ФИГУРНЫЕ СКОБКИ ЗДЕСЬ ОБЯЗАТЕЛЬНЫ — без них метод вызывает сам себя.
            //
            // Было: DispatcherInvokeAsync(() => started = action())
            // Выражение `started = action()` имеет ТИП Task, поэтому лямбда подходит и
            // под Action (значение отбрасывается), и под Func<Task>. При разрешении
            // перегрузок C# предпочитает делегат, возвращаемый тип которого совпадает
            // с выведенным типом лямбды, то есть Func<Task> — эту же перегрузку.
            // Получалась бесконечная рекурсия и мгновенное переполнение стека: процесс
            // умирал, не успев ни записать лог, ни отправить отчёт об ошибке.
            //
            // Блочная лямбда значения не возвращает и приводится ТОЛЬКО к Action.
            await this.DispatcherInvokeAsync(() => { started = action(); }).ConfigureAwait(false);

            if (started != null) {
                await started.ConfigureAwait(false);
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

                this.GameNewsHeader.Text = title;
            }
            catch (Exception ex) {
                // Заголовок секции новостей — косметика, оставляем прежний текст
                Core.Logging.Logger.Warn($"UpdateGameNewsHeader: {ex.Message}");
            }
        }

        private async Task LoadBuildsAndGameNewsAsync(string gameId, CancellationToken token = default) {
            try {
                // Очистим новости игры сразу, чтобы не мигали новости другой игры
                this.GameNewsList.ItemsSource = Array.Empty<NewsItem>();
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.GameNewsList.Visibility = System.Windows.Visibility.Collapsed;

                // Сборки
                var buildsUrl = $"{this.BaseApi}/api/games/{gameId}/builds";
                var buildsResp = await this.http.GetFromJsonAsync<BuildsResponse>(buildsUrl, token);

                // Выбор уже сменили: писать this.builds нельзя — они относились бы к другой игре
                token.ThrowIfCancellationRequested();

                // Сервер отдаёт сборки в произвольном порядке, а код ниже берёт из списка
                // «последнюю версию». Сортируем сами: на проде первым элементом приходила
                // 1.0.2 при доступной 1.1.10.
                this.builds = (buildsResp?.Items ?? new List<string>())
                    .OrderByDescending(v => v, Comparer<string>.Create(VersionOrder.Compare))
                    .ToList();

                // Обновим локальные поля, но не трогаем NeedsUpdate здесь — его ставит проверка по манифесту
                var game = this.games.FirstOrDefault(g => g.GameId == gameId);

                // Чтение версии с диска выполняем в фоновом потоке
                var localVer = await Task.Run(() => ReadLocalVersion(gameId), token);
                token.ThrowIfCancellationRequested();
                var localTrimmed = string.IsNullOrWhiteSpace(localVer) ? string.Empty : localVer.Trim();
                Core.Logging.Logger.Info($"LoadBuildsAndGameNewsAsync gid={gameId} local='{localTrimmed}'");
                if (game != null) {
                    game.IsInstalled = !string.IsNullOrWhiteSpace(localTrimmed);
                    game.InstalledVersion = localTrimmed ?? string.Empty;
                }

                this.GameList.Items.Refresh();

                // Новости игры
                var gameNewsUrl = $"{this.BaseApi}/news/games/{gameId}/index.json";
                var gameNews = await this.http.GetFromJsonAsync<NewsIndex>(gameNewsUrl, token);
                token.ThrowIfCancellationRequested();
                var items = gameNews?.Items ?? new List<NewsItem>();
                this.NormalizeCoverUrls(items);
                this.GameNewsList.ItemsSource = items;
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsList.Visibility = System.Windows.Visibility.Visible;

                // После загрузки — обновим заголовок (на случай, если он ещё не обновлён)
                this.UpdateGameNewsHeader();
            }
            catch (OperationCanceledException) {
                // Пользователь выбрал другую игру: результат этой загрузки больше не нужен,
                // и показывать по нему ошибку тем более нельзя.
                throw;
            }
            catch (Exception ex) {
                // Пользователю — суть, URL и текст исключения уходят в лог и в подсказку
                this.ShowUserError(
                    "Не удалось загрузить сведения об игре. Проверьте подключение к интернету.",
                    ex,
                    $"HomePage.LoadBuildsAndGameNewsAsync: GET {this.BaseApi}/api/games/{gameId}/builds, /news/games/{gameId}/index.json");

                // В случае ошибки не оставляем старые новости от предыдущей игры
                this.GameNewsList.ItemsSource = Array.Empty<NewsItem>();
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsList.Visibility = System.Windows.Visibility.Visible;

                // Обновим заголовок до дефолтного/актуального
                this.UpdateGameNewsHeader();
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
                this.ShowUserError("Не удалось обновить новости лаунчера.", ex, "HomePage.ReloadLauncherNewsAsync");
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
                this.ShowUserError("Не удалось обновить новости игры.", ex, "HomePage.ReloadGameNewsAsync");
            }
            finally {
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsList.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>
        /// Сбрасывает «залипший» режим «Повторить» при переходе к другой игре.
        /// Ошибка обновления относится к конкретной игре, а не ко всей странице.
        /// </summary>
        private void ResetUpdateErrorIfGameChanged(string? gid) {
            if (!this.hasUpdateError || this.isUpdating) {
                return;
            }

            if (string.Equals(this.updateErrorGameId, gid, StringComparison.OrdinalIgnoreCase)) {
                return; // та же игра — «Повторить» по-прежнему уместно
            }

            this.hasUpdateError = false;
            this.updateErrorGameId = null;
            this.ClearErrorDetails();
        }

        /// <summary>
        /// Смена выбранной игры. Обработчик async void, и пользователь легко запускает
        /// его несколько раз подряд (стрелками по списку). Без сериализации две загрузки
        /// шли параллельно и вперемешку писали this.builds: список сборок оставался от
        /// той игры, чей ответ пришёл последним, — то есть от произвольной.
        /// Предыдущая загрузка отменяется, новая ждёт её завершения.
        /// </summary>
        private async void GameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            var gid = this.GetSelectedGameId();
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref this.selectionCts, cts);
            try {
                previous?.Cancel();
            }
            catch (Exception ex) {
                // Предыдущий источник уже освобождён своим владельцем — это нормально
                Core.Logging.Logger.Warn($"GameCombo_SelectionChanged: отмена предыдущей загрузки: {ex.Message}");
            }

            try {
                await this.selectionGate.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) {
                cts.Dispose();
                return;
            }

            try {
                await this.HandleGameSelectionAsync(gid, cts.Token);
            }
            catch (OperationCanceledException) {
                // Выбор сменился, пока грузились данные — молча уступаем место новой загрузке
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось загрузить данные выбранной игры.", ex, "HomePage.GameCombo_SelectionChanged");
            }
            finally {
                this.selectionGate.Release();
                Interlocked.CompareExchange(ref this.selectionCts, null, cts);
                cts.Dispose();
            }
        }

        private async Task HandleGameSelectionAsync(string? gidRaw, CancellationToken token) {
            if (gidRaw is string gid && !string.IsNullOrWhiteSpace(gid)) {
                this.ResetUpdateErrorIfGameChanged(gid);

                // Если сейчас не выполняется обновление, сбросим состояние прогресса и статусы
                if (!this.isUpdating) {
                    this.StatusText.Text = "Готово";
                    this.UpdateProgress.IsIndeterminate = false;
                    this.UpdateProgress.Value = 0;
                    this.SpeedEtaText.Text = string.Empty;
                    this.FilesSizeText.Text = string.Empty;
                }

                // Обновим локальный статус выбранной игры для списка
                var g = this.games.FirstOrDefault(x => x.GameId == gid);
                var localVer = await Task.Run(() => ReadLocalVersion(gid), token);
                token.ThrowIfCancellationRequested();
                var localTrimmed = string.IsNullOrWhiteSpace(localVer) ? string.Empty : localVer.Trim();
                if (g != null) {
                    g.IsInstalled = !string.IsNullOrWhiteSpace(localTrimmed);
                    g.InstalledVersion = localTrimmed ?? string.Empty;
                }

                this.GameList.Items.Refresh();

                // Обновим заголовок новостей игры под выбранную игру
                this.UpdateGameNewsHeader();

                // Показать имеющийся кэш сразу (мгновенно), затем уточнить расчётом
                this.UpdateSpaceHintFromCache(gid);
                await this.LoadBuildsAndGameNewsAsync(gid, token);

                // На старте запрещаем тяжёлые проверки. Разрешаем только после первичного рендеринга (когда _allowFileChecks = true).
                // Если статус игры ещё проверяется — не дублируем тяжёлый Plan, оценка появится по завершении проверки.
                if (this.allowFileChecks && this.IsGameStatusKnown(gid)) {
                    await this.UpdateSpaceHintAsync(gid, token);
                }

                // Всегда обновляем состояние кнопки при смене выбора
                this.UpdateActionButtonState();
            }
            else {
                if (!this.isUpdating) {
                    this.StatusText.Text = "Готово";
                    this.UpdateProgress.IsIndeterminate = false;
                    this.UpdateProgress.Value = 0;
                    this.SpeedEtaText.Text = string.Empty;
                    this.FilesSizeText.Text = string.Empty;
                }

                // Сброс заголовка при отсутствии выбранной игры
                this.UpdateGameNewsHeader();
            }
        }

        /// <summary>
        /// Показывает строку вида «Нужно: N (M доступно)».
        /// Считает манифест и план по сети, поэтому к моменту записи результата выбор мог
        /// смениться: раньше метод запускался без токена и без проверки актуальности, и
        /// подсказка от предыдущей игры перетирала подсказку текущей.
        /// </summary>
        /// <param name="gid">Игра, для которой считаем объём.</param>
        /// <param name="token">Токен отмены выбора.</param>
        private async Task UpdateSpaceHintAsync(string gid, CancellationToken token) {
            try {
                if (!this.TryShowTrivialSpaceHint(gid)) {
                    return;
                }

                // Есть готовая оценка — сеть не трогаем
                if (this.spaceHint.TryGet(gid, out _)) {
                    this.FilesSizeText.Text = this.spaceHint.BuildTextFromCache(gid);
                    return;
                }

                // Версия: latest из списка игр, иначе максимальная из списка сборок
                var game = this.games?.FirstOrDefault(g => g.GameId == gid);
                var version = game?.LatestVersion;
                if (string.IsNullOrWhiteSpace(version)) {
                    version = VersionOrder.SelectLatest(this.builds);
                }

                if (string.IsNullOrWhiteSpace(version)) {
                    this.FilesSizeText.Text = string.Empty;
                    return;
                }

                var manifestUrl = IntegrityChecker.ManifestUrl(this.BaseApi, gid, version);
                var contentBase = IntegrityChecker.ContentBaseUrl(this.BaseApi, gid, version);
                var localRoot = GameLocalRoot(gid);

                var manifest = await this.sync.GetManifestAsync(manifestUrl, token);
                var plan = await this.PlanOffUiThreadAsync(manifest, localRoot, contentBase, token);

                // Кэш заполняем в любом случае — он привязан к игре, а не к выбору
                this.spaceHint.Remember(gid, plan.TotalDownloadBytes);

                // А вот в строку пишем, только если пользователь всё ещё смотрит на эту игру
                token.ThrowIfCancellationRequested();
                if (!string.Equals(this.GetSelectedGameId(), gid, StringComparison.OrdinalIgnoreCase)) {
                    return;
                }

                this.FilesSizeText.Text = SpaceHint.BuildText(plan.TotalDownloadBytes, GetAvailableFreeSpaceFor(gid));
            }
            catch (OperationCanceledException) {
                // Выбор сменился — результат уже неактуален, строку не трогаем
            }
            catch (Exception ex) {
                // Подсказка о размере не критична: сервер мог не отдать манифест — просто прячем строку.
                Core.Logging.Logger.Warn($"UpdateSpaceHint gid={gid}: {ex.Message}");
                this.SetFilesSizeTextSafe(string.Empty);
            }
        }

        // Обновляет FilesSizeText только из кэша, не выполняя сетевых запросов
        private void UpdateSpaceHintFromCache(string gid) {
            try {
                if (!this.TryShowTrivialSpaceHint(gid)) {
                    return;
                }

                this.FilesSizeText.Text = this.spaceHint.BuildTextFromCache(gid);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"UpdateSpaceHintFromCache gid={gid}: {ex.Message}");
            }
        }

        /// <summary>
        /// Общая часть обеих подсказок: во время активной установки не вмешиваемся,
        /// для актуально установленной игры показываем готовый текст.
        /// Возвращает true, если вызывающему коду ещё нужно посчитать размер.
        /// </summary>
        private bool TryShowTrivialSpaceHint(string gid) {
            if (this.isUpdating) {
                return false; // не вмешиваемся в активный процесс
            }

            var g = this.games?.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
            if (g != null && g.IsInstalled && !g.NeedsUpdate) {
                this.FilesSizeText.Text = "Последняя версия игры уже установлена";
                return false;
            }

            return !string.IsNullOrWhiteSpace(gid);
        }

        // Сброс строки размера не должен сам стать источником исключения в обработчике ошибок
        private void SetFilesSizeTextSafe(string text) {
            try {
                this.FilesSizeText.Text = text;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"FilesSizeText недоступен: {ex.Message}");
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

        // Обновление списка игр по кнопке в заголовке секции
        private async void RefreshGames_Click(object sender, RoutedEventArgs e) {
            // Сохраним текущее выделение, чтобы не потерять контекст страницы игры
            var prevSelectedId = this.GetSelectedGameId();

            // Статусы будут пересчитаны заново — до этого момента считаем их неизвестными (C4)
            this.ResetVerifiedStatuses();
            this.ClearErrorDetails();
            try {
                this.GamesSkeleton.Visibility = Visibility.Visible;
                this.GameList.Visibility = Visibility.Collapsed;

                var gamesUrl = $"{this.BaseApi}/api/games";
                var gamesResp = await this.http.GetFromJsonAsync<GamesResponse>(gamesUrl);
                this.games = gamesResp?.Items ?? new List<GameInfo>();
                this.HideServerUnavailableState();
                this.NormalizeGameIconsAndLocalState(this.games);

                // Сортировка: установленные сначала, затем порядок, полученный от API
                this.RememberApiOrder(this.games);
                this.games = this.SortGames(this.games);
                this.GameList.ItemsSource = this.games;

                // Восстановим выбранную игру, если она осталась в списке
                if (!string.IsNullOrWhiteSpace(prevSelectedId)) {
                    var idxSel = this.games.FindIndex(g => string.Equals(g.GameId, prevSelectedId, StringComparison.OrdinalIgnoreCase));
                    if (idxSel >= 0) {
                        this.GameList.SelectedIndex = idxSel;
                    }
                }
            }
            catch (Exception ex) {
                if (this.games == null || this.games.Count == 0) {
                    // Показывать нечего — выводим пустое состояние с кнопкой «Повторить»
                    this.ShowServerUnavailableState(ex);
                }
                else {
                    this.ShowUserError("Не удалось обновить список игр. Проверьте подключение к интернету.", ex, "HomePage.RefreshGames_Click");
                }
            }
            finally {
                // finally выполняется без внешнего try: любой сбой здесь уронил бы async void-обработчик
                try {
                    this.GamesSkeleton.Visibility = Visibility.Collapsed;

                    // Список показываем только если не активно пустое состояние «сервер недоступен»
                    if (this.GamesEmptyState.Visibility != Visibility.Visible) {
                        this.GameList.Visibility = Visibility.Visible;
                    }

                    this.UpdateActionButtonState();
                }
                catch (Exception exUi) {
                    Core.Logging.Logger.Error(exUi, "RefreshGames_Click.finally");
                }
            }

            // Запустить асинхронную проверку статусов по манифесту
            this.GamesVerifyIndicator.Visibility = Visibility.Visible;
            await this.VerifyAllGamesStatusesAsync(this.GetSelectedGameId());

            // После обновления статусов — освежим подсказку по текущей игре из кэша
            try {
                // Если выделение потеряно после верификации — восстановим прежнее
                var gid = this.GetSelectedGameId();
                if (string.IsNullOrWhiteSpace(gid) && !string.IsNullOrWhiteSpace(prevSelectedId)) {
                    var idxSel2 = this.games.FindIndex(g => string.Equals(g.GameId, prevSelectedId, StringComparison.OrdinalIgnoreCase));
                    if (idxSel2 >= 0) {
                        this.GameList.SelectedIndex = idxSel2;
                        gid = prevSelectedId;
                    }
                }

                if (!string.IsNullOrWhiteSpace(gid)) {
                    // Выполним полный пересчёт требуемого места, чтобы сразу увидеть оценку.
                    // Отменять здесь нечего, но актуальность выбора метод всё равно проверит.
                    await this.UpdateSpaceHintAsync(gid, CancellationToken.None);
                    this.UpdateActionButtonState();
                }
            }
            catch (Exception ex) {
                // Список уже обновлён; подсказка о размере — необязательная доводка
                Core.Logging.Logger.Error(ex, "RefreshGames_Click.PostVerify");
            }
        }

        // Theme toggle and icon are now managed in MainWindow header
        private void ActionBtn_Click(object sender, RoutedEventArgs e) {
            // Идёт удаление файлов игры — начинать установку в ту же папку нельзя
            if (this.isDeleting) {
                this.ShowToast("Идёт удаление файлов игры. Дождитесь завершения.");
                return;
            }

            // Блокируем действия только пока не известен статус ВЫБРАННОЙ игры.
            // Проверка остальных игр в фоне работе не мешает (C4).
            if (!this.isUpdating && !this.IsGameStatusKnown(this.GetSelectedGameId())) {
                try {
                    this.StatusText.Text = "Проверяем файлы игры…";
                    this.UpdateProgress.IsIndeterminate = true;
                    this.SetActionMode(ActionMode.Checking);
                }
                catch (Exception ex) {
                    // Показать «Проверка…» не вышло — всё равно выходим, действие пока недоступно
                    Core.Logging.Logger.Error(ex, "ActionBtn_Click.CheckingState");
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
                    // Источник от прошлой установки уже отработал (сюда попадаем только
                    // при isUpdating == false) — освобождаем его, а не теряем.
                    var previous = this.cts;
                    this.cts = new CancellationTokenSource();
                    previous?.Dispose();
                    _ = this.StartUpdateAsync(this.cts.Token);
                    break;
            }
        }

        /// <summary>
        /// Одна точка отправки метрики установки/обновления: и успех, и отмена,
        /// и сбой уходят одинаково, отличаясь только полем result. Сама отправка
        /// не блокирует и не бросает, но обёртка стоит на случай, если однажды
        /// начнёт.
        /// </summary>
        private void ReportSyncMetric(
            string? gameId,
            string? version,
            bool isInstall,
            string result,
            System.Diagnostics.Stopwatch watch,
            long bytes) {
            try {
                var ms = (long)watch.Elapsed.TotalMilliseconds;
                if (isInstall) {
                    Core.Metrics.MetricsService.GameInstall(gameId, version, result, ms, bytes);
                }
                else {
                    Core.Metrics.MetricsService.GameUpdate(gameId, version, result, ms, bytes);
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"Метрика синхронизации не отправлена: {ex.Message}");
            }
        }

        private async Task StartUpdateAsync(CancellationToken token) {
            // Нужен и в catch: по нему запоминаем, к какой игре относится «Повторить»
            string? currentGid = null;

            // Для метрики: нужна и в catch, чтобы отличить отмену от сбоя.
            var metricsWatch = System.Diagnostics.Stopwatch.StartNew();
            var metricsIsInstall = false;
            string? metricsVersion = null;
            long metricsBytes = 0;
            try {
                if (this.GetSelectedGameId() is not string gid || string.IsNullOrWhiteSpace(gid)) {
                    this.StatusText.Text = "Не выбрана игра";
                    return;
                }

                currentGid = gid;

                // Всегда используем latest; список версий доступен только для просмотра
                var game = this.games.FirstOrDefault(g => g.GameId == gid);
                var version = game?.LatestVersion;
                if (string.IsNullOrWhiteSpace(version)) {
                    // Фолбэк: максимальная версия из списка сборок, если latest неизвестен
                    version = VersionOrder.SelectLatest(this.builds);
                }

                if (string.IsNullOrWhiteSpace(version)) {
                    this.StatusText.Text = "Нет доступных сборок для установки";
                    return;
                }

                // Подстраховка на случай, если работы начались уже после отрисовки кнопки
                var isInstall = game?.IsInstalled != true;
                metricsIsInstall = isInstall;
                metricsVersion = version;
                var maintenance = Core.Maintenance.MaintenanceService.Current;
                if (isInstall ? maintenance.BlocksInstall : maintenance.BlocksUpdate) {
                    this.StatusText.Text = maintenance.BuildBannerText();
                    this.UpdateActionButtonState();
                    return;
                }

                Core.Logging.Logger.Info($"StartUpdateAsync gid={gid} version={version}");

                this.isUpdating = true;
                this.hasUpdateError = false;
                this.updateErrorGameId = null;
                this.SetActionMode(ActionMode.Cancel);
                this.GameList.IsEnabled = false;
                this.UpdateProgress.Value = 0;
                this.FilesSizeText.Text = string.Empty;
                this.SpeedEtaText.Text = string.Empty;
                this.emaSpeedMBs = 0.0;

                var manifestUrl = IntegrityChecker.ManifestUrl(this.BaseApi, gid, version);
                var contentBase = IntegrityChecker.ContentBaseUrl(this.BaseApi, gid, version);

                this.StatusText.Text = "Загрузка манифеста...";
                this.UpdateProgress.IsIndeterminate = true;
                this.UpdateProgress.Value = 0;
                this.SpeedEtaText.Text = string.Empty;
                this.FilesSizeText.Text = string.Empty;
                Core.Logging.Logger.Info($"StartUpdateAsync fetching manifest {manifestUrl}");
                var manifest = await this.sync.GetManifestAsync(manifestUrl, token);
                this.StatusText.Text = "Проверка...";
                this.UpdateProgress.IsIndeterminate = true;
                var localRoot = GameLocalRoot(gid);
                var plan = await this.PlanOffUiThreadAsync(manifest, localRoot, contentBase, token);
                Core.Logging.Logger.Info($"StartUpdateAsync plan: downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count} emptyDirs={plan.EmptyDirsToCreate.Count}");
                LogPlanDownloads(gid, "update", plan, localRoot);

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
                        this.UpdateActionButtonState();
                        return;
                    }
                }
                catch (Exception ex) {
                    // Не смогли оценить свободное место (сетевой диск, необычный путь).
                    // Это не повод отменять установку: пойдём дальше, ошибку записи поймает сам sync.
                    Core.Logging.Logger.Warn($"StartUpdateAsync: оценка свободного места не удалась: {ex.Message}");
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
                            this.UpdateActionButtonState();
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
                                this.SpeedEtaText.Text = $"Скорость: {this.emaSpeedMBs:0.0} МБ/с • Осталось: {FormatEta(etaSec)}";
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

                metricsBytes = plan.TotalDownloadBytes;
                await this.sync.ExecuteAsync(plan, prog, token);
                Core.Logging.Logger.Info($"StartUpdateAsync execute done gid={gid} version={version}");

                this.ReportSyncMetric(gid, metricsVersion, metricsIsInstall, "ok", metricsWatch, metricsBytes);

                this.StatusText.Text = "Готово";
                this.SpeedEtaText.Text = string.Empty;
                this.FilesSizeText.Text = "Последняя версия игры уже установлена"; // показываем итоговый статус

                // Сохраним версию в локальный маркер и отметим игру установленной
                WriteLocalVersion(gid, version);
                this.MarkInstalled(gid, version);

                // Обновим кэш: для установленной последней версии скачивание не требуется
                this.spaceHint.Remember(gid, 0);
                this.GameList.Items.Refresh();

                // Зафиксируем текущий выбор на UI-потоке
                var selectedIdAfterUpdate = this.GetSelectedGameId();

                // Дополнительно перепроверим статус игры по манифесту и обновим список в фоне,
                // чтобы не блокировать UI-поток сразу после завершения обновления
                _ = Task.Run(async () => {
                    try {
                        await this.VerifyGameStatusAsync(this.games.FirstOrDefault(x => x.GameId == gid) ?? new GameInfo { GameId = gid, LatestVersion = version ?? string.Empty });
                        var reordered = this.SortGames(this.games);
                        await this.DispatcherInvokeAsync(() => {
                            this.games = reordered;
                            this.GameList.ItemsSource = this.games;
                            this.GameList.Items.Refresh();
                            if (!string.IsNullOrWhiteSpace(selectedIdAfterUpdate)) {
                                var idx = this.games.FindIndex(x => x.GameId == selectedIdAfterUpdate);
                                if (idx >= 0) {
                                    this.GameList.SelectedIndex = idx;
                                }
                            }

                            // После изменения источника данных — обновим состояние кнопки
                            this.UpdateActionButtonState();
                        });
                    }
                    catch (Exception ex) {
                        // Игра уже установлена и версия записана: это лишь фоновая пересортировка списка
                        Core.Logging.Logger.Error(ex, "StartUpdateAsync.PostInstallRefresh");
                    }
                });

                // Создание ярлыка: параметры считаем на UI-потоке, а COM-вызов делаем в STA-потоке
                try {
                    string? shortcutTitle = null;
                    string? shortcutExe = null;
                    var gLocal = this.games.FirstOrDefault(g => g.GameId == gid);
                    if (gLocal != null && !string.IsNullOrWhiteSpace(gLocal.ExeRelativePath)) {
                        var rel = gLocal.ExeRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);
                        shortcutExe = System.IO.Path.Combine(localRoot, rel);
                        shortcutTitle = string.IsNullOrWhiteSpace(gLocal.Title) ? gid : gLocal.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(shortcutExe) && File.Exists(shortcutExe)) {
                        // TryCreateDesktopShortcut гасит собственные ошибки: ярлык не критичен для установки
                        var t = new System.Threading.Thread(() => TryCreateDesktopShortcut(shortcutTitle!, shortcutExe!));
                        t.IsBackground = true;
                        try {
                            t.SetApartmentState(System.Threading.ApartmentState.STA);
                        }
                        catch (Exception exSta) {
                            // Поток уже запущен/состояние занято — попробуем создать ярлык как есть
                            Core.Logging.Logger.Warn($"StartUpdateAsync: не удалось выставить STA для потока ярлыка: {exSta.Message}");
                        }

                        t.Start();
                    }
                }
                catch (Exception ex) {
                    // Ярлык на рабочем столе — приятная мелочь, установка уже завершена успешно
                    Core.Logging.Logger.Warn($"StartUpdateAsync: ярлык не создан: {ex.Message}");
                }
            }
            catch (OperationCanceledException) {
                this.StatusText.Text = "Операция отменена пользователем.";
                this.SpeedEtaText.Text = string.Empty;
                this.UpdateProgress.IsIndeterminate = false;
                this.UpdateProgress.Value = 0;
                this.ReportSyncMetric(currentGid, metricsVersion, metricsIsInstall, "cancel", metricsWatch, metricsBytes);
            }
            catch (ManifestValidationException ex) {
                // Манифест отклонён проверкой структуры — это не сетевой сбой.
                // Ни одного файла игры мы ещё не тронули, и не тронем: показываем отдельный текст.
                this.hasUpdateError = true;
                this.updateErrorGameId = currentGid;
                this.ShowUserError(ManifestValidator.UserMessage, ex, "HomePage.StartUpdateAsync.ManifestValidation");
                this.ReportSyncMetric(currentGid, metricsVersion, metricsIsInstall, "fail", metricsWatch, metricsBytes);
                Core.Metrics.MetricsService.Error("manifest_invalid", currentGid);
            }
            catch (Exception ex) {
                this.hasUpdateError = true;
                this.updateErrorGameId = currentGid;
                var userMessage = ex is IOException
                    ? "Не удалось записать файлы игры. Проверьте свободное место и права доступа."
                    : "Не удалось завершить обновление. Попробуйте ещё раз.";
                this.ShowUserError(userMessage, ex, "HomePage.StartUpdateAsync");
                this.ReportSyncMetric(currentGid, metricsVersion, metricsIsInstall, "fail", metricsWatch, metricsBytes);

                // Код классифицирует сбой по типу исключения. Текст исключения НЕ отправляем:
                // в нём регулярно встречаются пути с именем пользователя.
                Core.Metrics.MetricsService.Error(ex is IOException ? "sync_io" : "sync_failed", currentGid);
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
                this.UpdateActionButtonState();
            }
        }

        // --- Управление видимостью кнопок действий (Обновить/Играть) ---
        private GameInfo? GetSelectedGame() {
            try {
                if (this.GetSelectedGameId() is string gid && !string.IsNullOrWhiteSpace(gid)) {
                    return this.games.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex) {
                // Вызывается из фоновых задач, где список игр может пересобираться параллельно
                Core.Logging.Logger.Warn($"GetSelectedGame: {ex.Message}");
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
                        this.ApplyActionButtonStyle("Style.ActionButton.Cancel");
                        break;
                    case ActionMode.Checking:
                        this.ActionBtn.Content = "Проверка…";
                        this.ActionBtn.IsEnabled = false;
                        this.ApplyActionButtonStyle("Style.ActionButton.Checking");
                        break;
                    case ActionMode.Play:
                        this.ActionBtn.Content = "Играть";
                        this.ActionBtn.IsEnabled = true;
                        this.ApplyActionButtonStyle("Style.ActionButton.Play");
                        break;
                    case ActionMode.Retry:
                        this.ActionBtn.Content = "Повторить";
                        this.ActionBtn.IsEnabled = true;
                        this.ApplyActionButtonStyle("Style.ActionButton.Retry");
                        break;
                    case ActionMode.Maintenance:
                        // Причина и сроки — в баннере шапки, на кнопке только суть запрета
                        this.ActionBtn.Content = "Технические работы";
                        this.ActionBtn.IsEnabled = false;
                        this.ApplyActionButtonStyle("Style.ActionButton.Checking");
                        break;
                    case ActionMode.Install:
                        this.ActionBtn.Content = "Установить";
                        this.ActionBtn.IsEnabled = true;
                        this.ApplyActionButtonStyle("Style.ActionButton.Install");
                        break;
                    case ActionMode.Update:
                    default:
                        this.ActionBtn.Content = "Обновить";
                        this.ActionBtn.IsEnabled = true;
                        this.ApplyActionButtonStyle("Style.ActionButton.Update");
                        break;
                }
            }
            catch (Exception ex) {
                // Кнопка действия — центральный элемент экрана: не даём сбою оформления уронить страницу
                Core.Logging.Logger.Error(ex, $"SetActionMode({mode})");
            }
        }

        // Стиль кнопки берём из темы; если ресурс не найден, оставляем оформление по умолчанию
        private void ApplyActionButtonStyle(string styleKey) {
            if (this.TryFindResource(styleKey) is Style style) {
                this.ActionBtn.Style = style;
            }
            else {
                Core.Logging.Logger.Warn($"Стиль '{styleKey}' не найден в теме, кнопка действия останется с оформлением по умолчанию");
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

                // Ждём только проверку выбранной игры, а не всего списка (C4)
                if (!this.IsGameStatusKnown(g?.GameId)) {
                    this.SetActionMode(ActionMode.Checking);
                    return;
                }

                // Статус стал известен — снимаем «бегущую» полосу, которую включает клик по
                // кнопке во время проверки. Раньше её никто не выключал, и прогресс-бар
                // бежал до конца сессии, изображая несуществующую работу.
                if (!this.isUpdating && !this.isDeleting) {
                    this.UpdateProgress.IsIndeterminate = false;
                }

                // Сначала решаем, что вообще предложить пользователю, и только потом сверяемся
                // с режимом технических работ: так запрет не «съедает» логику состояний.
                var unfinished = HasUnfinishedUpdate(g?.GameId);
                ActionMode intended;
                if (this.hasUpdateError) {
                    intended = ActionMode.Retry;
                }
                else if (unfinished) {
                    // Осталось незавершённое обновление — «Играть» не предлагаем, нужно докатить (C2)
                    intended = ActionMode.Update;
                }
                else if (isInstalled && !needsUpdate) {
                    intended = ActionMode.Play;
                }
                else {
                    // Не установлена или требует обновления
                    intended = isInstalled ? ActionMode.Update : ActionMode.Install;
                }

                if (IsBlockedByMaintenance(intended)) {
                    this.SetActionMode(ActionMode.Maintenance);
                    this.StatusText.Text = Core.Maintenance.MaintenanceService.Current.BuildBannerText();
                    return;
                }

                this.SetActionMode(intended);
                if (intended == ActionMode.Update && unfinished && !this.isUpdating) {
                    this.StatusText.Text = "Обновление не завершено. Нажмите «Обновить», чтобы восстановить игру.";
                }
            }
            catch (Exception ex) {
                // Метод дёргается отовсюду (в т.ч. из фоновых задач) — он обязан быть безопасным
                Core.Logging.Logger.Error(ex, "UpdateActionButtonState");
            }
        }

        /// <summary>
        /// Запрещено ли задуманное действие текущим режимом технических работ.
        /// «Повторить» приравниваем к обновлению: за ним стоит та же закачка.
        /// </summary>
        private static bool IsBlockedByMaintenance(ActionMode mode) {
            var state = Core.Maintenance.MaintenanceService.Current;
            return mode switch {
                ActionMode.Install => state.BlocksInstall,
                ActionMode.Update or ActionMode.Retry => state.BlocksUpdate,
                ActionMode.Play => state.BlocksPlay,
                _ => false,
            };
        }

        // Режим техработ может включиться и выключиться, пока страница открыта.
        // Подписываемся на время видимости страницы, чтобы не держать ссылку на неё в статическом событии.
        private bool maintenanceSubscribed;

        // Авто-отчёты об ошибках: как и режим техработ, события статические,
        // поэтому подписка живёт ровно столько, сколько страница показана.
        private bool errorReporterSubscribed;

        private void SubscribeErrorReporter() {
            if (this.errorReporterSubscribed) {
                return;
            }

            Core.ErrorReporter.AutoReported += this.OnAutoReported;
            Core.ErrorReporter.AutoReportSuppressed += this.OnAutoReportSuppressed;
            this.errorReporterSubscribed = true;
        }

        private void UnsubscribeErrorReporter() {
            if (!this.errorReporterSubscribed) {
                return;
            }

            Core.ErrorReporter.AutoReported -= this.OnAutoReported;
            Core.ErrorReporter.AutoReportSuppressed -= this.OnAutoReportSuppressed;
            this.errorReporterSubscribed = false;
        }

        private void OnAutoReported(string context) =>
            _ = this.DispatcherInvokeAsync(() => this.ShowToast("Произошла ошибка. Отчёт автоматически отправлен"));

        private void OnAutoReportSuppressed(TimeSpan retryAfter) {
            var mins = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
            _ = this.DispatcherInvokeAsync(() => this.ShowToast($"Лимит авто-репортов исчерпан. Доступно через ~{mins} мин."));
        }

        private void SubscribeMaintenance() {
            if (this.maintenanceSubscribed) {
                return;
            }

            Core.Maintenance.MaintenanceService.Changed += this.OnMaintenanceChanged;
            this.maintenanceSubscribed = true;
        }

        private void UnsubscribeMaintenance() {
            if (!this.maintenanceSubscribed) {
                return;
            }

            Core.Maintenance.MaintenanceService.Changed -= this.OnMaintenanceChanged;
            this.maintenanceSubscribed = false;
        }

        // Сервер сообщил о смене режима: работы начались или закончились.
        // Перезапуск клиента не нужен — просто пересчитываем кнопку.
        private void OnMaintenanceChanged(Core.Maintenance.MaintenanceState state) {
            try {
                this.UpdateActionButtonState();
                if (!state.Enabled && !this.isUpdating && string.IsNullOrWhiteSpace(this.lastErrorDetails)) {
                    this.StatusText.Text = "Готово";
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "HomePage.OnMaintenanceChanged");
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

                // Сервер может запретить и запуск (например, работы на игровых серверах)
                var maintenance = Core.Maintenance.MaintenanceService.Current;
                if (maintenance.BlocksPlay) {
                    this.StatusText.Text = maintenance.BuildBannerText();
                    this.UpdateActionButtonState();
                    return;
                }

                // Предыдущее обновление не довели до конца — файлы игры смешаны из двух версий (C2)
                if (HasUnfinishedUpdate(gid)) {
                    this.StatusText.Text = "Обновление не завершено. Нажмите «Обновить», чтобы восстановить игру.";
                    this.UpdateActionButtonState();

                    return;
                }

                // Запомним последнюю запущенную игру
                var cfg = ChillHub.Core.ConfigService.Current;
                cfg.LastGameId = gid;
                ChillHub.Core.ConfigService.Save(cfg);
                var localRoot = GameLocalRoot(gid);
                var rel = game.ExeRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);
                var exePath = System.IO.Path.Combine(localRoot, rel);
                if (!System.IO.File.Exists(exePath)) {
                    // Пользователю — короткое объяснение, путь оставляем в подсказке и логе (C5)
                    this.ShowUserError(
                        "Файлы игры повреждены или неполные. Нажмите «Обновить», чтобы восстановить.",
                        null,
                        $"PlaySelectedGame: не найден исполняемый файл '{exePath}'");
                    return;
                }

                var psi = new ProcessStartInfo {
                    FileName = exePath,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? localRoot,
                    UseShellExecute = true,
                };
                Process.Start(psi);

                ChillHub.Core.Metrics.MetricsService.GameLaunch(game.GameId, game.LatestVersion);

                // Discord Rich Presence: полностью опционален и не должен влиять на запуск.
                // Пока Application ID не задан владельцем — вызов сразу выходит.
                ChillHub.Core.DiscordRichPresence.SetPlaying(game.Title, game.LatestVersion);

                this.UpdateActionButtonState();
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось запустить игру.", ex, "HomePage.PlaySelectedGame");
            }
        }

        /// <summary>
        /// Идентификатор выбранной игры. Вызывать ТОЛЬКО с UI-потока: SelectedItem принадлежит
        /// GameList, и обращение из фона бросает исключение. Фоновым задачам следует читать выбор
        /// через <see cref="DispatcherInvokeAsync(Action)"/>.
        /// </summary>
        private string? GetSelectedGameId() {
            try {
                var gi = this.GameList?.SelectedItem as GameInfo;
                return gi?.GameId;
            }
            catch (Exception ex) {
                // Молча вернуть null нельзя: вызывающий примет это за «игра не выбрана» и
                // потеряет выделение. Фиксируем в логе, чтобы такой вызов было видно.
                Core.Logging.Logger.Warn($"GetSelectedGameId: выбор недоступен (обращение не с UI-потока?): {ex.Message}");
                return null;
            }
        }

        // Обработчики остаются здесь: на их имена ссылается XAML. Вся логика — в Core/Home/ImageLoader.
        private void CoverImg_Loaded(object sender, RoutedEventArgs e) {
            if (sender is not Image img) {
                return;
            }

            ImageLoader.AttachAndLoad(img, this.BaseApi);
        }

        private void CoverImg_ImageFailed(object sender, ExceptionRoutedEventArgs e) {
            if (sender is not Image img) {
                return;
            }

            ImageLoader.HandleImageFailed(img, e.ErrorException);
        }

        // Пункты работы с файлами доступны только для установленных игр.
        // Сам пункт «Подробнее об игре» доступен всегда — страница игры полезна и до установки.
        private void GameItem_ContextMenuOpening(object sender, ContextMenuEventArgs e) {
            try {
                var fe = sender as FrameworkElement;
                var gi = fe?.DataContext as GameInfo;
                var gid = gi?.GameId;
                if (string.IsNullOrWhiteSpace(gid)) {
                    e.Handled = true;
                    return;
                }

                var localRoot = GameLocalRoot(gid);
                var hasFiles = Directory.Exists(localRoot) && HasAnyLocalGameFiles(localRoot);

                if (fe?.ContextMenu != null) {
                    foreach (var raw in fe.ContextMenu.Items) {
                        // Первый пункт («Подробнее об игре») оставляем активным всегда
                        if (raw is MenuItem mi && !ReferenceEquals(mi, fe.ContextMenu.Items.Count > 0 ? fe.ContextMenu.Items[0] : null)) {
                            mi.IsEnabled = hasFiles;
                        }
                    }
                }
            }
            catch (Exception ex) {
                // Не смогли подготовить меню — лучше его не показывать вовсе
                Core.Logging.Logger.Warn($"GameItem_ContextMenuOpening: {ex.Message}");
                e.Handled = true;
            }
        }

        // --- Переход на страницу игры (задача 24) ---
        private void GameDetails_Click(object sender, RoutedEventArgs e) {
            var gi = (sender as FrameworkElement)?.GetValue(MenuItem.CommandParameterProperty) as GameInfo
                     ?? (sender as FrameworkElement)?.DataContext as GameInfo
                     ?? this.GetSelectedGame();
            this.OpenGamePage(gi);
        }

        private void GameList_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            // Двойной клик по пустому месту списка игнорируем: интересует только строка игры
            var source = e.OriginalSource as DependencyObject;
            var item = FindAncestorListBoxItem(source);
            if (item?.DataContext is GameInfo gi) {
                e.Handled = true;
                this.OpenGamePage(gi);
            }
        }

        private static ListBoxItem? FindAncestorListBoxItem(DependencyObject? node) {
            while (node != null) {
                if (node is ListBoxItem lbi) {
                    return lbi;
                }

                node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }

        private void OpenGamePage(GameInfo? game) {
            try {
                if (game == null || string.IsNullOrWhiteSpace(game.GameId)) {
                    this.StatusText.Text = "Не удалось определить игру";
                    return;
                }

                // Синхронизируем выделение, чтобы после возврата действия относились к той же игре
                if (!ReferenceEquals(this.GameList.SelectedItem, game)) {
                    this.GameList.SelectedItem = game;
                }

                var win = Window.GetWindow(this) as ChillHub.MainWindow;
                win?.ContentFrame.Navigate(new GamePage(game));
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось открыть страницу игры.", ex, "HomePage.OpenGamePage");
            }
        }

        // Возврат со страницы игры или из настроек: перечитываем локальные маркеры версий
        private async void HomePage_Loaded(object sender, RoutedEventArgs e) {
            if (!this.loadedOnce) {
                this.loadedOnce = true;
                return;
            }

            // Папку для игр могли сменить в настройках: тогда все статусы, оценки объёма
            // и кеш «проверено» относятся к прежнему каталогу и врут до перезапуска.
            var gamesPath = ChillHub.Core.ConfigService.Current.GamesPath ?? string.Empty;
            var gamesPathChanged = !string.Equals(this.knownGamesPath, gamesPath, StringComparison.OrdinalIgnoreCase);
            if (gamesPathChanged) {
                this.knownGamesPath = gamesPath;
                this.spaceHint.Clear();
                this.ResetVerifiedStatuses();
                this.FilesSizeText.Text = string.Empty;
                Core.Logging.Logger.Info($"HomePage: папка игр изменилась на '{gamesPath}', статусы пересчитываются");
            }

            if (!gamesPathChanged && !GamePage.ConsumeLocalStateChanged()) {
                return;
            }

            try {
                var snapshot = this.games?.ToList() ?? new List<GameInfo>();
                await Task.Run(() => this.NormalizeGameIconsAndLocalState(snapshot));
                this.GameList.Items.Refresh();
                this.UpdateActionButtonState();

                if (gamesPathChanged && this.allowFileChecks) {
                    // Полная перепроверка по манифестам — в фоне, чтобы не морозить возврат из настроек
                    string? gid = this.GetSelectedGameId();
                    _ = Task.Run(() => this.VerifyAllGamesStatusesAsync(gid));
                }
            }
            catch (Exception ex) {
                // Список останется прежним до следующего обновления вручную — не критично
                Core.Logging.Logger.Error(ex, "HomePage.HomePage_Loaded");
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
                    var ver = ReadLocalVersion(g.GameId);
                    var verTrimmed = string.IsNullOrWhiteSpace(ver) ? string.Empty : ver.Trim();
                    g.IsInstalled = !string.IsNullOrWhiteSpace(verTrimmed);
                    g.InstalledVersion = verTrimmed ?? string.Empty;

                    // Compute needs update: installed and latest known but different
                    g.NeedsUpdate = g.IsInstalled && !string.IsNullOrWhiteSpace(g.LatestVersion) &&
                                     !string.Equals(g.InstalledVersion?.Trim(), g.LatestVersion?.Trim(), StringComparison.OrdinalIgnoreCase);

                    // Прерванное обновление: игра гарантированно требует восстановления (C2)
                    if (HasUnfinishedUpdate(g.GameId)) {
                        g.NeedsUpdate = true;
                    }

                    ChillHub.Core.Logging.Logger.Info($"NormalizeState gid={g.GameId} latest='{g.LatestVersion}' local='{g.InstalledVersion}' isInstalled={g.IsInstalled} needsUpdate={g.NeedsUpdate}");
                }
                catch (Exception ex) {
                    // Одна игра с некорректными данными не должна ломать нормализацию всего списка
                    ChillHub.Core.Logging.Logger.Error(ex, $"NormalizeGameIconsAndLocalState(gid={g?.GameId})");
                }
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
            catch (Exception ex) {
                // Версия на диске уже записана; здесь только обновление отображения
                Core.Logging.Logger.Error(ex, $"MarkInstalled(gid={gameId})");
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

                this.games = this.SortGames(this.games);
                this.GameList.ItemsSource = this.games;
                this.GameList.Items.Refresh();
                if (!string.IsNullOrWhiteSpace(selectedId)) {
                    var idx = this.games.FindIndex(x => x.GameId == selectedId);
                    if (idx >= 0) {
                        this.GameList.SelectedIndex = idx;
                    }
                }
            }
            catch (Exception ex) {
                // Файлы уже удалены; здесь только пересборка списка — ошибку показываем в статусе
                this.ShowUserError("Список игр обновится после перезапуска лаунчера.", ex, $"MarkUninstalled(gid={gameId})");
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
                var localRoot = GameLocalRoot(gid);
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
                this.ShowUserError("Не удалось открыть папку игры.", ex, "HomePage.OpenGameFolder_Click");
            }
        }

        private async void DeleteGame_Click(object sender, RoutedEventArgs e) {
            try {
                // Установка идёт прямо сейчас: Directory.Delete снёс бы файлы из-под
                // работающей закачки, а сама закачка продолжила бы писать в удаляемую папку.
                if (this.isUpdating) {
                    this.ShowToast("Идёт установка или обновление. Дождитесь завершения или нажмите «Отмена».");
                    return;
                }

                if (this.isDeleting) {
                    return; // удаление уже запущено — второй проход не нужен
                }

                var gi = (sender as FrameworkElement)?.GetValue(MenuItem.CommandParameterProperty) as GameInfo
                         ?? (sender as FrameworkElement)?.DataContext as GameInfo;
                var gid = gi?.GameId;
                if (string.IsNullOrWhiteSpace(gid)) {
                    this.StatusText.Text = "Не удалось определить игру";
                    return;
                }
                var localRoot = GameLocalRoot(gid);

                // Переключаем текущий выбор на удаляемую игру, чтобы область действий и статусы относились к ней
                if (gi != null) {
                    this.GameList.SelectedItem = gi;
                }

                // Подтверждение удаления (кастомный диалог в стиле темы)
                var title = string.IsNullOrWhiteSpace(gi?.Title) ? gid : gi!.Title;
                if (!HomeDialogs.ConfirmDeleteGameFiles(this, title!, localRoot)) {
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
                catch (Exception ex) {
                    // Не удалось опросить процессы — не блокируем удаление, файлы всё равно защищены самой ОС
                    Core.Logging.Logger.Warn($"DeleteGame_Click: проверка запущенного процесса не выполнена: {ex.Message}");
                }

                // Пытаемся удалить папку целиком.
                // Обход дерева на десятки тысяч файлов занимает секунды и минуты на HDD,
                // поэтому сама операция уходит в фон, а окно показывает индикатор.
                this.isDeleting = true;
                this.GameList.IsEnabled = false;
                this.ActionBtn.IsEnabled = false;
                this.StatusText.Text = $"Удаление файлов {title}…";
                this.UpdateProgress.IsIndeterminate = true;
                try {
                    // Directory.Delete(recursive) обрывается на ПЕРВОМ занятом файле, когда
                    // остальное уже снесено. Пользователь при этом видел «не удалось удалить»,
                    // игра продолжала числиться установленной — а на диске лежали её остатки,
                    // неспособные запуститься. Поэтому удаляем сами, по файлу, и доводим до
                    // конца: занятые собираем в список и потом честно называем.
                    var blocked = await Task.Run(() => DeleteGameFiles(localRoot));

                    ChillHub.Core.Sync.FileHashCache.Remove(gid);
                    this.spaceHint.Remember(gid, 0);
                    this.FilesSizeText.Text = string.Empty;

                    // Состояние обновляем в любом случае: игра с вырванными файлами не
                    // запустится, и показывать её установленной — врать пользователю.
                    this.MarkUninstalled(gid);

                    if (blocked.Count > 0) {
                        var names = string.Join(", ", blocked.Take(3).Select(Path.GetFileName));
                        var tail = blocked.Count > 3 ? $" и ещё {blocked.Count - 3}" : string.Empty;
                        this.ShowUserError(
                            $"Файлы игры удалены частично: {blocked.Count} шт. заняты другой программой ({names}{tail}). "
                            + "Закройте игру, лаунчеры модов и антивирусную проверку, затем удалите ещё раз. "
                            + "До этого игра работать не будет.",
                            null,
                            $"HomePage.DeleteGame_Click: {blocked.Count} файлов заняты");
                        return;
                    }
                }
                catch (Exception exDel) {
                    this.ShowUserError(
                        "Не удалось удалить файлы игры. Возможно, они заняты другой программой.",
                        exDel,
                        "HomePage.DeleteGame_Click");
                    return;
                }
                finally {
                    this.isDeleting = false;
                    this.GameList.IsEnabled = true;
                    this.UpdateProgress.IsIndeterminate = false;
                    this.UpdateActionButtonState();
                }

                // Перепроверим статусы игр (легко и асинхронно)
                await this.VerifyAllGamesStatusesAsync();

                // Покажем ненавязчивый Toast вместо изменения строки статуса
                this.ShowToast($"Локальные файлы {title} удалены");
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось удалить файлы игры.", ex, "HomePage.DeleteGame_Click");
            }
        }


    }
}
