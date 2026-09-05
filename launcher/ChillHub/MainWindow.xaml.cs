// <copyright file="MainWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Threading;

    using ChillHub.Core.Shell;

    public partial class MainWindow : Window {
        /// <summary>
        /// Караоке в шапке — Баста, «Моя игра»: печать, паузы, курсор. Всё поведение —
        /// в <see cref="KaraokePresenter"/>; окно лишь сообщает ему, когда его видно.
        /// </summary>
        private readonly KaraokePresenter karaoke;

        /// <summary>
        /// Периодическая проверка самообновления, пока лаунчер уже работает: раньше
        /// <see cref="Core.SelfUpdate.SelfUpdateChecker"/> вызывался ровно один раз, до
        /// показа этого окна (см. <c>App.Application_Startup</c>) — тот, кто оставил лаунчер
        /// открытым надолго, новую версию не видел никогда. Тикает раз в
        /// <see cref="SelfUpdateCheckInterval"/> и переиспользует тот же
        /// <see cref="UpdateWindow"/>, что и стартовая проверка — значит, и то же правило
        /// «применяется только по явному клику», см. <see cref="SelfUpdateCheckTimer_Tick"/>.
        /// </summary>
        private readonly DispatcherTimer selfUpdateCheckTimer = new DispatcherTimer(DispatcherPriority.Background);

        /// <summary>Как часто дёргать сервер за номером версии, пока лаунчер открыт.</summary>
        private static readonly TimeSpan SelfUpdateCheckInterval = TimeSpan.FromMinutes(10);

        /// <summary>Не даёт двум проверкам обновления (тик таймера и разворачивание из трея) столкнуться.</summary>
        private bool selfUpdateCheckRunning;

        /// <summary>
        /// Найденное обновление, которое пока некому показать: окно свёрнуто, спрятано
        /// в трей или занято работой в очереди. Сам автомат — в
        /// <see cref="Core.SelfUpdate.SelfUpdateGate"/>, где его проверяют тесты.
        /// </summary>
        private readonly Core.SelfUpdate.SelfUpdateGate selfUpdateGate = new Core.SelfUpdate.SelfUpdateGate();

        /// <summary>
        /// То же для состояния модов в папке Steam: перечитывание лезет в реестр и обходит
        /// папку игры на диске, причём на UI-потоке. Пять секунд — вернувшийся из Steam
        /// человек разницы не заметит, а перещёлкивание окон перестаёт стоить дисковых
        /// обходов.
        /// </summary>
        private readonly Core.Shell.ActivationThrottle modsStateOnActivate =
            new Core.Shell.ActivationThrottle(TimeSpan.FromSeconds(5));

        /// <summary>
        /// Единственный экземпляр главной страницы. Раньше каждый клик по «Каталогу» создавал
        /// новый HomePage, а вместе с ним — ещё один FeedbackService со своей копией очереди и
        /// своим 10-секундным таймером, который никто не останавливал: таймер старой страницы
        /// перезаписывал feedback_queue.json без нового сообщения, и оно терялось навсегда.
        /// </summary>
        private Pages.HomePage? homePage;

        /// <summary>
        /// Значок в трее. Создаётся лениво, только когда впервые понадобился (первое
        /// сворачивание в трей), чтобы не заводить NotifyIcon для пользователей,
        /// у которых MinimizeToTray выключен.
        /// </summary>
        private TrayService? tray;

        /// <summary>Настоящий выход запрошен из трея — Closing больше не должен его перехватывать.</summary>
        private bool exitRequested;

        /// <summary>
        /// Настоящий выход запрошен самообновлением: апдейтеру нужен полностью завершённый
        /// процесс, а не окно, спрятанное в трей. Без этого флага
        /// <see cref="Application.Shutdown()"/>, вызванный из <c>UpdateWindow</c> после
        /// запуска апдейтера, натыкался на <see cref="MainWindow_Closing"/> — при включённом
        /// <see cref="Core.ConfigService.MinimizeToTray"/> тот отменял закрытие и просто
        /// прятал окно, а Shutdown() при отменённом закрытии окна процесс не завершает.
        /// Апдейтер в это время уже переписывал файлы лаунчера, соревнуясь за них с живым
        /// (просто невидимым) старым процессом.
        /// </summary>
        internal void PrepareForForcedExit() => this.exitRequested = true;

        public MainWindow() {
            this.InitializeComponent();

            // Размер окна: минимальный по умолчанию, свой — если пользователь его менял
            // (см. WindowSizeMemory). Ставится до показа, чтобы окно не прыгало на старте.
            var size = WindowSizeMemory.Restore(Core.ConfigService.Current, this.MinWidth, this.MinHeight);
            this.Width = size.Width;
            this.Height = size.Height;
            if (size.Maximized) {
                this.WindowState = WindowState.Maximized;
            }

            this.karaoke = new KaraokePresenter(this.KaraokeHost, this.KaraokeCurrentText, this.KaraokeNextText, this.KaraokeCaret);
            Console.WriteLine("[BOOT] Showing MainWindow");
            this.NavigateToHome();
            this.Closing += this.MainWindow_Closing;

            // Значок в трее живёт всё время работы приложения, а не только пока окно
            // спрятано: раньше он появлялся исключительно в MainWindow_Closing (уход в
            // трей) и пропадал в RestoreFromTray, поэтому у развёрнутого или обычного
            // окна значка в трее не было вовсе — «Открыть/Играть/Выйти» из трея были
            // недоступны, пока пользователь ни разу не сворачивал окно.
            this.EnsureTray().Show();

            // См. описание selfUpdateCheckTimer: первая проверка уже сделана при старте
            // (App.Application_Startup), поэтому таймер просто ждёт свой первый интервал,
            // а не бьёт по серверу сразу же вслед за стартовой проверкой.
            this.selfUpdateCheckTimer.Interval = SelfUpdateCheckInterval;
            this.selfUpdateCheckTimer.Tick += this.SelfUpdateCheckTimer_Tick;
            this.selfUpdateCheckTimer.Start();
            this.Closed += (s, e) => this.selfUpdateCheckTimer.Stop();

            // Караоке печатает только пока окно видно и активно: свёрнутое или ушедшее
            // на задний план окно ставит его на паузу, чтобы не жечь тики впустую.
            this.Loaded += (s, e) => this.karaoke.Start(KaraokeLyrics.Text);
            this.IsVisibleChanged += (s, e) => this.SyncKaraokeWithWindowState();
            this.StateChanged += (s, e) => this.SyncKaraokeWithWindowState();
            this.Activated += (s, e) => {
                this.karaoke.Resume();

                // ПРОВЕРКА НА КАЖДЫЙ ВОЗВРАТ ФОКУСА, без ограничителя частоты. Человек
                // пришёл к лаунчеру — самое время узнать, не появилось ли обновление;
                // любой срок ожидания здесь оборачивается молчанием ровно в тот момент,
                // когда на окно смотрят.
                //
                // Штормов активаций (Alt+Tab между окнами) бояться не нужно:
                // selfUpdateCheckRunning не даёт запросам идти внахлёст, и пока один в
                // пути, остальные просто ничего не делают.
                _ = this.RunSelfUpdateCheckAsync();

                // Режим работ — по той же причине: пока окно лежало без фокуса, работы могли
                // начаться или кончиться, и человек, вернувшийся к лаунчеру, должен увидеть
                // актуальную картину сразу, а не через остаток минутного интервала опроса.
                _ = Core.Maintenance.MaintenanceService.RefreshNowAsync();

                // И состояние модов в папке Steam: из Steam в лаунчер возвращаются именно
                // так, а за время отсутствия игру могли поставить, удалить или перенести
                // (см. HomePage.RefreshLaunchOptionsFromDisk).
                if (this.modsStateOnActivate.Allow()) {
                    this.CurrentHome?.RefreshLaunchOptionsFromDisk();
                }
            };
            this.Deactivated += (s, e) => this.karaoke.Pause();

            // Режим технических работ (задача 25): баннер в шапке появляется и исчезает сам,
            // по ответам сервера. Опрос переживает недоступный сервер молча.
            try {
                Core.Maintenance.MaintenanceService.Changed += this.OnMaintenanceChanged;
                this.Closed += (s, e) => {
                    Core.Maintenance.MaintenanceService.Changed -= this.OnMaintenanceChanged;
                    Core.Maintenance.MaintenanceService.Stop();
                    this.tray?.Dispose();
                };
                this.ApplyMaintenanceState(Core.Maintenance.MaintenanceService.Current);
                Core.Maintenance.MaintenanceService.Start();
            }
            catch (Exception ex) {
                // Баннер — вспомогательная информация: его отсутствие не повод не открывать окно
                Core.Logging.Logger.Error(ex, "MainWindow.MaintenanceInit");
            }
        }

        /// <summary>
        /// Показывает главную страницу, переиспользуя единственный экземпляр.
        /// Если она уже открыта — ничего не делает (как и «Настройки»).
        /// </summary>
        public void NavigateToHome() {
            try {
                if (!ShellNavigation.NeedsNavigation(this.ContentFrame.Content, typeof(Pages.HomePage))) {
                    return;
                }

                if (this.homePage == null) {
                    this.homePage = new Pages.HomePage();
                    this.AttachDownloadsIndicator(this.homePage.DownloadQueue);

                    // Шапка показывает то, что считает страница: свободное место и
                    // очищённый поиск. Обратно уходит только строка запроса.
                    this.homePage.DiskFreeChanged += text => this.DiskFreeText.Text = text;
                    this.homePage.SearchCleared += () => {
                        if (this.GameSearchBox.Text.Length > 0) {
                            this.GameSearchBox.Text = string.Empty;
                        }
                    };
                    this.homePage.ApplySearch(this.GameSearchBox.Text);
                }

                this.ContentFrame.Navigate(this.homePage);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "MainWindow.NavigateToHome");
                MessageBox.Show($"Не удалось открыть каталог: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Открывает окно «Что нового» по просьбе человека: из новостей лаунчера или
        /// из настроек. Ничего не спрашивает и отметку о показе не двигает — иначе
        /// нажатие «посмотреть список» отменяло бы автоматический показ следующего
        /// обновления.
        /// </summary>
        internal void ShowChangelog() {
            try {
                // Отмечаем по той же отметке, что и автоматический показ: после него
                // непросмотренного не остаётся, и открытый вручную список стоит чистым.
                this.FillChangelog(Core.ConfigService.Current.LastSeenChangelogVersion);
                this.ChangelogSubtitle.Text = $"У вас версия {this.LauncherVersion()}. Ниже — история обновлений лаунчера.";
                this.ChangelogOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "MainWindow.ShowChangelog");
            }
        }

        /// <summary>
        /// Показывает список сам, если лаунчер обновился с прошлого запуска. Решение —
        /// в <see cref="Core.Changelog.ChangelogGate"/>; здесь только показ и отметка.
        /// </summary>
        internal void ShowChangelogAfterUpdate() {
            try {
                var cfg = Core.ConfigService.Current;
                var lastSeen = cfg.LastSeenChangelogVersion ?? string.Empty;
                var current = this.LauncherVersion();
                if (!Core.Changelog.ChangelogGate.ShouldShow(lastSeen, current, Core.Changelog.ChangelogData.Visible)) {
                    return;
                }

                // Отмечаем ДО того, как отметка о показе съедет на текущую версию:
                // после неё новым не окажется ничего.
                var unseen = this.FillChangelog(lastSeen);
                this.ChangelogSubtitle.Text = string.IsNullOrWhiteSpace(lastSeen)
                    ? $"У вас версия {current}. Ниже — вся история обновлений лаунчера."
                    : unseen > 0
                        ? $"Лаунчер обновился до версии {current}. Всё, что вышло с прошлого раза, отмечено значком."
                        : $"Лаунчер обновился до версии {current}. Вот что изменилось.";
                this.ChangelogOverlay.Visibility = Visibility.Visible;

                // Отметку ставим на показе, а не на закрытии: окно можно закрыть и вместе
                // со всем лаунчером, и тогда список всплывал бы после каждого запуска.
                cfg.LastSeenChangelogVersion = current;
                if (!Core.ConfigService.TrySave(cfg, out var saveError)) {
                    Core.Logging.Logger.Warn($"Changelog: отметку о показе сохранить не удалось: {saveError}");
                }
            }
            catch (Exception ex) {
                // Список обновлений — приятная мелочь, ронять из-за неё запуск нельзя.
                Core.Logging.Logger.Error(ex, "MainWindow.ShowChangelogAfterUpdate");
            }
        }

        /// <summary>
        /// Наполняет окно и отмечает непросмотренные выпуски. Прокрутку возвращает
        /// в начало: открытый второй раз список должен начинаться сверху.
        /// </summary>
        /// <param name="lastSeenVersion">Версия, на которой список показывали в прошлый раз.</param>
        /// <returns>Сколько выпусков отмечено новыми.</returns>
        private int FillChangelog(string? lastSeenVersion) {
            var releases = Core.Changelog.ChangelogData.Visible;
            var unseen = Core.Changelog.ChangelogMarks.MarkUnseen(releases, lastSeenVersion);

            // Список один на весь запуск, а отметки на нём только что поменялись:
            // без сброса источника окно покажет подсветку прошлого открытия.
            this.ChangelogList.ItemsSource = null;
            this.ChangelogList.ItemsSource = releases;
            this.ChangelogScroll.ScrollToTop();
            return unseen;
        }

        private string LauncherVersion()
            => Core.SelfUpdate.SelfUpdateVersions.ReadLocalVersion(AppDomain.CurrentDomain.BaseDirectory);

        private void ChangelogClose_Click(object sender, RoutedEventArgs e)
            => this.ChangelogOverlay.Visibility = Visibility.Collapsed;

        /// <summary>
        /// Escape закрывает список обновлений. Обработчик висит на окне, а не на странице:
        /// оверлей теперь общий, и из настроек его иначе нечем было бы закрыть с клавиатуры.
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
            try {
                if (e.Key == System.Windows.Input.Key.Escape && this.ChangelogOverlay.Visibility == Visibility.Visible) {
                    e.Handled = true;
                    this.ChangelogOverlay.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex) {
                // Обработчик клавиш не имеет права ронять окно
                Core.Logging.Logger.Error(ex, "MainWindow.PreviewKeyDown");
            }
        }

        /// <summary>
        /// Крестик/Alt+F4: при включённом MinimizeToTray прячем окно в трей вместо закрытия.
        /// Единственный способ по-настоящему выйти в этом режиме — пункт «Выход»
        /// в меню значка (см. <see cref="EnsureTray"/>), который сам ставит <see cref="exitRequested"/>.
        /// Значок уже показан (см. конструктор) — трей живёт независимо от видимости окна.
        /// </summary>
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
            this.RememberWindowSize();
            try {
                if (this.exitRequested || !Core.ConfigService.Current.MinimizeToTray) {
                    return;
                }

                e.Cancel = true;
                this.Hide();
                this.ShowTrayHintOnce();
            }
            catch (Exception ex) {
                // Сбой сворачивания в трей не должен помешать пользователю закрыть окно
                Core.Logging.Logger.Error(ex, "MainWindow.MainWindow_Closing");
            }
        }

        /// <summary>
        /// Первое в жизни сворачивание в трей: окно исчезло по крестику, и без подсказки
        /// это выглядит как закрытие. Говорим один раз и запоминаем в конфиге — тот, кто
        /// прочитал, второй раз читать не хочет. Сбой сохранения флага не страшен: покажем ещё раз.
        /// </summary>
        private void ShowTrayHintOnce() {
            var cfg = Core.ConfigService.Current;
            if (cfg.TrayHintShown || this.tray == null) {
                return;
            }

            this.tray.Notify("Лаунчер продолжает работать в трее", "Закачки идут дальше. Открыть — клик по значку, выйти — «Выход» в его меню.");
            cfg.TrayHintShown = true;
            Core.ConfigService.Save(cfg);
        }

        /// <summary>
        /// Запоминает размер окна перед уходом (закрытие или в трей). RestoreBounds — размер
        /// в нормальном состоянии даже у развёрнутого окна. Сбой записи не мешает закрыться.
        /// </summary>
        private void RememberWindowSize() {
            try {
                var cfg = Core.ConfigService.Current;
                var bounds = this.RestoreBounds;
                var w = bounds.IsEmpty ? this.Width : bounds.Width;
                var h = bounds.IsEmpty ? this.Height : bounds.Height;
                if (WindowSizeMemory.Remember(cfg, w, h, this.WindowState == WindowState.Maximized)) {
                    Core.ConfigService.Save(cfg);
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"MainWindow.RememberWindowSize: {ex.Message}");
            }
        }

        /// <summary>Создаёт значок в трее при первой необходимости и подключает его меню к окну.</summary>
        private TrayService EnsureTray() {
            if (this.tray != null) {
                return this.tray;
            }

            var t = new TrayService();
            t.OpenRequested += (s, e) => this.RestoreFromTray();
            t.PlayRequested += (s, e) => this.PlayFromTray();

            // Имя игры — в момент открытия меню, а не при уходе в трей: значок виден
            // всегда, и выбор мог смениться, пока окно оставалось на экране.
            t.MenuOpening += (s, e) => t.SetCurrentGame(this.CurrentHome?.SelectedGameTitle);
            t.ExitRequested += (s, e) => {
                this.exitRequested = true;
                t.Hide();
                Application.Current.Shutdown();
            };
            this.tray = t;
            return t;
        }

        /// <summary>Главная страница, если она сейчас в кадре: меню трея работает через неё.</summary>
        private Pages.HomePage? CurrentHome => this.ContentFrame?.Content as Pages.HomePage;

        /// <summary>
        /// Пункт «Играть» из трея. Готовую игру запускает, не поднимая окно — ровно за этим
        /// пункт и нужен. Если играть пока нельзя (идёт установка, требуется обновление,
        /// статус ещё проверяется), окно показываем: действие всё равно потребует внимания.
        /// Что именно делать, решает <see cref="Core.UI.TrayPlayDecision"/>.
        /// </summary>
        private void PlayFromTray() {
            var home = this.CurrentHome;
            if (home == null) {
                this.RestoreFromTray();
                return;
            }

            var what = Core.UI.TrayPlayDecision.For(home.CanPlaySelectedGame, home.SelectedActionCancels);
            if (what != Core.UI.TrayPlay.Launch) {
                this.RestoreFromTray();
            }

            if (what != Core.UI.TrayPlay.ShowWindow) {
                home.InvokeSelectedAction();
            }
        }

        /// <summary>Возвращает окно из трея на экран. Значок в трее остаётся — см. конструктор.</summary>
        private void RestoreFromTray() {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();

            // Именно в момент разворачивания — свежая проверка, а не то, что успело натикать
            // в трее: лаунчер мог простоять свёрнутым дольше интервала таймера, и пользователь
            // не должен ждать следующего тика, чтобы узнать об обновлении.
            _ = this.RunSelfUpdateCheckAsync();

            // И свежее состояние технических работ — баннер и кнопки должны отражать то,
            // что на сервере сейчас, а не то, что было на последнем тике фонового опроса.
            // Activated после Show() тоже дёрнет опрос, но сервис схлопывает параллельные
            // запросы в один — см. MaintenanceService.RefreshNowAsync.
            _ = Core.Maintenance.MaintenanceService.RefreshNowAsync();
        }

        /// <summary>
        /// Реакция на повторный запуск лаунчера (ярлык, вторая копия), пока этот экземпляр уже
        /// жив — см. <see cref="Core.SingleInstance.StartListeningForShowRequests"/>. Если окно
        /// сейчас в трее — поднимает его оттуда; если уже на экране — просто выводит на передний
        /// план, чтобы повторный клик по ярлыку не выглядел так, будто ничего не произошло.
        /// </summary>
        internal void ShowAndActivate() {
            if (!this.IsVisible) {
                this.RestoreFromTray();
                return;
            }

            if (this.WindowState == WindowState.Minimized) {
                this.WindowState = WindowState.Normal;
            }

            this.Activate();
        }

        /// <summary>
        /// Открывает то, что просит ярлык с рабочего стола: главную с выделенной игрой, а
        /// если игры в каталоге нет — окно с предложением запустить установленную копию.
        /// <para>
        /// Ждёт загрузки каталога: запрос приходит сразу после запуска лаунчера, когда
        /// списка игр ещё нет, и без ожидания КАЖДАЯ игра выглядела бы пропавшей.
        /// </para>
        /// </summary>
        /// <param name="request">Запрос ярлыка; null — обычный запуск, делать нечего.</param>
        internal void HandleShortcutRequest(Core.Shell.ShortcutRequest? request) {
            if (request == null || string.IsNullOrWhiteSpace(request.GameId)) {
                return;
            }

            _ = this.OpenShortcutRequestAsync(request);
        }

        /// <summary>
        /// Открывает главную с выделенной игрой — из новости про эту игру.
        /// </summary>
        /// <param name="gameId">Игра из заметки.</param>
        internal void OpenGameFromNews(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            this.NavigateToHome();
            this.homePage?.SelectGameById(gameId);
        }

        private async Task OpenShortcutRequestAsync(Core.Shell.ShortcutRequest request) {
            try {
                // Именно главная, а не страница игры: запуск, обновление и моды живут
                // там же, где список, — ярлык обязан приводить туда, откуда в игру играют.
                this.NavigateToHome();
                var home = this.homePage;
                if (home == null) {
                    return;
                }

                await home.GamesLoaded;

                var action = Core.Shell.ShortcutOpen.Decide(request, home.Games);
                if (action == Core.Shell.ShortcutOpenAction.SelectGame && home.SelectGameById(request.GameId)) {
                    return;
                }

                if (action == Core.Shell.ShortcutOpenAction.None) {
                    return;
                }

                Core.Logging.Logger.Info($"Ярлык: игра '{request.GameId}' — {action}");
                var dialog = new ShortcutLaunchWindow(request, action) { Owner = this };
                dialog.ShowDialog();

                // Согласились скачать заново — качаем и поднимаем игру, когда докачается.
                // Само окно этого не умеет: очередь и запуск живут на главной.
                if (dialog.InstallRequested) {
                    home.InstallAndLaunch(request.GameId);
                }
            }
            catch (Exception ex) {
                // Ярлык не должен ронять уже открытый лаунчер: человек просто останется
                // на каталоге, а не окажется без окна вовсе.
                Core.Logging.Logger.Error(ex, "MainWindow.OpenShortcutRequest");
            }
        }

        /// <summary>Тик <see cref="selfUpdateCheckTimer"/> — то же самое, что и разворачивание из трея.</summary>
        private async void SelfUpdateCheckTimer_Tick(object? sender, EventArgs e) => await this.RunSelfUpdateCheckAsync();

        /// <summary>
        /// Спрашивает сервер о версии и, если есть что показать, показывает диалог (или
        /// откладывает его — см. <see cref="TryShowSelfUpdateDialog"/>). Вызывается и по
        /// расписанию (<see cref="selfUpdateCheckTimer"/> — версия проверяется независимо от
        /// того, видно ли окно, иначе лаунчер, оставленный в трее, никогда не узнал бы об
        /// обновлении), и сразу при разворачивании окна (<see cref="RestoreFromTray"/>), и
        /// при каждом возврате фокуса на окно (<see cref="Activated"/>).
        /// <para>
        /// Флаг <see cref="selfUpdateCheckRunning"/> не даёт этим двум вызовам столкнуться —
        /// если проверка уже идёт (например, только что начал тик), повторный запрос из
        /// RestoreFromTray просто ничего не делает: результат тика и так вот-вот появится.
        /// </para>
        /// </summary>
        private async Task RunSelfUpdateCheckAsync() {
            if (this.selfUpdateCheckRunning) {
                return;
            }

            this.selfUpdateCheckRunning = true;
            try {
                var precheck = await UpdateWindow.PrecheckAsync();

                // Актуальная версия — рассказывать нечего, тикаем дальше молча, как и при
                // старте (см. SelfUpdatePrecheck.NeedsWindow). Отложенное при этом
                // забываем: обновления больше нет, показывать его было бы враньём.
                if (!precheck.NeedsWindow) {
                    this.selfUpdateGate.Forget();
                    return;
                }

                // Ошибки и прочие неоднозначные исходы проверки (нет сети, битый манифест,
                // блокировка петли, итог прошлого обновления) пользователю показываем только
                // при старте лаунчера — см. App.Application_Startup. Пока лаунчер уже работает,
                // фоновая проверка молчит о них и просто попробует ещё раз по расписанию;
                // единственное, ради чего стоит прерывать пользователя на лету — реальная
                // доступная новая версия.
                if (precheck.Decision.State != Core.SelfUpdate.SelfUpdateState.UpdateAvailable) {
                    Core.Logging.Logger.Info($"Background self-update check: state={precheck.Decision.State}, dialog suppressed outside startup");

                    // Обновления больше нет (уже поставили, откатили на сервере) —
                    // отложенному показывать нечего.
                    this.selfUpdateGate.Forget();
                    return;
                }

                this.TryShowSelfUpdateDialog(precheck);
            }
            catch (Exception ex) {
                // Фоновая проверка не должна ронять лаунчер — просто попробуем в следующий раз.
                Core.Logging.Logger.Error(ex, "MainWindow.RunSelfUpdateCheckAsync");
            }
            finally {
                this.selfUpdateCheckRunning = false;
            }
        }

        /// <summary>
        /// Показывает диалог самообновления, если для этого подходящий момент, иначе просто
        /// молчит — следующий шанс спросить и показать будет либо по расписанию, либо сразу
        /// же при разворачивании окна (см. <see cref="RestoreFromTray"/>, которая гоняет
        /// свежую проверку сама, а не полагается на то, что могло устареть за это время).
        /// <para>
        /// Диалог тот же модальный <see cref="UpdateWindow"/>, что и при старте —
        /// применение обновления по-прежнему происходит исключительно по клику «Обновить и
        /// перезапустить» (см. <see cref="UpdateWindow.PrimaryBtn_Click"/>), значит лаунчер
        /// не может закрыться и начать обновление без ввода пользователя ни отсюда, ни оттуда.
        /// В отличие от старта: отказ или закрытие этого диалога НЕ завершает лаунчер —
        /// сюда не подшита никакая реакция на DialogResult.
        /// </para>
        /// </summary>
        private void TryShowSelfUpdateDialog(SelfUpdatePrecheck precheck) {
            // Окно спрятано в трее/свёрнуто или идёт работа в очереди — не время лезть с
            // диалогом. Но и забывать найденное нельзя: покажем, как только человек
            // вернётся к окну (см. pendingSelfUpdate и обработчик Activated).
            var ready = this.selfUpdateGate.Offer(
                precheck,
                this.IsVisible,
                this.WindowState == WindowState.Minimized,
                this.CurrentHome?.HasActiveDownloads == true);
            if (ready == null) {
                Core.Logging.Logger.Info(
                    $"Self-update {precheck.Decision.RemoteVersion} найдено, но показывать сейчас некому — отложено до возврата к окну");
                return;
            }

            var upd = new UpdateWindow(ready) {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            upd.ShowDialog();
        }

        /// <summary>
        /// Проверка обновления лаунчера по явной просьбе пользователя (кнопка в настройках).
        /// В отличие от фоновой (<see cref="RunSelfUpdateCheckAsync"/>) не молчит, когда
        /// версия актуальна: возвращает false, и вызывающий говорит об этом сам. Активная
        /// закачка игры её не откладывает — человек нажал кнопку и ждёт ответа сейчас.
        /// </summary>
        /// <returns>True — обновление есть и диалог показан; false — установлена последняя версия.</returns>
        internal async Task<bool> CheckForLauncherUpdateAsync() {
            if (this.selfUpdateCheckRunning) {
                return false;
            }

            this.selfUpdateCheckRunning = true;
            try {
                var precheck = await UpdateWindow.PrecheckAsync();
                if (!precheck.NeedsWindow) {
                    return false;
                }

                var upd = new UpdateWindow(precheck) {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                upd.ShowDialog();
                return true;
            }
            finally {
                this.selfUpdateCheckRunning = false;
            }
        }

        private void OnMaintenanceChanged(Core.Maintenance.MaintenanceState state) => this.ApplyMaintenanceState(state);

        /// <summary>
        /// Показывает или убирает баннер работ. Вызывается и при старте, и при каждой смене
        /// состояния — в том числе когда сервер сообщил, что работы закончены.
        /// </summary>
        private void ApplyMaintenanceState(Core.Maintenance.MaintenanceState? state) {
            try {
                if (this.MaintenanceBanner == null || this.MaintenanceBannerReason == null || this.MaintenanceBannerEta == null) {
                    return;
                }

                var view = MaintenanceBannerView.For(state);
                this.MaintenanceBannerReason.Text = view.Reason;
                this.MaintenanceBannerEta.Text = view.Eta;
                this.MaintenanceBanner.Visibility = view.Visible ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "MainWindow.ApplyMaintenanceState");
            }
        }

        /// <summary>
        /// Подписывает чип загрузок в шапке на очередь главной страницы. События летят из
        /// фонового воркера — на Dispatcher, как и в самой HomePage.
        /// </summary>
        private void AttachDownloadsIndicator(Core.Game.IDownloadQueue queue) {
            // Смена экрана меняет и судьбу чипа: на главной очередь стоит внизу целиком,
            // на остальных её не видно вовсе. Без этой подписки чип обновлялся только по
            // событиям очереди — уход в «Настройки» при молчащей закачке оставлял человека
            // вообще без индикатора.
            this.ContentFrame.Navigated += (s, e) => this.RefreshDownloadsIndicator(queue, e.Content is Pages.HomePage);

            void Refresh(Core.Game.QueueItem item) => this.Dispatcher.BeginInvoke(() => this.RefreshDownloadsIndicator(queue));
            queue.ItemAdded += Refresh;
            queue.ItemProgress += Refresh;
            queue.ItemCompleted += item => this.Dispatcher.BeginInvoke(() => {
                this.RefreshDownloadsIndicator(queue);
                this.NotifyDownloadFinished(item);
            });
            queue.ItemRemoved += Refresh;
            queue.Reordered += _ => this.Dispatcher.BeginInvoke(() => this.RefreshDownloadsIndicator(queue));
            this.RefreshDownloadsIndicator(queue);
        }

        /// <summary>
        /// Закачка закончилась, а окно спрятано в трей — единственное место сказать об этом
        /// пользователю — уведомление у значка. При видимом окне молчим: результат и так на экране.
        /// </summary>
        private void NotifyDownloadFinished(Core.Game.QueueItem item) {
            if (this.IsVisible || this.tray == null) {
                return;
            }

            switch (item.State) {
                case Core.Game.QueueItemState.Completed:
                    this.tray.Notify(item.Title, "Игра готова к запуску");
                    break;
                case Core.Game.QueueItemState.Failed:
                    this.tray.Notify(item.Title, "Не удалось скачать — откройте лаунчер");
                    break;
            }
        }

        /// <summary>
        /// Чип загрузок в шапке — замена очереди на тех экранах, где её не видно. На главной
        /// он молчит: там очередь стоит внизу целиком, и чип пересказывал её же цифры в
        /// полуметре выше — «31% · ещё 3» в шапке и та же закачка в доке одновременно.
        /// Подсказка трея живёт своей жизнью и обновляется всегда: окна может не быть на
        /// экране вовсе.
        /// </summary>
        /// <param name="queue">Очередь, по которой считается подпись.</param>
        /// <param name="onHome">
        /// Главная ли страница в кадре. Передаётся из события Navigated: там о новой
        /// странице знает сам аргумент события, а <see cref="CurrentHome"/> в этот момент
        /// может ещё отвечать про предыдущую.
        /// </param>
        private void RefreshDownloadsIndicator(Core.Game.IDownloadQueue queue, bool? onHome = null) {
            try {
                var text = Core.UI.DownloadsChip.Text(queue.Snapshot());
                var duplicatesDock = onHome ?? this.CurrentHome != null;
                this.HeaderDownloadsText.Text = text;
                this.HeaderDownloadsBtn.Visibility = text.Length > 0 && !duplicatesDock ? Visibility.Visible : Visibility.Collapsed;
                this.tray?.SetStatus(text);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"MainWindow.RefreshDownloadsIndicator: {ex.Message}");
            }
        }

        /// <summary>
        /// Набранное в поиске уходит на главную страницу. Поле живёт здесь, а список
        /// игр — там; страница о поле не знает и получает только строку.
        /// </summary>
        /// <param name="sender">Поле поиска.</param>
        /// <param name="e">Аргументы события.</param>
        private void GameSearch_TextChanged(object sender, TextChangedEventArgs e)
            => this.homePage?.ApplySearch(this.GameSearchBox.Text);

        private void SettingsBtn_Click(object sender, RoutedEventArgs e) {
            try {
                // Do not re-open Settings if it's already shown
                if (!ShellNavigation.NeedsNavigation(this.ContentFrame.Content, typeof(Pages.SettingsPage))) {
                    return;
                }

                this.ContentFrame.Navigate(new Pages.SettingsPage());
            }
            catch (System.Exception ex) {
                MessageBox.Show($"Не удалось открыть страницу настроек: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Theme toggle removed: single dark theme is used

        /// <summary>
        /// Окно на экране — печатаем и анимируем, ушло в трей или в панель задач — молчим.
        /// <para>
        /// Кроме караоке тем же переключателем гасятся бесконечные анимации разметки
        /// (см. <see cref="Core.UI.UiAnimations"/>): скелетоны и бегунки прячутся
        /// <c>Visibility.Collapsed</c>, но их клоки продолжали тикать на UI-потоке каждый
        /// кадр — спрятанный лаунчер жёг на этом около 2% ядра, свёрнутый в панель задач —
        /// ещё и видеокарту. Условия в разметке ловят скрытое окно и сами по
        /// <c>IsVisible</c>, а вот у свёрнутого окна <c>IsVisible</c> остаётся истинной —
        /// ради этого случая переключатель и нужен.
        /// </para>
        /// </summary>
        private void SyncKaraokeWithWindowState() {
            var onScreen = this.IsVisible && this.WindowState != WindowState.Minimized;
            Core.UI.UiAnimations.Instance.Enabled = onScreen;

            // Спрятанному окну незачем и опрашивать сервер о режиме работ: баннер
            // показывать некому, а вернувшись на экран, окно спросит само (Activated).
            // Лаунчер живёт в трее часами, и всё это время уходил запрос в минуту.
            Core.Maintenance.MaintenanceService.Suspended = !onScreen;

            if (onScreen) {
                this.karaoke.Resume();
            }
            else {
                this.karaoke.Pause();
            }
        }
    }
}
