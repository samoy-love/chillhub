// <copyright file="HomePage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
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
    using System.Windows.Controls.Primitives;
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

        /// <summary>Завершается, когда каталог игр загружен, — см. <see cref="gamesLoaded"/>.</summary>
        internal Task GamesLoaded => this.gamesLoaded.Task;

        /// <summary>Показанный список игр: ярлыку нужно знать, есть ли в нём его игра.</summary>
        internal IReadOnlyList<GameInfo> Games => this.games;

        private readonly HttpClient http = HttpClientProvider.Shared;
        private List<GameInfo> games = new();

        /// <summary>
        /// Каталог игр загружен: список либо заполнен ответом сервера, либо остался пустым
        /// (сервер недоступен). Ждёт этого ярлык с рабочего стола: он приходит выделить
        /// конкретную игру сразу после запуска лаунчера, когда списка ещё нет, и без
        /// ожидания любая игра выглядела бы для него пропавшей из каталога.
        /// </summary>
        private readonly TaskCompletionSource<bool> gamesLoaded =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private List<string> builds = new();
        // Идёт удаление локальных файлов игры: блокирует повторный запуск и установку
        private bool isDeleting = false;

        // Идёт установка модпака в копию из Steam. Второй такой же запуск писал бы в ту
        // же папку теми же файлами параллельно с первым — отсюда флаг, а не просто
        // выключенный пункт меню: меню строится заново на каждое открытие.
        private bool steamModsInstalling;

        // 1, пока идёт проход VerifyAllGamesStatusesAsync. Взводится через Interlocked:
        // метод зовут и из UI-потока, и из фоновых задач.
        private int verifyRunning;

        // Загрузка данных выбранной игры: сериализуется воротами, предыдущая отменяется токеном.
        private readonly SemaphoreSlim selectionGate = new(1, 1);
        private CancellationTokenSource? selectionCts;

        // Порядок игр от API и единое правило сортировки — в Core/Home/GameCatalog.
        private readonly GameCatalog catalog = new();
        private readonly ISyncService sync = new SimpleSyncService();

        // Кэш оценок требуемого объёма скачивания по игре (обновляется при VerifyGameStatusAsync)
        private readonly SpaceHint spaceHint = new();

        // Раньше здесь был флаг initialVerifyRunning, блокировавший любые действия на время
        // полной проверки всех игр. Теперь блокировка точечная: см. verifiedGameIds (C4).

        // Разрешение на тяжёлые проверки файлов (Plan/Execute). На старте запрещено, включаем после первичного рендеринга
        private volatile bool allowFileChecks = false;

        // Игры, чей статус уже проверен в этой сессии (C4). Набор — в Core/Home/VerifiedGames.
        private readonly VerifiedGames verified = new();

        // Сама проверка статуса по манифесту — в Core/Home/GameStatusVerifier,
        // режим и оформление единой кнопки действия — в Core/Home/ActionButtonState.
        private GameStatusVerifier? statusVerifier;

        private GameStatusVerifier Verifier => this.statusVerifier ??=
            new GameStatusVerifier(this.sync, () => this.BaseApi, this.spaceHint, this.verified);

        // Очередь загрузок: один экземпляр на всё время жизни страницы —
        // HomePage кешируется приложением, поэтому очередь переживает переходы на GamePage
        // и обратно, и уже стоящие в ней закачки не обрываются при уходе со страницы.
        private readonly Core.Game.DownloadQueue downloadQueue;
        private readonly System.Collections.ObjectModel.ObservableCollection<Core.Game.QueueItem> queueDockItems = new();

        /// <summary>
        /// Что из очереди реально показано внизу экрана: первые несколько позиций, сколько
        /// разрешил <see cref="Core.UI.QueueDockLayout"/>. Отдельный список, а не потолок
        /// высоты у дока: прежний потолок в долю окна оставлял на низком окне полторы
        /// строки со скроллом, и очередь из четырёх позиций выглядела как очередь из двух.
        /// </summary>
        private readonly System.Collections.ObjectModel.ObservableCollection<Core.Game.QueueItem> queueDockVisibleItems = new();

        /// <summary>Док раскрыт кликом по «Показать ещё N» — видны все позиции очереди.</summary>
        private bool queueDockExpanded;

        /// <summary>
        /// Когда строку очереди последний раз перерисовывали, по идентификатору игры.
        /// Нужен, чтобы отчёты о ходе закачки не пересобирали строку десять раз в секунду
        /// (см. <see cref="Core.UI.QueueDockLayout.ShouldRefreshRow"/>).
        /// </summary>
        private readonly Dictionary<string, long> rowRefreshedAt = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Та же очередь — наружу для GamePage: установка/обновление со страницы игры идёт через
        /// неё, а не через отдельный локальный запуск (см. GamePage.StartQueuedSync), иначе
        /// закачка обрывалась при уходе с этой страницы на главную.
        /// </summary>
        internal Core.Game.IDownloadQueue DownloadQueue => this.downloadQueue;

        // Галерея игры: тот же приём кеша в памяти, что и у остальных
        // content-загрузчиков страницы — не перезапрашивать gallery.json на каждый выбор игры.
        private readonly Core.Game.GalleryClient galleryClient = new();
        private CancellationTokenSource? galleryCts;

        // Технические подробности последней ошибки: в статусе показываем короткий текст,
        // детали уходят в лог и в подсказку к строке статуса (C5).
        private string lastErrorDetails = string.Empty;

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

        private async void FbSend_Click(object sender, RoutedEventArgs e) {
            try {
                var comment = this.FbComment.Text?.Trim() ?? string.Empty;
                if (comment.Length < 5) {
                    this.FbStatus.Text = "Опишите проблему (мин. 5 символов)";
                    return;
                }

                // Диагностика уходит ВСЕГДА и безусловно. Отключаемая галочка не сработала:
                // её снимали именно в тех обращениях, где без логов ответить нечего, и
                // разбор упирался в переписку «пришлите ещё раз, но с галочкой». Приватность
                // держится не выбором пользователя, а составом бандла: имя пользователя
                // Windows в путях заменяется на %USER% (см. Diagnostics.Redact).
                var draft = new FeedbackService.FeedbackDraft(
                    this.FbName.Text?.Trim() ?? string.Empty,
                    this.FbContact.Text?.Trim() ?? string.Empty,
                    this.GetFeedbackTypeString(),
                    comment,
                    true,
                    FeedbackService.CollectSystemInfo());

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

        /// <summary>
        /// Что сейчас нарисовано в строке действий витрины: по нему меню под стрелкой
        /// знает, какие варианты уже стоят кнопками и повторять их не нужно.
        /// </summary>
        private Core.Mods.LaunchBarView? launchBar;

        /// <summary>Снимок вариантов запуска: за ним реестр и файловая система.</summary>
        private readonly Core.Mods.LaunchOptionsCache launchOptionsCache = new();

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

        /// <summary>Список обновлений в этом запуске уже показывали (или решили не показывать).</summary>
        private bool changelogChecked;

        /// <summary>
        /// Папка для игр, к которой относятся текущие статусы. Её могли сменить в настройках,
        /// и тогда всё показанное состояние относится к другому каталогу.
        /// </summary>
        private string knownGamesPath = ChillHub.Core.ConfigService.Current.GamesPath ?? string.Empty;

        public HomePage() {
            this.InitializeComponent();

            // Самообновление обрабатывается отдельным окном UpdateWindow до показа MainWindow
            _ = this.StartupAsync();

            this.downloadQueue = new Core.Game.DownloadQueue(
                gid => this.games.FirstOrDefault(g => string.Equals(g.GameId, gid, StringComparison.OrdinalIgnoreCase)),
                () => this.BaseApi,
                syncServiceFactory: null,
                confirm: AskFromQueue);
            this.QueueDock.ItemsSource = this.queueDockVisibleItems;

            // Очередь работает в фоне, а спрашивать можно только с UI-потока: Invoke
            // блокирует её до ответа — этого и надо, решение принимает человек.
            bool AskFromQueue(string text, string caption) =>
                this.Dispatcher.Invoke(() => Core.Home.HomeDialogs.AskYesNo(text, caption));

            // Незакрытые сессии прошлого запуска: досмотреть те игры, что ещё бегут, и
            // закрыть остальные. Заодно возвращает в состояние без модов папки, в которых
            // их включал прошлый запуск, — покой чужой папки в Steam всегда ванильный.
            Core.Game.PlaytimeStore.EnsureStarted();

            // Сколько строк очереди влезает, зависит от высоты окна: на низком остаётся
            // только качающаяся. Пересчитываем на каждое изменение размера — иначе окно,
            // растянутое мышью, продолжало бы показывать одну строку из четырёх.
            this.SizeChanged += (s, e) => this.SyncQueueDockRows();
            this.downloadQueue.ItemAdded += this.OnQueueItemChanged;
            this.downloadQueue.ItemProgress += this.OnQueueItemChanged;
            this.downloadQueue.ItemCompleted += this.OnQueueItemCompleted;
            this.downloadQueue.ItemRemoved += this.OnQueueItemRemoved;
            this.downloadQueue.Reordered += this.OnQueueReordered;

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

                // Список обновлений — по Loaded, а не из конструктора: окно ищется по
                // визуальному дереву, а в конструкторе страница в него ещё не вставлена,
                // и Window.GetWindow вернул бы null. Ничего с сервера этот показ не ждёт,
                // поэтому идёт до загрузки данных.
                this.Loaded += (s, e) => this.MaybeShowChangelog();

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

                // Запущенные игры: то же статическое событие, та же продолжительность
                // подписки. Отметки в списке ставим сразу — незакрытые сессии прошлого
                // запуска уже разобраны EnsureStarted выше, и игра, пережившая лаунчер,
                // должна значиться запущенной с первого кадра.
                this.SubscribeRunningGames();
                this.Loaded += (s, e) => {
                    this.SubscribeRunningGames();
                    this.SyncRunLabels();
                };
                this.Unloaded += (s, e) => this.UnsubscribeRunningGames();

                // Статус пишут два десятка мест по всему файлу; вместо того чтобы обходить
                // каждое, слушаем сами свойства — панель прячется и показывается там, где
                // текст и полоса действительно меняются. Список наблюдаемого — в
                // Core.Home.BottomBarWatch: забытое свойство здесь не падает, а молча
                // оставляет внизу экрана строку пустоты.
                // Подписка привязана к жизни страницы: со страницы уходят в игру и в
                // новость и возвращаются обратно. Заведённая один раз и снятая по первому
                // Unloaded, она умирала навсегда — панель замирала в том виде, в каком её
                // застал уход, и после удаления игры внизу оставалось «Готово» под панелью,
                // которой полагалось исчезнуть. Держать её в поле не нужно: страница
                // держит подписку своими же событиями ровно столько, сколько живёт сама.
                _ = Core.Home.BottomBarWatch.Follow(
                    this, this.StatusText, this.UpdateProgress, this.OnStatusTextChanged);
                this.SyncBottomBarVisibility();
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "HomePage.ctor");
            }
        }

        /// <summary>
        /// Просит окно показать список обновлений, если лаунчер обновился. Разметка окна
        /// «Что нового» живёт в <see cref="ChillHub.MainWindow"/>: открывать его умеют и
        /// настройки. Отсюда идёт автоматический показ — он привязан к главному экрану.
        /// </summary>
        private void MaybeShowChangelog() {
            // Loaded приходит на каждый возврат с другой страницы, а показ обещан один
            // раз: отметка в конфиге закрыла бы повтор и сама, но только если её удалось
            // записать. Флаг держит обещание и при неудачной записи.
            if (this.changelogChecked) {
                return;
            }

            this.changelogChecked = true;
            (Window.GetWindow(this) as ChillHub.MainWindow)?.ShowChangelogAfterUpdate();
        }

        private void ChangelogBtn_Click(object sender, RoutedEventArgs e)
            => (Window.GetWindow(this) as ChillHub.MainWindow)?.ShowChangelog();

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
        private void NormalizeCoverUrls(IEnumerable<NewsItem> items) => HomeFeed.NormalizeCoverUrls(items, this.BaseApi);

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
                var gamesUrl = HomeFeed.GamesUrl(this.BaseApi);
                var newsUrl = HomeFeed.LauncherNewsUrl(this.BaseApi);

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
                this.catalog.RememberApiOrder(games);
                var ordered = this.catalog.Sort(games);

                await this.DispatcherInvokeAsync(() => {
                    try {
                        this.games = ordered;
                        this.SetGamesSource();

                        // Выбор при старте: последняя запущенная, иначе первая установленная, иначе первая
                        var idx = GameCatalog.SelectStartupIndex(this.games, ChillHub.Core.ConfigService.Current.LastGameId);
                        if (idx >= 0) {
                            this.GameList.SelectedItem = this.games[idx];
                        }

                        // Скелетоны -> список
                        this.GamesSkeleton.Visibility = System.Windows.Visibility.Collapsed;
                        this.GameList.Visibility = System.Windows.Visibility.Visible;

                        // Отметки «Играет» — на новых объектах списка: подпись живёт в
                        // самой строке, а строки только что созданы заново.
                        this.SyncRunLabels();
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
                    // Task-версия: загрузку нужно ДОЖДАТЬСЯ, иначе следом включаются тяжёлые
                    // проверки файлов и конкурируют с ней за UI-поток.
                    await this.DispatcherInvokeTaskAsync(() => this.LoadBuildsAndGameNewsAsync(gid0));
                }

                // После первичного рендеринга — разрешаем тяжёлые проверки и запускаем в фоне
                this.allowFileChecks = true;
                this.verified.Reset();

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
            finally {
                // ЛЮБОЙ исход загрузки — это ответ ожидающему ярлыку: и полный список, и
                // пустой после недоступного сервера. Иначе ярлык, нажатый при отсутствии
                // сети, ждал бы список игр до закрытия лаунчера.
                this.gamesLoaded.TrySetResult(true);
            }
        }

        // --- Пустое состояние «сервер недоступен» (C5) ---
        private void ShowServerUnavailableState(Exception? ex) {
            try {
                this.games = new List<GameInfo>();
                this.SetGamesSource();
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

        /// <summary>
        /// Показывает или прячет бегунок проверки игр. Видимость и <c>IsIndeterminate</c>
        /// ставятся вместе: бесконечная анимация полосы должна жить ровно столько, сколько
        /// идёт проверка. Раньше <c>IsIndeterminate</c> стоял в разметке, бегунок стартовал
        /// вместе со страницей и крутился под невидимой полосой до самого выхода.
        /// </summary>
        /// <param name="running">Проверка идёт.</param>
        private void ShowGamesVerifyIndicator(bool running) {
            this.GamesVerifyIndicator.IsIndeterminate = running;
            this.GamesVerifyIndicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
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
                    this.ShowGamesVerifyIndicator(true);
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
                            await this.Verifier.VerifyAsync(first);
                        }
                        catch (Exception ex) {
                            // Одна игра не проверилась — остальные проверяем дальше, статус этой останется прежним
                            Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync(priority {first.GameId})");
                        }

                        Interlocked.Increment(ref processed);
                        await this.DispatcherInvokeAsync(() => {
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
                            await this.Verifier.VerifyAsync(g);
                        }
                        catch (Exception ex) {
                            Core.Logging.Logger.Error(ex, $"VerifyGameStatusAsync({g.GameId})");
                        }
                        finally {
                            Interlocked.Increment(ref processed);
                            try {
                                // Бейдж проверенной игры обновляем сразу, чтобы статусы появлялись по мере готовности
                                await this.DispatcherInvokeAsync(() => {
                                    // Строка списка перерисуется сама: статусы игры —
                                    // свойства с уведомлением. Если это выбранная игра —
                                    // кнопка действия должна разблокироваться немедленно.
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

                    // Порядок из ответа API сохраняем, установленные держим сверху.
                    // Здесь достаточно сравнить с this.games: этот путь ничего не вливает
                    // с сервера, состав списка тот же, и поле указывает на показанное.
                    var sorted = this.catalog.Sort(this.games);

                    // СПИСОК ПОДМЕНЯЕТСЯ ТОЛЬКО НА UI-ПОТОКЕ. Сюда мы приходим из Task.Run,
                    // а this.games в то же время читает и переписывает обработчик «Обновить
                    // список» на UI-потоке. Пока присвоение шло из фона, две правки могли
                    // разъехаться: поле указывало на один список, ListBox — на другой, и
                    // выделение с восстановлением индекса считались уже по разным.
                    await this.DispatcherInvokeAsync(() => {
                        var reordered = !GameCatalog.SameOrder(this.games, sorted);
                        this.games = sorted;

                        // Проверка статусов почти всегда оставляет порядок прежним, и вот
                        // тогда список трогать нельзя вовсе: смена источника пересоздаёт все
                        // строки и перезагружает значки — то самое мерцание после запуска.
                        if (reordered) {
                            this.SetGamesSource();
                            this.RestoreSelection(selectedId);
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
                        this.verified.MarkKnown(g?.GameId);
                    }
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Error(ex, "VerifyAllGamesStatusesAsync.finally");
                }

                Interlocked.Exchange(ref this.verifyRunning, 0);
                await this.DispatcherInvokeAsync(() => {
                    this.ShowGamesVerifyIndicator(false);
                    this.RefreshGamesBtn.IsEnabled = true;

                    // Проверка кончилась — строку гасим, чтобы не зависало «Проверка игр X/Y».
                    // Именно гасим, а не пишем «Готово»: панель показывает идущую работу, и
                    // отчёт о кончившейся оставлял её висеть на экране до следующей закачки.
                    // Сообщение об ошибке при этом не затираем — оно важнее.
                    if (string.IsNullOrWhiteSpace(this.lastErrorDetails)) {
                        this.StatusText.Text = string.Empty;
                    }

                    this.UpdateActionButtonState();
                });
            }
        }

        // Короткое сообщение пользователю в статусе; технические детали — в лог и в подсказку (C5)
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

        // Обход папки игры с пересчётом хешей не имеет права выполняться на UI-потоке: см. Core/Home/SyncPlanner.
        private Task<DiffPlan> PlanOffUiThreadAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken token) =>
            SyncPlanner.PlanOffUiThreadAsync(this.sync, manifest, localRoot, contentBaseUrl, token);

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
        /// <para>
        /// ИМЯ У ЭТОГО МЕТОДА ДРУГОЕ НЕ ИЗ КРАСОТЫ. Пока обе версии назывались
        /// <c>DispatcherInvokeAsync</c>, они были перегрузками, и метод рекурсивно вызывал
        /// сам себя: в строке ниже выражение <c>started = action()</c> имеет ТИП Task,
        /// поэтому лямбда подходила и под <see cref="Action"/> (значение отбрасывается),
        /// и под <see cref="Func{Task}"/> — а при разрешении перегрузок C# предпочитает
        /// делегат, возвращаемый тип которого совпадает с выведенным типом лямбды, то есть
        /// эту же перегрузку. Стек переполнялся мгновенно, и процесс умирал, не успев ни
        /// записать лог, ни отправить отчёт об ошибке (так падала версия 1.2.1).
        /// </para>
        /// <para>
        /// Спасали фигурные скобки — блочная лямбда значения не возвращает и приводится
        /// только к <see cref="Action"/>, — но это защита, которую ломает одна случайная
        /// правка. Разные имена делают ошибку невыразимой. Сведение имён обратно вернёт
        /// рекурсию.
        /// </para>
        /// </summary>
        private async Task DispatcherInvokeTaskAsync(Func<Task> action) {
            Task? started = null;

            await this.DispatcherInvokeAsync(() => started = action()).ConfigureAwait(false);

            if (started != null) {
                await started.ConfigureAwait(false);
            }
        }

        private async Task LoadBuildsAndGameNewsAsync(string gameId, CancellationToken token = default) {
            try {
                this.BeginGameNewsLoading();

                // Сборки. Ни сборок, ни новостей у игры на сервере может не быть —
                // это пустой раздел, а не сбой связи (HomeFeed.GetOptionalAsync).
                var buildsUrl = HomeFeed.BuildsUrl(this.BaseApi, gameId);
                var buildsResp = await HomeFeed.GetOptionalAsync<BuildsResponse>(this.http, buildsUrl, token);

                // Выбор уже сменили: писать this.builds нельзя — они относились бы к другой игре
                token.ThrowIfCancellationRequested();

                this.builds = HomeFeed.SortBuilds(buildsResp?.Items);

                // Обновим локальные поля, но не трогаем NeedsUpdate здесь — его ставит проверка по манифесту
                var game = this.games.FirstOrDefault(g => g.GameId == gameId);

                // Чтение версии с диска выполняем в фоновом потоке
                var localVer = await Task.Run(() => ReadLocalVersion(gameId), token);
                token.ThrowIfCancellationRequested();
                var localTrimmed = GameStatus.ApplyLocalVersion(game, localVer);
                Core.Logging.Logger.Info($"LoadBuildsAndGameNewsAsync gid={gameId} local='{localTrimmed}'");

                // Новости игры
                var gameNewsUrl = HomeFeed.GameNewsUrl(this.BaseApi, gameId);
                var gameNews = await HomeFeed.GetOptionalAsync<NewsIndex>(this.http, gameNewsUrl, token);
                token.ThrowIfCancellationRequested();
                var items = gameNews?.Items ?? new List<NewsItem>();
                this.NormalizeCoverUrls(items);
                this.ShowGameNews(items);

                // После загрузки — обновим заголовок (на случай, если он ещё не обновлён)
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
                this.ShowGameNews(Array.Empty<NewsItem>());

                // Обновим заголовок до дефолтного/актуального
            }
        }

        /// <summary>
        /// Ставит ленту новостей в состояние «гружусь»: старые новости убираем сразу.
        /// <para>
        /// Зовётся дважды — по клику в списке и в самой загрузке. Клик первым: между
        /// ним и запросом стоит очередь на загрузку сведений, и во время закачки эта
        /// пауза тянется секундами. Всё это время под названием новой игры висели
        /// новости прошлой.
        /// </para>
        /// </summary>
        private void BeginGameNewsLoading() {
            try {
                this.GameNewsList.ItemsSource = Array.Empty<NewsItem>();
                this.GameNewsSkeleton.Visibility = Visibility.Visible;
                this.GameNewsList.Visibility = Visibility.Collapsed;
                this.GameNewsEmptyState.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"BeginGameNewsLoading: {ex.Message}");
            }
        }

        // Одна кнопка обновления на обе вкладки: перечитывает ту ленту, что открыта.
        private async void RefreshActiveNews_Click(object sender, RoutedEventArgs e) {
            if (this.NewsTabGame.IsChecked == true) {
                await this.ReloadGameNewsAsync();
            }
            else {
                await this.ReloadLauncherNewsAsync();
            }
        }

        /// <summary>
        /// Перечитывает обе ленты сразу — и лаунчера, и выбранной игры.
        /// В отличие от кнопки над лентой, обновляющей только открытую вкладку,
        /// обновление списка игр относится ко всей витрине: закрытая вкладка иначе
        /// осталась бы со старыми новостями и показала бы их при переключении.
        /// </summary>
        /// <returns>Задача, завершающаяся после обеих лент.</returns>
        private async Task ReloadAllNewsAsync() {
            await this.ReloadLauncherNewsAsync();

            // Игра может быть не выбрана (пустой список, сервер недоступен) — тогда
            // обновлять нечего, а ReloadGameNewsAsync написал бы в статус жалобу на
            // невыбранную игру, хотя пользователь про новости игры не спрашивал.
            if (!string.IsNullOrWhiteSpace(this.GetSelectedGameId())) {
                await this.ReloadGameNewsAsync();
            }
        }

        private async Task ReloadLauncherNewsAsync() {
            try {
                this.LauncherNewsSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.LauncherNewsList.Visibility = System.Windows.Visibility.Collapsed;
                var newsUrl = HomeFeed.LauncherNewsUrl(this.BaseApi);
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

        private async Task ReloadGameNewsAsync() {
            if (this.GetSelectedGameId() is not string gid || string.IsNullOrWhiteSpace(gid)) {
                this.StatusText.Text = "Не выбрана игра для обновления новостей";
                return;
            }

            try {
                this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Visible;
                this.GameNewsList.Visibility = System.Windows.Visibility.Collapsed;
                this.GameNewsEmptyState.Visibility = System.Windows.Visibility.Collapsed;
                // Ленты у игры может не быть вовсе — тогда раздел просто пустой,
                // см. HomeFeed.GetOptionalAsync.
                var gameNewsUrl = HomeFeed.GameNewsUrl(this.BaseApi, gid);
                var gameNews = await HomeFeed.GetOptionalAsync<NewsIndex>(this.http, gameNewsUrl);
                var items = gameNews?.Items ?? new List<NewsItem>();
                this.NormalizeCoverUrls(items);
                this.ShowGameNews(items);
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось обновить новости игры.", ex, "HomePage.ReloadGameNewsAsync");
                this.ShowGameNews(Array.Empty<NewsItem>());
            }
        }

        /// <summary>
        /// Показывает ленту новостей игры либо пустое состояние — ровно одно из двух.
        /// До появления пустого состояния список показывался всегда, и при нуле новостей
        /// под заголовком оставалась пустая область в пол-экрана.
        /// </summary>
        private void ShowGameNews(IReadOnlyList<NewsItem> items) {
            this.GameNewsList.ItemsSource = items;
            this.GameNewsSkeleton.Visibility = System.Windows.Visibility.Collapsed;

            var empty = items.Count == 0;
            this.GameNewsList.Visibility = empty ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            this.GameNewsEmptyState.Visibility = empty ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        /// <summary>
        /// Сбрасывает «залипший» режим «Повторить» при переходе к другой игре.
        /// Ошибка обновления относится к конкретной игре, а не ко всей странице.
        /// </summary>
        private void ResetUpdateErrorIfGameChanged(string? gid) {
            if (!this.hasUpdateError || this.IsQueued(gid)) {
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
            this.UpdateDiskFreeText(gid);

            // Витрина перерисовывается ПЕРЕД сетью, а не после неё. Раньше название,
            // бейдж и кнопки ставились в самом конце загрузки сведений об игре, и во
            // время закачки — когда канал занят, а запросы идут секундами — клик по
            // соседней игре выделял строку, но правая половина экрана оставалась от
            // прошлой. Выглядело это как «нажми ещё раз».
            this.ResetUpdateErrorIfGameChanged(gid);
            this.UpdateActionButtonState();
            this.BeginGameNewsLoading();
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

            this.StartHeroGalleryLoad(gid);
        }

        private async Task HandleGameSelectionAsync(string? gidRaw, CancellationToken token) {
            if (gidRaw is string gid && !string.IsNullOrWhiteSpace(gid)) {
                this.ResetUpdateErrorIfGameChanged(gid);

                // Статус выбранной игры сбрасываем, только когда очередь пуста: при непустой
                // очереди этот блок вообще скрыт (вместо него — карточки, см.
                // SyncQueuePanelVisibility), и прогресс позиции живёт в её собственной карточке.
                if (this.queueDockItems.Count == 0) {
                    this.StatusText.Text = string.Empty;
                    this.UpdateProgress.IsIndeterminate = false;
                    this.UpdateProgress.Value = 0;
                    this.SpeedEtaText.Text = string.Empty;
                    this.FilesSizeText.Text = string.Empty;
                }

                // Обновим локальный статус выбранной игры для списка
                var g = this.games.FirstOrDefault(x => x.GameId == gid);
                var localVer = await Task.Run(() => ReadLocalVersion(gid), token);
                token.ThrowIfCancellationRequested();
                GameStatus.ApplyLocalVersion(g, localVer);

                // Обновим заголовок новостей игры под выбранную игру

                // Показать имеющийся кэш сразу (мгновенно), затем уточнить расчётом
                this.UpdateSpaceHintFromCache(gid);
                await this.LoadBuildsAndGameNewsAsync(gid, token);

                // На старте запрещаем тяжёлые проверки. Разрешаем только после первичного рендеринга (когда _allowFileChecks = true).
                // Если статус игры ещё проверяется — не дублируем тяжёлый Plan, оценка появится по завершении проверки.
                if (this.allowFileChecks && this.verified.IsKnown(gid)) {
                    await this.UpdateSpaceHintAsync(gid, token);
                }

                // Всегда обновляем состояние кнопки при смене выбора
                this.UpdateActionButtonState();
            }
            else {
                this.StatusText.Text = string.Empty;
                this.UpdateProgress.IsIndeterminate = false;
                this.UpdateProgress.Value = 0;
                this.SpeedEtaText.Text = string.Empty;
                this.FilesSizeText.Text = string.Empty;

                // Сброс заголовка при отсутствии выбранной игры
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
                var version = HomeFeed.SelectVersion(game, this.builds);

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
        /// Общая часть обеих подсказок: решение принимает <see cref="SpaceHint.Decide"/>,
        /// здесь остаётся только показ. Возвращает true, если вызывающему коду ещё нужно
        /// посчитать размер.
        /// </summary>
        private bool TryShowTrivialSpaceHint(string gid) {
            // Пока ЭТА игра стоит в очереди, её статус даже не смотрим: он пересобирается
            // фоновой проверкой после завершения закачки.
            var queued = this.IsQueued(gid);
            var g = queued ? null : this.games?.FirstOrDefault(x => string.Equals(x.GameId, gid, StringComparison.OrdinalIgnoreCase));
            switch (SpaceHint.Decide(queued, g, gid)) {
                case SpaceHintAction.ShowUpToDate:
                    this.FilesSizeText.Text = SpaceHint.UpToDateText;
                    return false;
                case SpaceHintAction.Compute:
                    return true;
                default:
                    return false;
            }
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
                    var url = HomeFeed.LauncherNewsItemUrl(this.BaseApi, it.Slug);
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
                    var url = HomeFeed.GameNewsItemUrl(this.BaseApi, gid, it.Slug);
                    var win = Window.GetWindow(this) as ChillHub.MainWindow;
                    win?.ContentFrame.Navigate(new NewsDetailPage(it.Title, url));
                }
                finally {
                    this.GameNewsList.SelectedItem = null;
                }
            }
        }

        /// <summary>
        /// Отдаёт список игр в UI и заново вешает фильтр поиска.
        /// <para>
        /// Фильтр живёт на представлении списка (<see cref="ItemCollection.Filter"/>), а не
        /// подменяет ItemsSource: восстановление выбранной игры считает индекс по
        /// <c>this.games</c>, и подменённый источник разъехался бы с этим индексом.
        /// Представление создаётся заново при каждой смене ItemsSource, поэтому фильтр
        /// приходится ставить после каждой такой смены — иначе набранный запрос молча
        /// слетал бы на любом обновлении списка.
        /// </para>
        /// </summary>
        /// <summary>
        /// Возвращает выделение на прежнюю игру после подмены источника. Если её больше
        /// нет — на первую в списке: без выделения витрина и кнопка действия остаются от
        /// игры, которой уже не существует.
        /// </summary>
        /// <param name="gameId">Игра, выделенная до подмены.</param>
        private void RestoreSelection(string? gameId) {
            var idx = GameCatalog.SelectionIndexAfterRefresh(this.games, gameId);
            if (idx >= 0) {
                this.GameList.SelectedItem = this.games[idx];
            }
        }

        private void SetGamesSource() {
            // Список пересобран из свежих объектов — метки очереди на них ещё не стоят,
            // а закачка тем временем идёт. Переносим их из снимка очереди.
            try {
                foreach (var item in this.downloadQueue.Snapshot()) {
                    this.SetQueueLabel(item.GameId, Core.UI.QueueRowLabel.For(item));
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"SetGamesSource queue labels: {ex.Message}");
            }

            this.GameList.ItemsSource = this.games;
            this.ApplyGameFilter();
        }

        private void ApplyGameFilter() {
            try {
                var query = this.GameSearchBox?.Text?.Trim() ?? string.Empty;
                if (query.Length == 0) {
                    this.GameList.Items.Filter = null;
                    return;
                }

                this.GameList.Items.Filter = o =>
                    o is GameInfo g
                    && (g.Title ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            }
            catch (Exception ex) {
                // Фильтр — удобство, а не функция: список должен остаться показанным целиком
                Core.Logging.Logger.Warn($"ApplyGameFilter: {ex.Message}");
            }
        }

        private void GameSearch_TextChanged(object sender, TextChangedEventArgs e) {
            var selected = this.GameList?.SelectedItem;
            this.ApplyGameFilter();

            // Выбор сбрасывается сменой фильтра. Если игра всё ещё видна — возвращаем её,
            // чтобы витрина не мигала при наборе каждой буквы.
            if (selected != null && this.GameList != null && this.GameList.Items.Contains(selected)) {
                this.GameList.SelectedItem = selected;
            }
        }

        /// <summary>
        /// Свободное место на диске игр — в подвале сайдбара. Это свойство машины, а не
        /// текущей закачки, поэтому оно живёт отдельно от строки прогресса.
        /// </summary>
        private void UpdateDiskFreeText(string? gid) {
            try {
                var free = GetAvailableFreeSpaceFor(gid);
                this.DiskFreeText.Text = free > 0
                    ? $"Свободно на диске: {FormatSize(free)}"
                    : string.Empty;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"UpdateDiskFreeText gid={gid}: {ex.Message}");
                this.DiskFreeText.Text = string.Empty;
            }
        }

        // Обновление списка игр по кнопке в заголовке секции
        private async void RefreshGames_Click(object sender, RoutedEventArgs e) {
            // Сохраним текущее выделение, чтобы не потерять контекст страницы игры
            var prevSelectedId = this.GetSelectedGameId();

            // Статусы будут пересчитаны заново — до этого момента считаем их неизвестными (C4)
            this.verified.Reset();
            this.ClearErrorDetails();
            try {
                // Скелет — только когда показывать нечего. Список, который уже на экране,
                // не прячем: обновление занимает секунды, и всё это время экран стоял
                // пустым, хотя данные на нём были верные.
                var firstFill = this.games == null || this.games.Count == 0;
                if (firstFill) {
                    this.GamesSkeleton.Visibility = Visibility.Visible;
                    this.GameList.Visibility = Visibility.Collapsed;
                }

                var gamesUrl = HomeFeed.GamesUrl(this.BaseApi);
                var gamesResp = await this.http.GetFromJsonAsync<GamesResponse>(gamesUrl);

                // Вливаем ответ в уже показанные игры, а не подменяем их новыми объектами:
                // для WPF новый объект — это новая строка со всеми её значками, и список
                // дёргался целиком даже тогда, когда сервер не сказал ничего нового.
                this.games = GameCatalog.Merge(this.games, gamesResp?.Items);
                this.HideServerUnavailableState();
                this.NormalizeGameIconsAndLocalState(this.games);

                // Явное обновление списка — подходящий момент сбросить кеш обложек: сервер
                // часто отдаёт новую картинку по тому же адресу (см. Core.Home.ImageLoader),
                // и без сброса «Обновить список игр» не менял бы обложки, даже если они
                // реально сменились.
                Core.Home.ImageLoader.InvalidateAll();
                this.ReloadGameIcons();

                // Сортировка: установленные сначала, затем порядок, полученный от API
                this.catalog.RememberApiOrder(this.games);
                var sorted = this.catalog.Sort(this.games);

                // Ничего не изменилось — источник не трогаем: его подмена пересоздаёт
                // строки, а вместе с ними сбрасывает выделение и грузит значки заново.
                // Сравнение идёт с ПОКАЗАННЫМ списком, а не с this.games: слияние ответа
                // сервера уже подменило поле новым списком, и сравнение поля с самим собой
                // всегда говорило «то же самое» — игра, удалённая в админке, оставалась в
                // списке (см. GameCatalog.NeedsRebind).
                var rebind = GameCatalog.NeedsRebind(this.GameList.ItemsSource, sorted);
                this.games = sorted;
                this.SyncRunLabels();
                if (rebind) {
                    this.SetGamesSource();

                    // Восстановим выбранную игру, если она осталась в списке. Если её
                    // удалили — выделяем первую: пустая витрина после обновления списка
                    // выглядит как поломка, а кнопка действия осталась бы от игры,
                    // которой больше нет.
                    this.RestoreSelection(prevSelectedId);
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

            // Витрина и ленты живут своими запросами и своими кешами: без этого
            // «Обновить список игр» приносил новый список, но оставлял на экране
            // прежнюю обложку витрины и прежние новости — ровно то, за чем на кнопку
            // и жмут после правок в админке.
            this.galleryClient.InvalidateAll();
            this.StartHeroGalleryLoad(this.GetSelectedGameId());
            await this.ReloadAllNewsAsync();

            // Запустить асинхронную проверку статусов по манифесту
            this.ShowGamesVerifyIndicator(true);
            await this.VerifyAllGamesStatusesAsync(this.GetSelectedGameId());

            // После обновления статусов — освежим подсказку по текущей игре из кэша
            try {
                // Если выделение потеряно после верификации — восстановим прежнее
                var gid = this.GetSelectedGameId();
                if (string.IsNullOrWhiteSpace(gid) && !string.IsNullOrWhiteSpace(prevSelectedId)) {
                    var idxSel2 = GameCatalog.IndexOfIgnoreCase(this.games, prevSelectedId);
                    if (idxSel2 >= 0) {
                        this.GameList.SelectedItem = this.games[idxSel2];
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

            var gid = this.GetSelectedGameId();

            // Блокируем действия только пока не известен статус ВЫБРАННОЙ игры.
            // Проверка остальных игр в фоне работе не мешает (C4).
            if (!this.IsQueued(gid) && !this.verified.IsKnown(gid)) {
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

            // Выбранная игра уже стоит в очереди (ждёт или качается прямо сейчас) — кнопка
            // работает как «Отмена» этой конкретной позиции, а не глобальный переключатель:
            // до перехода на очередь один флаг isUpdating красил кнопку в «Отмена» для ЛЮБОЙ
            // выбранной игры, даже когда качалась совсем другая — снять можно было только её.
            if (!string.IsNullOrWhiteSpace(gid) && this.IsQueued(gid)) {
                this.downloadQueue.Remove(gid!);
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
                    if (!string.IsNullOrWhiteSpace(gid)) {
                        // Единственная точка запуска закачки — дальше игра либо сразу качается
                        // (очередь была пуста), либо ждёт своей позиции: ровно то же самое
                        // "Добавить в очередь загрузок" контекстного меню, см. EnqueueGame_Click.
                        // Список игр при этом никогда не блокируется — переключаться на другие
                        // экраны можно свободно, пока эта позиция стоит в очереди или качается.
                        if (!this.downloadQueue.Enqueue(gid!)) {
                            this.StatusText.Text = "Игра уже установлена или уже в очереди.";
                        }
                    }

                    break;
            }
        }

        /// <summary>
        /// Есть ли у игры сборка на сервере.
        /// <para>
        /// Пустая версия — это игра, которая живёт только копией из Steam: сервер про неё
        /// знает всё, кроме файлов. Ровно так же считает <see cref="Core.Mods.LaunchPlan"/>,
        /// когда решает, предлагать ли «Пиратку».
        /// </para>
        /// </summary>
        /// <param name="game">Игра из каталога.</param>
        /// <returns>true, если сборку можно скачать.</returns>
        private static bool HasServerBuild(GameInfo? game)
            => !string.IsNullOrWhiteSpace(game?.LatestVersion);

        /// <summary>Выбранная игра прямо сейчас стоит в очереди — ждёт или качается.</summary>
        private bool IsQueued(string? gid)
            => !string.IsNullOrWhiteSpace(gid)
               && this.downloadQueue.Snapshot().Any(i => string.Equals(i.GameId, gid, StringComparison.OrdinalIgnoreCase));

        /// <summary>Снимок позиции выбранной игры в очереди — null, если её там нет.</summary>
        private Core.Game.QueueItem? SelectedQueueItem() {
            var gid = this.GetSelectedGameId();
            if (string.IsNullOrWhiteSpace(gid)) {
                return null;
            }

            return this.downloadQueue.Snapshot().FirstOrDefault(i => string.Equals(i.GameId, gid, StringComparison.OrdinalIgnoreCase));
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
                var look = ActionButtonState.Appearance(mode, this.SelectedRunState());
                this.ActionBtn.Content = look.Content;
                this.ActionBtn.IsEnabled = look.IsEnabled;
                this.ApplyActionButtonStyle(look.StyleKey);
                this.SyncLaunchBar(mode);
            }
            catch (Exception ex) {
                // Кнопка действия — центральный элемент экрана: не даём сбою оформления уронить страницу
                Core.Logging.Logger.Error(ex, $"SetActionMode({mode})");
            }
        }

        /// <summary>
        /// Пересобирает строку действий витрины: «Играть» или две кнопки запуска.
        /// <para>
        /// Кнопки появляются только у игры с модами и только в режиме «Играть»: пока
        /// игра качается, обновляется или проверяется, запускать нечего, а обещать
        /// выбор, которого в этот момент нет, — врать.
        /// </para>
        /// </summary>
        /// <param name="mode">Текущий режим кнопки действия.</param>
        private void SyncLaunchBar(ActionMode mode) {
            try {
                var game = this.GetSelectedGame();
                var playMode = mode == ActionMode.Play;

                // Копия из Steam живёт своей жизнью: моды ставятся в чужую папку, и
                // ждать ради них закачки сборки с сервера незачем. Не предлагаем её
                // только там, где игре сейчас не до запуска: идёт закачка, удаление,
                // проверка или технические работы.
                var steamAllowed = mode is ActionMode.Install or ActionMode.Update
                    or ActionMode.Retry or ActionMode.SteamOnly;
                var options = (playMode || steamAllowed) && game?.Mods != null
                    ? this.CachedLaunchOptions(game)
                    : null;

                var gid = game?.GameId;
                var view = Core.Mods.LaunchButtons.Compute(
                    game?.Mods, playMode, steamAllowed, options,
                    Core.Mods.LaunchChoice.Remembered(gid),
                    target => Core.Game.RunningGames.StateOf(gid, target));

                this.launchBar = view;
                this.ActionBtn.Visibility = view.ActionVisible ? Visibility.Visible : Visibility.Collapsed;
                this.ApplyLaunchButton(this.LaunchBtn1, this.LaunchBtn1Title, this.LaunchBtn1Note, view, 0);
                this.ApplyLaunchButton(this.LaunchBtn2, this.LaunchBtn2Title, this.LaunchBtn2Note, view, 1);
                this.LaunchMenuBtn.Visibility = view.MenuVisible ? Visibility.Visible : Visibility.Collapsed;
                this.LaunchMenuBtn.ToolTip = view.MenuVisible ? view.MenuTooltip : null;

                // Подсказка «что запустится» нужна только той «Играть», которая
                // открывает меню: у кнопок запуска ответ написан прямо на них, а
                // «Установить» и «Обновить» и так называют своё действие.
                this.ActionBtn.ToolTip = playMode && view.ActionVisible && view.MenuVisible
                    ? "Выбрать, что запускать"
                    : null;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"SyncLaunchBar: {ex.Message}");
            }
        }

        /// <summary>
        /// Выставляет одну кнопку запуска по счёту или прячет её, если варианта нет.
        /// </summary>
        /// <param name="button">Сама кнопка.</param>
        /// <param name="title">Крупная строка.</param>
        /// <param name="note">Мелкая строка.</param>
        /// <param name="view">Посчитанная строка действий.</param>
        /// <param name="index">Место кнопки в строке.</param>
        private void ApplyLaunchButton(
            Button button, TextBlock title, TextBlock note, Core.Mods.LaunchBarView view, int index) {
            if (index >= view.Buttons.Count) {
                button.Visibility = Visibility.Collapsed;
                button.Tag = null;
                return;
            }

            var model = view.Buttons[index];
            title.Text = model.Title;
            note.Text = model.Subtitle;
            button.ToolTip = model.Tooltip;
            button.Tag = model.Target;
            button.Visibility = Visibility.Visible;
            button.IsEnabled = model.Enabled;

            if (this.TryFindResource(model.StyleKey) is Style style) {
                button.Style = style;
            }

            // Надписи одевает Core.UI.LaunchButtonLook: стиль кнопки до них не дотягивается
            // (обе живут именованными TextBlock'ами внутри содержимого), а различаться
            // залитая и контурная кнопки обязаны не только фоном.
            Core.UI.LaunchButtonLook.Apply(
                title, note, model.Accent, (Brush)(this.TryFindResource("Brush.TextSecondary") ?? title.Foreground));
        }

        /// <summary>
        /// Варианты запуска для строки действий — со снимком на секунду.
        /// <para>
        /// Метод дёргается на каждое событие очереди и проверки, а за ним стоят реестр
        /// и файловая система. Щелчок по кнопке варианты всё равно пересчитывает, так
        /// что устареть снимку негде.
        /// </para>
        /// </summary>
        /// <param name="game">Выбранная игра.</param>
        /// <returns>Варианты запуска.</returns>
        private IReadOnlyList<Core.Mods.LaunchOption> CachedLaunchOptions(GameInfo game) {
            if (this.launchOptionsCache.Get(game) is { } cached) {
                return cached;
            }

            var options = this.LaunchOptionsFor(game, logSteam: false);
            this.launchOptionsCache.Put(game, options);
            return options;
        }

        /// <summary>Забывает снимок вариантов: состояние копий только что менялось.</summary>
        private void InvalidateLaunchOptions() => this.launchOptionsCache.Invalidate();

        /// <summary>
        /// Перечитывает состояние копий игры на диске и пересобирает строку действий.
        /// <para>
        /// Моды живут в ЧУЖОЙ папке — в копии игры из Steam, — и лаунчер ею не владеет.
        /// Пока его окно стояло в стороне, игру могли удалить из Steam или, наоборот,
        /// поставить; библиотеку могли перенести на другой диск; загрузчик мог унести
        /// антивирус или сам игрок. Кнопка «Steam · с модами» после этого обещала бы то,
        /// чего в папке уже нет.
        /// </para>
        /// <para>
        /// А вот на проверку целостности файлов средствами Steam рассчитывать не стоит:
        /// добавленные файлы она не трогает, и модпак после неё остаётся на месте.
        /// </para>
        /// <para>
        /// Возврат фокуса на окно — ровно тот момент, когда человек пришёл из Steam, и
        /// перечитать папку дешевле всего: это несколько файлов, а не обход дерева.
        /// Следить за папкой постоянно незачем — между возвратами фокуса её состояние
        /// никого не интересует.
        /// </para>
        /// </summary>
        internal void RefreshLaunchOptionsFromDisk() {
            // Без своего try/catch: сбрасывать снимок нечему, а SyncLaunchBar ловит своё
            // сам — второй перехват поверх него ничего не добавлял бы, кроме строк.
            this.InvalidateLaunchOptions();
            this.SyncLaunchBar(this.actionMode);
        }

        /// <summary>Запускает вариант, вынесенный кнопкой на витрину.</summary>
        /// <param name="sender">Нажатая кнопка.</param>
        /// <param name="e">Аргументы события.</param>
        private void LaunchBtn_Click(object sender, RoutedEventArgs e) {
            if (sender is not Button { Tag: Core.Mods.LaunchTarget target }) {
                return;
            }

            var game = this.GetSelectedGame();
            if (game?.Mods == null) {
                return;
            }

            // Варианты пересчитываются заново: между отрисовкой кнопки и щелчком игру
            // могли удалить из Steam, а запустить не то, что написано на кнопке, —
            // худший из возможных исходов.
            this.InvalidateLaunchOptions();
            var chosen = Core.Mods.LaunchButtons.Chosen(this.LaunchOptionsFor(game, logSteam: true), target);
            if (chosen.Option == null) {
                this.StatusText.Text = chosen.Message;
                this.SyncLaunchBar(this.actionMode);
                return;
            }

            this.StartLaunchOption(game, chosen.Option);
        }

        private void LaunchMenuBtn_Click(object sender, RoutedEventArgs e) {
            var game = this.GetSelectedGame();
            if (game?.Mods is not null) {
                this.ShowModsLaunchMenu(game, onlyHidden: true);
            }
        }

        // --- Вкладки под витриной: «Новости игры» / «Новости лаунчера» ---
        private void NewsTabGame_Click(object sender, RoutedEventArgs e) => this.SelectNewsTab(showGameNews: true);

        private void NewsTabLauncher_Click(object sender, RoutedEventArgs e) => this.SelectNewsTab(showGameNews: false);

        private void SelectNewsTab(bool showGameNews) {
            try {
                this.NewsTabGame.IsChecked = showGameNews;
                this.NewsTabLauncher.IsChecked = !showGameNews;
                this.GameNewsPanel.Visibility = showGameNews ? Visibility.Visible : Visibility.Collapsed;
                this.LauncherNewsPanel.Visibility = showGameNews ? Visibility.Collapsed : Visibility.Visible;

                // «Что нового» — про лаунчер, и на вкладке новостей игры ему не место.
                this.ChangelogBtn.Visibility = showGameNews ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"SelectNewsTab: {ex.Message}");
            }
        }

        // Заголовок, обложка и бейдж статуса витрины-героя — под выбранную игру
        private void UpdateHero() {
            try {
                var g = this.GetSelectedGame();
                this.HeroTitleText.Text = g?.Title is string t && !string.IsNullOrWhiteSpace(t) ? t : "Выберите игру";

                string status;
                if (g == null) {
                    status = string.Empty;
                }
                else if (Core.Game.RunningGameLook.Headline(Core.Game.RunningGames.StateOf(g.GameId)) is { Length: > 0 } open) {
                    // Впереди всего остального: «Установлена» под открытой игрой отвечает
                    // на вопрос, которого игрок в этот момент не задавал.
                    status = open;
                }
                else if (!string.IsNullOrEmpty(g.QueueLabel)) {
                    // Игра в очереди: бейдж говорит «Скачивание · 5%» / «В очереди», а не
                    // «Требуется обновление» — иначе на одном экране про одну игру стояли
                    // три разных состояния: в списке качается, в витрине требует, кнопка отменяет.
                    status = g.QueueLabel;
                }
                else if (!this.verified.IsKnown(g.GameId)) {
                    status = "Проверка…";
                }
                else if (g.NeedsUpdate) {
                    // Те же слова, что на странице игры: одно состояние — одно имя
                    status = "Доступно обновление";
                }
                else if (g.IsInstalled) {
                    status = "Установлена";
                }
                else {
                    status = "Не установлена";
                }

                this.HeroStatusText.Text = status;
                this.HeroStatusBadge.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
                this.HeroMetaText.Text = BuildHeroMeta(g);
            }
            catch (Exception ex) {
                // Витрина — украшение поверх основной логики: сбой обновления заголовка не должен мешать остальному
                Core.Logging.Logger.Warn($"UpdateHero: {ex.Message}");
            }
        }

        /// <summary>
        /// Название выбранной игры — для пункта «Играть» в меню трея. Пусто, если игра не
        /// выбрана: меню обязано это показать, а не предлагать запуск в никуда.
        /// </summary>
        internal string? SelectedGameTitle => this.GetSelectedGame()?.Title;

        /// <summary>
        /// Выбранная игра установлена и готова к запуску — трей может стартовать её, не
        /// поднимая окно. В любом другом состоянии (установка, обновление, проверка) окно
        /// придётся показать: пользователю нужно видеть, что происходит.
        /// </summary>
        internal bool CanPlaySelectedGame =>
            this.actionMode == ActionMode.Play
            && this.SelectedRunState() == Core.Game.GameRunState.None;

        /// <summary>Делает то же, что кнопка действия на витрине — вызов из меню трея.</summary>
        internal void InvokeSelectedAction() => this.ActionBtn_Click(this, new RoutedEventArgs());

        /// <summary>
        /// В очереди загрузок есть хоть одна карточка — идущая или ждущая своей очереди.
        /// Периодическая проверка самообновления (см. <see cref="ChillHub.MainWindow"/>)
        /// откладывает показ диалога, пока это true: прерывать загрузку игры вопросом про
        /// обновление лаунчера — не то, ради чего пользователь его открыл.
        /// </summary>
        internal bool HasActiveDownloads => this.queueDockItems.Count > 0;

        /// <summary>
        /// Строка под названием в витрине: версия, куда обновляемся, и сколько наиграно.
        /// Пустая, если про игру нечего сказать — пустых разделителей в ней не остаётся.
        /// </summary>
        private static string BuildHeroMeta(GameInfo? g) {
            if (g == null) {
                return string.Empty;
            }

            var parts = new List<string>();
            var installed = (g.InstalledVersion ?? string.Empty).Trim();
            var latest = (g.LatestVersion ?? string.Empty).Trim();

            // Сначала наигранное, потом версия: игроку интересно первое, номер сборки —
            // справочная мелочь, и на первом месте он читался как главное о игре.
            try {
                var playtime = Core.Game.PlaytimeStore.Get(g.GameId);
                if (playtime.TotalSeconds > 0) {
                    parts.Add(Core.Game.PlaytimeStore.FormatTotal(playtime.TotalSeconds) + " в игре");
                }
            }
            catch (Exception ex) {
                // Наигранное — приятная мелочь, а не причина оставить витрину без строки
                Core.Logging.Logger.Warn($"BuildHeroMeta playtime gid={g.GameId}: {ex.Message}");
            }

            if (g.IsInstalled && installed.Length > 0) {
                parts.Add(g.NeedsUpdate && latest.Length > 0 && latest != installed
                    ? $"версия {installed} → {latest}"
                    : $"версия {installed}");
            }
            else if (latest.Length > 0) {
                parts.Add($"версия {latest}");
            }

            // Модпак называется здесь же, отдельным куском строки. Выбирать игроку
            // нечего — активный модпак на игру ровно один и назначается в админке, —
            // но знать, ЧТО у него стоит, он должен: без этого в жалобе «моды
            // сломались» нет ни имени сборки, ни её версии.
            var pack = g.Mods?.Describe() ?? string.Empty;
            if (pack.Length > 0) {
                parts.Add("моды: " + pack);
            }

            return string.Join(" · ", parts);
        }

        /// <summary>
        /// Нижняя панель видна, только пока что-то происходит: есть очередь, идёт полоса
        /// или строке состояния есть что сказать. В покое она забирала полтораста пикселей под пустой
        /// прогрессбар — полоса в нуле читается как остановившийся процесс, а не как его
        /// отсутствие.
        /// </summary>
        private void OnStatusTextChanged(object? sender, EventArgs e) => this.SyncBottomBarVisibility();

        private void SyncBottomBarVisibility() {
            try {
                if (this.BottomBar == null) {
                    return;
                }

                // Само решение — в Core.Home.BottomBarLook: здесь остаётся только
                // расставить его по контролам.
                var look = Core.Home.BottomBarLook.Decide(
                    this.QueuePanel.Visibility == Visibility.Visible,
                    this.UpdateProgress.IsIndeterminate,
                    this.UpdateProgress.Value,
                    this.StatusText.Text,
                    this.SpeedEtaText.Text,
                    this.FilesSizeText.Text);

                this.BottomBar.Visibility = Shown(look.Panel);
                this.UpdateProgress.Visibility = Shown(look.Progress);
                this.StatusText.Visibility = Shown(look.Status);
                this.SpeedEtaText.Visibility = Shown(look.SpeedEta);
                this.FilesSizeText.Visibility = Shown(look.FilesSize);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"SyncBottomBarVisibility: {ex.Message}");
            }
        }

        private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

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
                this.UpdateHero();

                // Пока файлы удаляются, любое событие очереди или проверки не должно
                // вернуть на кнопку «Обновить» поверх наполовину снесённой игры.
                if (this.isDeleting) {
                    this.SetActionMode(ActionMode.Deleting);
                    return;
                }

                var g = this.GetSelectedGame();
                var isInstalled = g?.IsInstalled == true;
                var needsUpdate = g?.NeedsUpdate == true;

                // Выбранная игра стоит в очереди (ждёт или качается) — кнопка отменяет ЭТУ
                // позицию. Прогресс при этом рисует её собственная карточка в очереди и
                // только она: дублировать его строкой статуса больше не нужно.
                if (this.SelectedQueueItem() is Core.Game.QueueItem queued) {
                    // Ждущая позиция — не «Отмена»: останавливать нечего, можно только
                    // снять с очереди. Красная кнопка остаётся за идущей закачкой.
                    this.SetActionMode(queued.State == Core.Game.QueueItemState.Waiting
                        ? ActionMode.Dequeue
                        : ActionMode.Cancel);
                    return;
                }

                // Ждём только проверку выбранной игры, а не всего списка (C4)
                if (!this.verified.IsKnown(g?.GameId)) {
                    this.SetActionMode(ActionMode.Checking);
                    return;
                }

                // Статус стал известен — снимаем «бегущую» полосу, которую включает клик по
                // кнопке во время проверки. Раньше её никто не выключал, и прогресс-бар
                // бежал до конца сессии, изображая несуществующую работу.
                if (!this.isDeleting) {
                    this.UpdateProgress.IsIndeterminate = false;
                }

                // Сначала решаем, что вообще предложить пользователю, и только потом сверяемся
                // с режимом технических работ: так запрет не «съедает» логику состояний.
                var unfinished = HasUnfinishedUpdate(g?.GameId);
                var intended = ActionButtonState.Decide(
                    this.hasUpdateError, unfinished, isInstalled, needsUpdate, HasServerBuild(g));

                // Причину и срок не дублируем в строку статуса: они уже висят баннером в
                // шапке, а строка статуса раскрывала нижнюю панель с тем же текстом и
                // застрявшей подписью прошлой проверки — одно сообщение стояло дважды.
                if (ActionButtonState.IsBlockedByMaintenance(intended, Core.Maintenance.MaintenanceService.Current)) {
                    this.SetActionMode(ActionMode.Maintenance);
                    return;
                }

                this.SetActionMode(intended);
                if (intended == ActionMode.Update && unfinished) {
                    this.StatusText.Text = "Обновление не завершено. Нажмите «Обновить», чтобы восстановить игру.";
                }
            }
            catch (Exception ex) {
                // Метод дёргается отовсюду (в т.ч. из фоновых задач) — он обязан быть безопасным
                Core.Logging.Logger.Error(ex, "UpdateActionButtonState");
            }
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

        /// <summary>Что сейчас с выбранной игрой: запущена, запускается или ни то ни другое.</summary>
        /// <returns>Состояние запуска.</returns>
        private Core.Game.GameRunState SelectedRunState() {
            try {
                return Core.Game.RunningGames.StateOf(this.GetSelectedGameId());
            }
            catch (Exception ex) {
                // Не смогли узнать — считаем, что игра не запущена: запертая витрина хуже
                // лишнего запуска, а лишний запуск отсюда и так не следует.
                Core.Logging.Logger.Warn($"SelectedRunState: {ex.Message}");
                return Core.Game.GameRunState.None;
            }
        }

        // Игра запускается и закрывается, пока страница открыта: подписка живёт ровно
        // столько же, сколько подписки на техработы и авто-отчёты, и по той же причине —
        // событие статическое, а страница пересоздаётся.
        private bool runningGamesSubscribed;

        private void SubscribeRunningGames() {
            if (this.runningGamesSubscribed) {
                return;
            }

            Core.Game.RunningGames.Changed += this.OnRunningGamesChanged;
            this.runningGamesSubscribed = true;
        }

        private void UnsubscribeRunningGames() {
            if (!this.runningGamesSubscribed) {
                return;
            }

            Core.Game.RunningGames.Changed -= this.OnRunningGamesChanged;
            this.runningGamesSubscribed = false;
        }

        /// <summary>
        /// Игру запустили или закрыли. Событие приходит из фоновой задачи — той, что
        /// дожидается выхода процесса, — поэтому всё, что трогает окно, уходит в диспетчер.
        /// </summary>
        private void OnRunningGamesChanged() =>
            _ = this.DispatcherInvokeAsync(() => {
                try {
                    this.SyncRunLabels();
                    this.UpdateActionButtonState();
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Error(ex, "HomePage.OnRunningGamesChanged");
                }
            });

        /// <summary>Переписывает подписи «Играет» в списке игр.</summary>
        private void SyncRunLabels() => Core.Game.RunningGameLook.ApplyLabels(this.games);

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
        // Перезапуск клиента не нужен — просто пересчитываем кнопку. Строку статуса не
        // трогаем: режим работ в неё не пишет, а гасить чужое сообщение
        // («Обновление не завершено…») из-за окончания работ было бы неверно.
        private void OnMaintenanceChanged(Core.Maintenance.MaintenanceState state) {
            try {
                this.UpdateActionButtonState();
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "HomePage.OnMaintenanceChanged");
            }
        }

        // --- Очередь загрузок -------------------------------------------------------------

        /// <summary>
        /// Ставит в очередь проверку файлов игры.
        /// <para>
        /// Через очередь, а не отдельным прогоном: проверка читает и хеширует десятки
        /// гигабайт, и раньше она обрывалась уходом со страницы игры, а в панели
        /// загрузок её не было видно вовсе.
        /// </para>
        /// </summary>
        /// <param name="sender">Пункт меню.</param>
        /// <param name="e">Аргументы события.</param>
        private void VerifyGame_Click(object sender, RoutedEventArgs e) {
            try {
                if (Core.UI.GameMenuItems.GameOf(sender) is not GameInfo game) {
                    return;
                }

                var kind = Core.Game.QueueTaskKind.Verify;
                if (!this.downloadQueue.Enqueue(game.GameId, kind)) {
                    this.StatusText.Text = Core.Game.QueueRefusal.For(kind, game.Title);
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "HomePage.VerifyGame_Click");
            }
        }

        private void EnqueueGame_Click(object sender, RoutedEventArgs e) {
            try {
                // Тот же порядок разрешения, что у остальных пунктов этого контекстного меню
                // (GameDetails_Click/OpenGameFolder_Click/DeleteGame_Click) — CommandParameter
                // сперва, DataContext вторым.
                var game = (sender as FrameworkElement)?.GetValue(MenuItem.CommandParameterProperty) as GameInfo
                           ?? (sender as FrameworkElement)?.DataContext as GameInfo;
                if (game == null) {
                    return;
                }

                if (!this.downloadQueue.Enqueue(game.GameId)) {
                    this.StatusText.Text = $"«{game.Title}» уже установлена или уже в очереди.";
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "HomePage.EnqueueGame_Click");
            }
        }

        private void MoveQueueItemUp_Click(object sender, RoutedEventArgs e) {
            if ((sender as FrameworkElement)?.DataContext is Core.Game.QueueItem item) {
                this.downloadQueue.MoveUp(item.GameId);
            }
        }

        private void MoveQueueItemDown_Click(object sender, RoutedEventArgs e) {
            if ((sender as FrameworkElement)?.DataContext is Core.Game.QueueItem item) {
                this.downloadQueue.MoveDown(item.GameId);
            }
        }

        private void CancelQueueItem_Click(object sender, RoutedEventArgs e) {
            if ((sender as FrameworkElement)?.DataContext is Core.Game.QueueItem item) {
                this.downloadQueue.Remove(item.GameId);
            }
        }

        // ItemAdded/ItemProgress несут актуальный снимок позиции — заменяем её в коллекции
        // целиком (QueueItem неизменяем), а не пытаемся мутировать старую запись на месте.
        //
        // BeginInvoke, а не синхронный Invoke: события летят из фонового воркера DownloadQueue
        // (RunWorkerAsync await-ит ProcessAsync перед тем, как взять следующую позицию), и
        // синхронное ожидание UI-потока здесь придерживало бы воркер на каждое обновление
        // прогресса/каждое завершение позиции. BeginInvoke сохраняет порядок событий одного
        // потока-источника (Dispatcher — FIFO-очередь), просто не ждёт их обработки.
        private void OnQueueItemChanged(Core.Game.QueueItem item) {
            this.Dispatcher.BeginInvoke(() => {
                var idx = IndexOfQueueItem(this.queueDockItems, item.GameId);
                if (idx >= 0) {
                    // Замена позиции пересобирает строку в доке целиком, а отчёты о ходе
                    // закачки приходят десять раз в секунду. Цифры от четырёх обновлений
                    // в секунду не отстают, а смена состояния проходит сразу — см.
                    // QueueDockLayout.ShouldRefreshRow.
                    var sameState = this.queueDockItems[idx].State == item.State;
                    if (!Core.UI.QueueDockLayout.ShouldRefreshRow(sameState, this.SinceLastRowRefresh(item.GameId))) {
                        this.SetQueueLabel(item.GameId, Core.UI.QueueRowLabel.For(item));
                        return;
                    }

                    this.MarkRowRefreshed(item.GameId);
                    this.queueDockItems[idx] = item;
                }
                else {
                    this.MarkRowRefreshed(item.GameId);
                    this.queueDockItems.Add(item);
                }

                this.SetQueueLabel(item.GameId, Core.UI.QueueRowLabel.For(item));
                this.SyncQueuePanelVisibility();

                // Витрина/нижняя панель отражают позицию ВЫБРАННОЙ игры — если это она,
                // перерисовываем сразу, а не ждём следующего клика/смены выбора.
                if (string.Equals(this.GetSelectedGameId(), item.GameId, StringComparison.OrdinalIgnoreCase)) {
                    this.UpdateActionButtonState();
                }
            });
        }

        /// <summary>
        /// Нижняя панель показывает ЛИБО очередь, либо статус выбранной игры — но не оба
        /// сразу: до объединения блоков одна и та же позиция описывалась дважды, карточкой
        /// в середине экрана и строкой внизу.
        /// </summary>
        private void SyncQueuePanelVisibility() {
            var count = this.queueDockItems.Count;
            this.SyncQueueDockRows();
            this.QueuePanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            this.IdleStatusPanel.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
            // Заголовок нужен, только когда позиций несколько: над единственной карточкой
            // «Очередь загрузок · качается 1 из 1» пересказывает саму карточку.
            this.QueueSummaryText.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (count > 1) {
                var running = this.queueDockItems.Count(i => i.State == Core.Game.QueueItemState.Running);
                this.QueueSummaryText.Text = running > 0
                    ? $"Очередь загрузок · качается {running} из {count}"
                    : $"Очередь загрузок · {count} в ожидании";
            }

            this.SyncBottomBarVisibility();
        }

        /// <summary>
        /// Приводит видимые строки дока в соответствие с очередью и высотой окна. Сколько
        /// строк показать и как поправить список — в Core.UI.QueueDockLayout, здесь остаётся
        /// только разметка.
        /// </summary>
        private void SyncQueueDockRows() {
            try {
                var view = Core.UI.QueueDockLayout.Compute(this.queueDockItems.Count, this.ActualHeight, this.queueDockExpanded);
                Core.UI.QueueDockLayout.ApplyVisible(this.queueDockItems, this.queueDockVisibleItems, view.VisibleRows);

                this.QueueMoreBtn.Content = view.ToggleText;
                this.QueueMoreBtn.Visibility = view.ToggleText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

                // Очередь укоротилась до размеров свёрнутого дока — раскрытым его больше
                // держать нечем: иначе следующая закачка появилась бы сразу раскрытой.
                if (view.ToggleText.Length == 0) {
                    this.queueDockExpanded = false;
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"SyncQueueDockRows: {ex.Message}");
            }
        }

        /// <summary>Сколько миллисекунд прошло с прошлой перерисовки строки этой игры.</summary>
        /// <param name="gameId">Игра.</param>
        /// <returns>Миллисекунды; для строки, которой ещё не было, — бесконечность.</returns>
        private double SinceLastRowRefresh(string gameId)
            => this.rowRefreshedAt.TryGetValue(gameId ?? string.Empty, out var at)
                ? Environment.TickCount64 - at
                : double.PositiveInfinity;

        /// <summary>Запоминает момент перерисовки строки.</summary>
        /// <param name="gameId">Игра.</param>
        private void MarkRowRefreshed(string gameId)
            => this.rowRefreshedAt[gameId ?? string.Empty] = Environment.TickCount64;

        /// <summary>«Показать ещё N» / «Свернуть очередь» под доком.</summary>
        private void QueueMoreBtn_Click(object sender, RoutedEventArgs e) {
            this.queueDockExpanded = !this.queueDockExpanded;
            this.SyncQueueDockRows();
        }

        /// <summary>
        /// Позиция очереди завершилась — успехом, ошибкой или отменой. Устанавливает то, что
        /// раньше делал завершающий блок StartUpdateAsync (без прямой закачки эта логика
        /// переехала сюда — единственное место, где ЛЮБАЯ закачка на самом деле кончается),
        /// а затем убирает карточку из очереди тем же путём, что и OnQueueItemRemoved.
        /// </summary>
        private void OnQueueItemCompleted(Core.Game.QueueItem item) {
            this.Dispatcher.BeginInvoke(() => {
                switch (item.State) {
                    case Core.Game.QueueItemState.Completed:
                        this.hasUpdateError = false;
                        if (string.Equals(this.updateErrorGameId, item.GameId, StringComparison.OrdinalIgnoreCase)) {
                            this.updateErrorGameId = null;
                        }

                        var g = this.games.FirstOrDefault(x => string.Equals(x.GameId, item.GameId, StringComparison.OrdinalIgnoreCase));
                        this.MarkInstalled(item.GameId, g?.LatestVersion);

                        // Ярлык на рабочем столе: игра уже распакована и запускается, так что
                        // ошибки здесь установку не портят — их гасит сам вызов.
                        GameLocalState.StartDesktopShortcutCreation(g?.Title, item.GameId, g?.ExeRelativePath);
                        break;
                    case Core.Game.QueueItemState.Failed:
                        this.hasUpdateError = true;
                        this.updateErrorGameId = item.GameId;
                        break;
                    case Core.Game.QueueItemState.Cancelled:
                    default:
                        break;
                }

                // Конец работы — всплывашкой, строка внизу остаётся за идущей работой:
                // см. Core.Home.QueueDone. Ошибка — исключение, её оставляем в строке.
                var done = Core.Home.QueueDone.For(item.State, item.Title, item.StatusText);
                if (done.Toast.Length > 0) {
                    this.ShowToast(done.Toast);
                }

                if (string.Equals(this.GetSelectedGameId(), item.GameId, StringComparison.OrdinalIgnoreCase)) {
                    this.StatusText.Text = done.Status;
                    this.UpdateActionButtonState();
                }
            });

            this.OnQueueItemRemoved(item);
        }

        private void OnQueueItemRemoved(Core.Game.QueueItem item) {
            this.Dispatcher.BeginInvoke(() => {
                var idx = IndexOfQueueItem(this.queueDockItems, item.GameId);
                if (idx >= 0) {
                    this.queueDockItems.RemoveAt(idx);
                }

                this.SetQueueLabel(item.GameId, string.Empty);
                this.rowRefreshedAt.Remove(item.GameId ?? string.Empty);
                this.SyncQueuePanelVisibility();

                // Снятая с очереди игра — та, что выбрана: кнопка обязана вернуться из
                // «Убрать из очереди» к «Установить», иначе она врёт до следующего клика
                // по списку — карточки внизу уже нет, а кнопка всё ещё предлагает снять её.
                if (string.Equals(this.GetSelectedGameId(), item.GameId, StringComparison.OrdinalIgnoreCase)) {
                    this.UpdateActionButtonState();
                }
            });
        }

        /// <summary>
        /// Подпись очереди в строке списка игр. Строка сама перерисовывается через
        /// PropertyChanged — без Items.Refresh(), который на каждый тик прогресса
        /// пересобирал бы все карточки.
        /// </summary>
        private void SetQueueLabel(string gameId, string label) {
            try {
                var g = this.games.FirstOrDefault(x => string.Equals(x.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                if (g != null) {
                    g.QueueLabel = label;
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"SetQueueLabel({gameId}): {ex.Message}");
            }
        }

        /// <summary>Порядок ожидающих позиций поменялся — перестраиваем список целиком в новом порядке.</summary>
        private void OnQueueReordered(IReadOnlyList<Core.Game.QueueItem> snapshot) {
            this.Dispatcher.BeginInvoke(() => {
                this.queueDockItems.Clear();
                foreach (var item in snapshot) {
                    this.queueDockItems.Add(item);
                    this.SetQueueLabel(item.GameId, Core.UI.QueueRowLabel.For(item));
                }

                this.SyncQueuePanelVisibility();
            });
        }

        private static int IndexOfQueueItem(System.Collections.ObjectModel.ObservableCollection<Core.Game.QueueItem> items, string gameId) {
            for (var i = 0; i < items.Count; i++) {
                if (string.Equals(items[i].GameId, gameId, StringComparison.OrdinalIgnoreCase)) {
                    return i;
                }
            }

            return -1;
        }

        // --- Галерея игры -----------------------------------------------------------------

        /// <summary>Запускает загрузку обложки витрины для игры, отменив предыдущую.</summary>
        /// <param name="gid">Игра, чью обложку показываем; пустое значение — просто снять предыдущую загрузку.</param>
        private void StartHeroGalleryLoad(string? gid) {
            // Галерея — своя, независимая от гонки за selectionGate загрузка: не должна
            // задерживать/блокировать основную (новости/версии/сборки). Но предыдущий запрос
            // всё равно отменяем тем же приёмом, что и above для selectionCts — иначе прокрутка
            // стрелками по списку игр запускает по HTTP-запросу на каждый шаг, и все они доходят
            // до конца впустую (применяется только последний). Диспоз — забота самого
            // LoadHeroGalleryAsync (в своём finally, после await), а не этого места: диспозить
            // предыдущий CTS сразу после Cancel() рискует ObjectDisposedException, если внутренний
            // HttpClient ещё регистрирует колбэк на токене в момент отмены.
            var galleryCtsLocal = new CancellationTokenSource();
            var previousGalleryCts = Interlocked.Exchange(ref this.galleryCts, galleryCtsLocal);
            try {
                previousGalleryCts?.Cancel();
            }
            catch (ObjectDisposedException) {
                // Прошлая загрузка уже закончилась и освободила свой источник — отменять
                // нечего. Это обычный ход событий при перещёлкивании списка игр, а не сбой:
                // на нём набегала тысяча строк WARN за сеанс, вытеснявших из лога всё остальное.
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"StartHeroGalleryLoad: отмена предыдущей загрузки галереи: {ex.Message}");
            }

            _ = this.LoadHeroGalleryAsync(gid, galleryCtsLocal);
        }

        // Владеет переданным cts целиком — сама диспозит его в finally, после того как
        // её собственная работа (успешно или нет) завершилась. Так у ЛЮБОГО экземпляра,
        // включая самый последний за время жизни страницы, гарантированно есть момент
        // диспоза (раньше это было заботой вызывающего кода, который диспозил только
        // «предыдущий» CTS при следующем выборе — последний созданный экземпляр не
        // диспозился никогда).
        private async Task LoadHeroGalleryAsync(string? gameId, CancellationTokenSource cts) {
            try {
                if (string.IsNullOrWhiteSpace(gameId)) {
                    return;
                }

                IReadOnlyList<Core.Game.GalleryImage> images;
                try {
                    images = await this.galleryClient.GetGalleryAsync(this.BaseApi, gameId!, cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) {
                    return;
                }

                if (cts.Token.IsCancellationRequested || !string.Equals(this.GetSelectedGameId(), gameId, StringComparison.OrdinalIgnoreCase)) {
                    // Выбор уже сменился, пока грузилась галерея прошлой игры — не перетираем витрину.
                    return;
                }

                var cover = images.FirstOrDefault();
                if (cover == null) {
                    this.HeroCoverImg.Visibility = Visibility.Collapsed;
                    this.HeroCoverBrush.ImageSource = null;
                    return;
                }

                try {
                    // Обложку качаем и собираем тем же загрузчиком, что и значки игр:
                    // у BitmapImage со своим UriSource удалённая картинка докачивается уже
                    // после создания — заморозить её нельзя, а без заморозки не обойти её
                    // же кеш адресов, из-за которого заменённая обложка не появлялась до
                    // перезапуска лаунчера. Витрина на этом ловила ошибку и пряталась,
                    // показывая вместо обложки значок игры.
                    var bmp = await Core.Home.ImageLoader.LoadFrozenAsync(cover.ImageUrl).ConfigureAwait(true);
                    if (cts.Token.IsCancellationRequested
                        || !string.Equals(this.GetSelectedGameId(), gameId, StringComparison.OrdinalIgnoreCase)) {
                        // Пока качалась обложка, выбрали другую игру — её витрину не трогаем.
                        return;
                    }

                    this.HeroCoverBrush.ImageSource = bmp;
                    this.HeroCoverImg.Visibility = Visibility.Visible;
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Warn($"LoadHeroGalleryAsync: не удалось загрузить обложку {gameId}: {ex.Message}");
                    this.HeroCoverImg.Visibility = Visibility.Collapsed;
                }
            }
            finally {
                cts.Dispose();
            }
        }

        // Сам запуск живёт в Core/Home/GameLaunch: здесь только показ того, чем он кончился.
        private void PlaySelectedGame() {
            var gid = this.GetSelectedGameId();

            // Игра без модов идёт мимо StartLaunchOption с его проверкой, а «Играть» из
            // трея не смотрит на выключенную кнопку витрины: без этой ветки вторая копия
            // поднималась бы именно отсюда.
            // Игра без модпака живёт одной версией — сборкой с сервера без модов.
            var soleVariant = Core.Game.RunningGames.StateOf(gid, Core.Mods.LaunchTarget.LocalVanilla);
            if (Core.Game.RunningGameLook.Refusal(soleVariant) is { Length: > 0 } busy) {
                this.ShowToast(busy);
                return;
            }

            // У игры с модами запусков четыре: своя копия из Steam или сборка с сервера,
            // каждая с модами или без. Выбор показывается меню на кнопке «Играть», а не
            // отдельным экраном: выбирать тут нечего кроме этих четырёх, и лишний экран
            // между «хочу играть» и игрой никому не нужен.
            var selected = this.games?.FirstOrDefault(g => g.GameId == gid);
            if (selected?.Mods is { } modsCfg && !string.IsNullOrWhiteSpace(modsCfg.SteamAppId)) {
                // Запомненный вариант стартует сразу, остальные — под стрелкой рядом.
                // Меню на каждый запуск брало по два клика и ничем не показывало, что
                // оно вообще откроется вместо игры.
                if (this.PreferredLaunch(selected) is { } preferred) {
                    this.StartLaunchOption(selected, preferred);
                }
                else {
                    this.ShowModsLaunchMenu(selected);
                }

                return;
            }

            var result = GameLaunch.Play(gid, this.games, Core.Maintenance.MaintenanceService.Current);
            switch (result.Outcome) {
                case LaunchOutcome.ExeMissing:
                case LaunchOutcome.Failed:
                    this.ShowUserError(result.Message, result.Error, result.Context);
                    break;
                case LaunchOutcome.BlockedByMaintenance:
                case LaunchOutcome.UnfinishedUpdate:
                    this.StatusText.Text = result.Message;
                    this.UpdateActionButtonState();
                    break;
                case LaunchOutcome.Started:
                    this.UpdateActionButtonState();
                    break;
                default:
                    this.StatusText.Text = result.Message;
                    break;
            }
        }

        /// <summary>
        /// Показывает меню с вариантами запуска игры с модами.
        /// <para>
        /// Недоступные пункты остаются в меню, но выключены и подписаны причиной:
        /// исчезнувший пункт не объясняет игроку ничего, а «Steam не установлен» —
        /// объясняет. Поиск копии в Steam делается здесь же, при открытии меню, а не
        /// заранее: игру могли поставить или удалить, пока лаунчер был открыт.
        /// </para>
        /// </summary>
        /// <param name="game">Выбранная игра.</param>
        /// <param name="onlyHidden">
        /// Показать только то, чего нет кнопками на витрине. Стрелка на то и стрелка,
        /// что под ней лежит остальное: повторять в ней «Steam · с модами», когда он
        /// стоит кнопкой в сантиметре левее, значит спрашивать дважды об одном.
        /// </param>
        private void ShowModsLaunchMenu(GameInfo game, bool onlyHidden = false) {
            try {
                this.InvalidateLaunchOptions();
                var options = this.LaunchOptionsFor(game, logSteam: true);
                var shown = onlyHidden
                    ? Core.Mods.LaunchButtons.MenuOptions(options, this.launchBar?.Buttons)
                    : options;

                // Меню цепляется к тому, что сейчас на экране: «Играть» может быть
                // спрятана кнопками запуска, а всплывашка у невидимой кнопки уезжает
                // в угол окна.
                var anchor = onlyHidden || this.ActionBtn.Visibility != Visibility.Visible
                    ? (FrameworkElement)this.LaunchMenuBtn
                    : this.ActionBtn;
                var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };

                var remembered = Core.Mods.LaunchChoice.Remembered(game.GameId);
                foreach (var option in shown) {
                    var item = new MenuItem {
                        Header = option.MenuText,
                        IsEnabled = option.Available,
                        Tag = option,

                        // Строка меню говорит «Steam не установлен», подсказка — что с
                        // этим делать. Раньше длинные объяснения были написаны, но не
                        // показывались нигде: игрок видел только короткую пометку.
                        ToolTip = string.IsNullOrEmpty(option.Hint) ? null : option.Hint,

                        // Галочка у текущего выбора: меню из четырёх строк без неё не
                        // отвечает на вопрос «а что запускается сейчас».
                        IsChecked = option.Target == remembered,
                        IsCheckable = false,
                    };
                    item.Click += this.ModsLaunchItem_Click;
                    menu.Items.Add(item);
                }

                menu.IsOpen = true;
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось составить список вариантов запуска.", ex, "HomePage.ShowModsLaunchMenu");
            }
        }

        /// <summary>
        /// Чем узнавать состояние копий игры: реестр Windows и файловая система.
        /// <para>
        /// Настоящие обращения к машине собраны здесь, чтобы всё остальное —
        /// «какие варианты предложить» и «что сделает нажатие» — оставалось
        /// проверяемым кодом в Core.Mods.
        /// </para>
        /// </summary>
        /// <param name="logSteam">Писать ли в журнал ход поиска копии в Steam.</param>
        /// <returns>Набор проб.</returns>
        private Core.Mods.LaunchProbes LaunchProbes(bool logSteam)
            => new(
                Core.Home.GameLocalState.GameLocalRoot,
                Core.Home.GameLocalState.HasAnyLocalGameFiles,
                Core.Mods.SteamLocator.Locate,
                Core.Home.GameLocalState.ReadModsVersionAt,
                logSteam ? Core.Logging.Logger.Info : null,
                Core.Mods.ModPackFiles.Broken);

        /// <summary>
        /// Считает четыре (или два) варианта запуска на текущий момент.
        /// <para>
        /// Поиск копии в Steam делается при каждом вызове, а не заранее: игру могли
        /// поставить или удалить, пока лаунчер был открыт. Двух вариантов вместо
        /// четырёх — когда у игры нет сборки на сервере: такая живёт только копией
        /// из Steam.
        /// </para>
        /// </summary>
        /// <param name="game">Выбранная игра.</param>
        /// <param name="logSteam">Писать ли в журнал ход поиска копии в Steam.</param>
        /// <returns>Варианты запуска.</returns>
        private IReadOnlyList<Core.Mods.LaunchOption> LaunchOptionsFor(GameInfo game, bool logSteam)
            => Core.Mods.LaunchPlan.OptionsFor(game, this.LaunchProbes(logSteam));

        /// <summary>
        /// Какой вариант запустится по «Играть»: запомненный, если он сейчас доступен.
        /// <para>
        /// Доступность пересчитывается здесь же, а не берётся из памяти: игру могли
        /// удалить из Steam с прошлого запуска, и молча подставить вместо неё другую
        /// копию — худший из возможных исходов.
        /// </para>
        /// </summary>
        /// <param name="game">Выбранная игра.</param>
        /// <returns>Вариант запуска или null, если спрашивать всё-таки надо.</returns>
        private Core.Mods.LaunchOption? PreferredLaunch(GameInfo game) {
            try {
                return Core.Mods.LaunchChoice.Preferred(game.GameId, this.LaunchOptionsFor(game, logSteam: false));
            }
            catch (Exception ex) {
                // Не смогли посчитать — покажем меню. Это хуже на один клик и лучше
                // на одну неверную догадку.
                Core.Logging.Logger.Warn($"PreferredLaunch({game.GameId}): {ex.Message}");
                return null;
            }
        }

        /// <summary>Запускает выбранный в меню вариант.</summary>
        /// <param name="sender">Пункт меню.</param>
        /// <param name="e">Аргументы события.</param>
        private void ModsLaunchItem_Click(object sender, RoutedEventArgs e) {
            if (sender is not MenuItem { Tag: Core.Mods.LaunchOption option }) {
                return;
            }

            var gid = this.GetSelectedGameId();
            var game = this.games?.FirstOrDefault(g => g.GameId == gid);
            if (game?.Mods != null) {
                this.StartLaunchOption(game, option);
            }
        }

        /// <summary>
        /// Доводит выбранную строку меню до игры: решение, память, установка, запуск.
        /// <para>
        /// Вся цепочка живёт в <see cref="Core.Mods.LaunchRunner"/>: здесь остаются
        /// только настоящие обращения к окну — строка состояния, всплывашка,
        /// очередь загрузок и сам старт процесса.
        /// </para>
        /// </summary>
        /// <param name="game">Игра.</param>
        /// <param name="option">Что запускаем.</param>
        private void StartLaunchOption(GameInfo game, Core.Mods.LaunchOption option) {
            try {
                // ПОСЛЕДНИЙ РУБЕЖ ПРОТИВ ВТОРОЙ КОПИИ ИГРЫ. Выключенных кнопок мало:
                // сюда ведут ещё меню под стрелкой, «Играть» из трея и горячий клик,
                // успевший пройти до перерисовки витрины. Проверка одна на все входы.
                var running = Core.Game.RunningGames.StateOf(game.GameId, option.Target);
                if (Core.Game.RunningGameLook.Refusal(running) is { Length: > 0 } busy) {
                    this.ShowToast(busy);
                    return;
                }

                var runner = new Core.Mods.LaunchRunner(new Core.Mods.LaunchUi {
                    SetStatus = text => this.StatusText.Text = text,
                    Toast = text => this.ShowToast(text),
                    Enqueue = gid => this.downloadQueue.Enqueue(gid),
                    RefreshChoice = () => {
                        this.InvalidateLaunchOptions();
                        this.SyncLaunchBar(this.actionMode);
                    },
                    InstallMods = (g, title, dir, repair) => this.InstallModsToSteamAsync(g, title, dir, repair),
                    Launch = this.LaunchNow,
                }) {
                    ModsBusy = () => this.steamModsInstalling,
                };

                _ = runner.RunAsync(
                    game, option, Core.Maintenance.MaintenanceService.Current, this.LaunchProbes(logSteam: false));
            }
            catch (Exception ex) {
                this.ShowUserError("Не удалось запустить игру.", ex, "HomePage.StartLaunchOption");
            }
        }

        /// <summary>Собственно старт процесса игры и отметка в статистике.</summary>
        /// <param name="game">Игра.</param>
        /// <param name="option">Готовый к запуску вариант.</param>
        private void LaunchNow(GameInfo game, Core.Mods.LaunchOption option) {
            var steam = Core.Mods.SteamLocator.Locate(game.Mods!.SteamAppId, game.Mods.SteamFolder);
            var proc = Core.Mods.ModsLaunch.Start(option, game.Mods, game.ExeRelativePath, steam);
            if (proc == null && option.ViaSteam) {
                // Steam.exe завершается сразу, отдав команду; отсутствие процесса тут
                // не ошибка. Настоящий отказ уже записан в журнал внутри ModsLaunch.
                // Про сам запуск строке внизу говорить нечего: «Запускается…» уже стоит
                // на кнопке, в бейдже витрины и в строке списка (Core.Game.RunningGameLook),
                // а строка внизу это же слово никогда потом не убирала.
            }
            else if (proc == null) {
                this.StatusText.Text = "Не удалось запустить игру. Подробности в журнале.";
            }
            else {
                this.StatusText.Text = string.Empty;
            }

            if (proc != null || option.ViaSteam) {
                Core.Metrics.MetricsService.GameLaunch(game.GameId, game.Mods.Version);

                // Отсчёт наигранного времени и срок жизни включённых модов. Через Steam
                // процесс игры ещё предстоит дождаться, поэтому вызов ничего не ждёт.
                Core.Game.GameSession.Begin(
                    game.GameId,
                    option.GameDir,
                    Core.Mods.ModsLaunch.ResolveExe(option.GameDir, game.ExeRelativePath),
                    proc,
                    option.ViaSteam,
                    option.Modded ? option.GameDir : null,
                    option.Target);
            }
        }

        /// <summary>
        /// Выделяет игру в списке по идентификатору — так с рабочего стола приходит ярлык
        /// (см. <see cref="Core.Shell.ShortcutTarget"/>). Дальше человек видит обычную
        /// главную: витрину этой игры, её состояние и кнопку запуска.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>false, если такой игры в каталоге нет.</returns>
        internal bool SelectGameById(string? gameId) {
            var game = this.games?.FirstOrDefault(g =>
                g != null && string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            if (game == null) {
                return false;
            }

            // Набранный в поиске запрос мог отфильтровать эту игру из списка, а выделять
            // скрытую строку бессмысленно: экран остался бы прежним, будто ярлык не нажимали.
            // Фильтр переставит сам обработчик GameSearch_TextChanged.
            if (!string.IsNullOrEmpty(this.GameSearchBox?.Text)) {
                this.GameSearchBox.Text = string.Empty;
            }

            this.GameList.SelectedItem = game;
            this.GameList.ScrollIntoView(game);
            return true;
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

        /// <summary>
        /// Перезагружает значки уже показанных строк списка.
        /// <para>
        /// Значок грузится обработчиком <c>Loaded</c>, то есть в момент, когда строку
        /// создают. Раньше строки пересоздавались на каждом обновлении списка — заодно
        /// перечитывались и значки. Теперь строки живут, и сброшенный кеш обложек сам по
        /// себе ничего бы не изменил: «Обновить список игр» перестал бы обновлять
        /// картинки, ради которых кеш и сбрасывают.
        /// </para>
        /// </summary>
        private void ReloadGameIcons() {
            try {
                foreach (var img in Core.UI.VisualTreeSearch.Descendants<Image>(this.GameList)) {
                    Core.Home.ImageLoader.AttachAndLoad(img, this.BaseApi);
                }
            }
            catch (Exception ex) {
                // Картинки — украшение: не обновились, значит останутся прежними.
                Core.Logging.Logger.Warn($"ReloadGameIcons: {ex.Message}");
            }
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

                // Правило и проход — в Core.UI.GameMenuItems: внутри WPF-меню их никто не
                // проверит, а ошибка в них выглядит как пункт, который не работает.
                Core.UI.GameMenuItems.Apply(fe?.ContextMenu?.Items, gi, hasFiles);
            }
            catch (Exception ex) {
                // Не смогли подготовить меню — лучше его не показывать вовсе
                Core.Logging.Logger.Warn($"GameItem_ContextMenuOpening: {ex.Message}");
                e.Handled = true;
            }
        }


        /// <summary>
        /// Сама установка: качает модпак в папку Steam, не занимая UI-поток.
        /// </summary>
        /// <param name="game">Игра из каталога.</param>
        /// <param name="title">Название игры для подписей.</param>
        /// <param name="steamDir">Найденная папка копии из Steam.</param>
        /// <param name="repair">
        /// Моды уже стоят, и вернуть надо только пропавшее. Меняет одни подписи: работа
        /// та же самая, а «Установка модов…» над починкой двух файлов вводит в
        /// заблуждение ровно там, где игрок и так недоволен.
        /// </param>
        /// <returns>Задача установки.</returns>
        private async Task<bool> InstallModsToSteamAsync(
            GameInfo game, string title, string steamDir, bool repair = false) {
            var view = new Core.Game.SyncProgressView();
            var started = DateTime.UtcNow;

            // Progress создаётся на UI-потоке и поэтому сам возвращает отчёты сюда же:
            // служба синхронизации репортит из фоновых задач.
            var progress = new Progress<SyncProgress>(
                p => this.ApplySyncDisplay(view.Describe(p, (DateTime.UtcNow - started).TotalSeconds)));

            this.steamModsInstalling = true;

            // Полоса включается ДО текста: смена текста перерисовывает нижнюю панель,
            // и включённый после неё бегунок остаётся скрытым до следующего события.
            this.UpdateProgress.IsIndeterminate = true;
            this.SpeedEtaText.Text = string.Empty;
            this.FilesSizeText.Text = string.Empty;
            this.StatusText.Text = repair
                ? $"Восстановление модов в копии {title} из Steam…"
                : $"Установка модов в копию {title} из Steam…";
            this.SyncBottomBarVisibility();

            try {
                // Отмены у этой операции нет намеренно: прервать её на середине означает
                // оставить в чужой установке Steam половину модпака.
                var result = await Core.Mods.ModsService.EnsureAsync(
                    game, steamDir, this.BaseApi, this.sync, progress, CancellationToken.None).ConfigureAwait(true);

                var message = Core.Home.SteamModsInstall.DescribeResult(result, title, repair);
                if (result.Ok) {
                    // Об успехе говорит всплывашка; строку внизу гасим, чтобы панель ушла.
                    this.StatusText.Text = string.Empty;
                    this.ShowToast(message);
                }
                else {
                    this.ShowUserError(message, null, "HomePage.InstallModsToSteamAsync");
                }

                return result.Ok;
            }
            finally {
                this.steamModsInstalling = false;
                this.UpdateProgress.IsIndeterminate = false;
                this.UpdateProgress.Value = 0;
                this.SpeedEtaText.Text = string.Empty;
                this.FilesSizeText.Text = string.Empty;

                // Модпак только что лёг в чужую папку Steam: снимок вариантов, снятый до
                // установки, всё ещё утверждает «установить моды».
                this.InvalidateLaunchOptions();
                this.UpdateActionButtonState();
            }
        }

        /// <summary>
        /// Раскладывает отчёт о прогрессе по подписям нижней панели. Null в поле —
        /// «этой строки стадия не касается», её прежнее значение остаётся.
        /// </summary>
        /// <param name="display">Что показать.</param>
        private void ApplySyncDisplay(Core.Game.SyncProgressDisplay display) {
            try {
                if (display.Indeterminate is { } indeterminate) {
                    this.UpdateProgress.IsIndeterminate = indeterminate;
                }

                if (display.Value is { } value) {
                    this.UpdateProgress.Value = value;
                }

                if (display.Status is { } status) {
                    this.StatusText.Text = status;
                }

                if (display.SpeedEta is { } speedEta) {
                    this.SpeedEtaText.Text = speedEta;
                }

                if (display.FilesSize is { } filesSize) {
                    this.FilesSizeText.Text = filesSize;
                }
            }
            catch (Exception ex) {
                // Подписи — не повод ронять установку, которая идёт нормально.
                Core.Logging.Logger.Warn($"HomePage.ApplySyncDisplay: {ex.Message}");
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
                win?.ContentFrame.Navigate(new GamePage(game, this.downloadQueue));
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
                this.verified.Reset();
                this.FilesSizeText.Text = string.Empty;
                Core.Logging.Logger.Info($"HomePage: папка игр изменилась на '{gamesPath}', статусы пересчитываются");
            }

            if (!gamesPathChanged && !GamePage.ConsumeLocalStateChanged()) {
                return;
            }

            try {
                var snapshot = this.games?.ToList() ?? new List<GameInfo>();
                await Task.Run(() => this.NormalizeGameIconsAndLocalState(snapshot));
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
        private void NormalizeGameIconsAndLocalState(IEnumerable<GameInfo> games) =>
            GameStatus.NormalizeIconsAndLocalState(games, this.BaseApi);

        private void MarkInstalled(string gameId, string? version) {
            try {
                // Строка списка перерисуется сама: «установлена» — свойство с
                // уведомлением. Пересортировку сделает фоновой шаг.
                GameStatus.MarkInstalled(this.games.FirstOrDefault(x => x.GameId == gameId), version);
            }
            catch (Exception ex) {
                // Версия на диске уже записана; здесь только обновление отображения
                Core.Logging.Logger.Error(ex, $"MarkInstalled(gid={gameId})");
            }
        }

        private void MarkUninstalled(string gameId) {
            try {
                var selectedId = this.GetSelectedGameId();
                GameStatus.MarkUninstalled(this.games.FirstOrDefault(x => x.GameId == gameId));

                this.games = this.catalog.Sort(this.games);

                // SetGamesSource, а не голое присваивание ItemsSource: смена источника
                // пересоздаёт представление списка вместе с фильтром поиска, и набранный
                // запрос молча слетал после удаления файлов игры.
                this.SetGamesSource();
                this.RestoreSelection(selectedId);
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

                // Установка ИМЕННО этой игры идёт прямо сейчас: Directory.Delete снёс бы файлы
                // из-под работающей закачки, а сама закачка продолжила бы писать в удаляемую
                // папку. Другая игра, качающаяся параллельно в очереди, тут не мешает —
                // список игр больше не блокируется целиком на время любой закачки.
                if (this.IsQueued(gid)) {
                    this.ShowToast("Идёт установка или обновление этой игры. Дождитесь завершения или снимите её с очереди.");
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
                this.SetActionMode(ActionMode.Deleting);

                // Подсказка «Нужно: N ГБ (M доступно)» — про установку, которой сейчас
                // нет; под строкой «Удаление файлов…» она читалась как её часть.
                // Полоса включается ДО текста статуса: смена текста перерисовывает
                // панель (OnStatusTextChanged), и бегунок, включённый после неё,
                // оставался скрытым до следующего события.
                this.FilesSizeText.Text = string.Empty;
                this.SpeedEtaText.Text = string.Empty;
                this.UpdateProgress.IsIndeterminate = true;
                this.StatusText.Text = $"Удаление файлов {title}…";
                this.SyncBottomBarVisibility();
                try {
                    // Directory.Delete(recursive) обрывается на ПЕРВОМ занятом файле, когда
                    // остальное уже снесено. Пользователь при этом видел «не удалось удалить»,
                    // игра продолжала числиться установленной — а на диске лежали её остатки,
                    // неспособные запуститься. Поэтому удаляем сами, по файлу, и доводим до
                    // конца: занятые собираем в список и потом честно называем.
                    var blocked = await Task.Run(() => GameFiles.DeleteGameFiles(localRoot));

                    // Ярлык уносим вместе с файлами: иначе на рабочем столе остаётся иконка,
                    // которая по клику ругается «не найден элемент».
                    await Task.Run(() => GameLocalState.TryRemoveDesktopShortcuts(localRoot));

                    ChillHub.Core.Sync.FileHashCache.Remove(gid);
                    this.spaceHint.Remember(gid, 0);
                    this.FilesSizeText.Text = string.Empty;

                    // Состояние обновляем в любом случае: игра с вырванными файлами не
                    // запустится, и показывать её установленной — врать пользователю.
                    this.MarkUninstalled(gid);

                    // Освободившиеся гигабайты обязаны отразиться и в «Установка и
                    // удаление программ»: размер там считается вместе с папкой игр
                    // (Core/Shell/InstalledAppsEntry.cs). Обход папки уходит в фон.
                    Core.Shell.InstalledAppsEntry.RefreshInBackground();

                    if (blocked.Count > 0) {
                        this.ShowUserError(
                            GameFiles.BuildBlockedFilesMessage(blocked),
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
