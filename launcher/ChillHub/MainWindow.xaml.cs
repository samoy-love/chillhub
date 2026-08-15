// <copyright file="MainWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Animation;
    using System.Windows.Threading;

    using ChillHub.Core.Shell;

    public partial class MainWindow : Window {
        // Karaoke C4: two-line typewriter + crossfade
        // Use Render-priority DispatcherTimer and time-based character progression to keep constant speed under UI load
        private readonly DispatcherTimer karaokeTimer = new DispatcherTimer(DispatcherPriority.Render);
        private bool karaokePaused = false;
        private bool karaokeTransitionRunning = false;

        // --- Настройки караоке (одно место) ---
        // Все параметры поведения караоке сосредоточены в одном объекте (см. Core.Shell.KaraokeConfig)
        private readonly KaraokeConfig k = new KaraokeConfig();

        // Счёт символов и времени печати живёт отдельно от окна — см. Core.Shell.KaraokeTicker
        private readonly KaraokeTicker karaoke;

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
            this.karaoke = new KaraokeTicker(this.k);
            this.InitializeComponent();
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

            // Karaoke setup
            // Используем собранные настройки выше
            this.karaokeTimer.Interval = TimeSpan.FromMilliseconds(this.k.TimerTickMs);
            this.karaokeTimer.Tick += this.KaraokeTimer_Tick;
            this.Loaded += this.MainWindow_Loaded;
            this.IsVisibleChanged += this.MainWindow_IsVisibleChanged;
            this.StateChanged += this.MainWindow_StateChanged;
            this.Activated += (s, e) => {
                this.ResumeKaraoke();

                // Проверка версии не только при разворачивании из трея (RestoreFromTray),
                // но и при обычном возврате фокуса на окно: пользователь мог оставить
                // лаунчер открытым, но не активным, дольше интервала selfUpdateCheckTimer
                // — переключение назад не должно ждать следующего тика. selfUpdateCheckRunning
                // не даёт столкнуться с уже идущей проверкой (тиком или тем же RestoreFromTray).
                _ = this.RunSelfUpdateCheckAsync();
            };
            this.Deactivated += (s, e) => this.PauseKaraoke();

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

                this.homePage ??= new Pages.HomePage();
                this.ContentFrame.Navigate(this.homePage);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "MainWindow.NavigateToHome");
                MessageBox.Show($"Не удалось открыть каталог: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Крестик/Alt+F4: при включённом MinimizeToTray прячем окно в трей вместо закрытия.
        /// Единственный способ по-настоящему выйти в этом режиме — пункт «Выйти полностью»
        /// в меню значка (см. <see cref="EnsureTray"/>), который сам ставит <see cref="exitRequested"/>.
        /// </summary>
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
            try {
                if (this.exitRequested || !Core.ConfigService.Current.MinimizeToTray) {
                    return;
                }

                e.Cancel = true;
                var trayIcon = this.EnsureTray();
                // Имя подставляем в момент ухода в трей: пока окно на экране, меню
                // никто не видит, а выбранная игра до этого могла смениться.
                // Сам значок уже показан (см. конструктор) — трей живёт независимо
                // от видимости окна, прятать/показывать его заново не нужно.
                trayIcon.SetCurrentGame(this.CurrentHome?.SelectedGameTitle);
                this.Hide();
            }
            catch (Exception ex) {
                // Сбой сворачивания в трей не должен помешать пользователю закрыть окно
                Core.Logging.Logger.Error(ex, "MainWindow.MainWindow_Closing");
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
            t.CheckUpdatesRequested += (s, e) => this.CheckUpdatesFromTray();
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
        /// </summary>
        private void PlayFromTray() {
            var home = this.CurrentHome;
            if (home == null) {
                this.RestoreFromTray();
                return;
            }

            if (!home.CanPlaySelectedGame) {
                this.RestoreFromTray();
            }

            home.InvokeSelectedAction();
        }

        /// <summary>
        /// Пункт «Проверить обновления» из трея: перечитывает список игр и заново сверяет их
        /// статусы. Окно поднимаем — проверка идёт с индикатором и может закончиться
        /// предложением обновиться, а сообщать об этом некуда, пока окно спрятано.
        /// </summary>
        private void CheckUpdatesFromTray() {
            this.RestoreFromTray();
            this.CurrentHome?.RefreshGamesAndStatuses();
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
                // старте (см. SelfUpdatePrecheck.NeedsWindow).
                if (!precheck.NeedsWindow) {
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
            // Окно спрятано в трее/свёрнуто или идёт загрузка игры — не время лезть с диалогом
            // обновления.
            if (!this.IsVisible || this.WindowState == WindowState.Minimized || this.CurrentHome?.HasActiveDownloads == true) {
                return;
            }

            var upd = new UpdateWindow(precheck) {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            upd.ShowDialog();
        }

        private void OnMaintenanceChanged(Core.Maintenance.MaintenanceState state) => this.ApplyMaintenanceState(state);

        /// <summary>
        /// Показывает или убирает баннер работ. Вызывается и при старте, и при каждой смене
        /// состояния — в том числе когда сервер сообщил, что работы закончены.
        /// </summary>
        private void ApplyMaintenanceState(Core.Maintenance.MaintenanceState? state) {
            try {
                if (this.MaintenanceBanner == null || this.MaintenanceBannerText == null) {
                    return;
                }

                var view = MaintenanceBannerView.For(state);
                this.MaintenanceBannerText.Text = view.Text;
                this.MaintenanceBanner.Visibility = view.Visible ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "MainWindow.ApplyMaintenanceState");
            }
        }

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
        /// Контексты, по которым сбой караоке уже записан. Строка печатается ~30 раз в секунду,
        /// поэтому одну и ту же ошибку логируем один раз за сессию, иначе лог станет непригоден.
        /// </summary>
        private static readonly HashSet<string> KaraokeLoggedContexts = new(StringComparer.Ordinal);

        /// <summary>
        /// Караоке — украшение шапки: любая его ошибка не должна ни ронять окно, ни заливать лог.
        /// </summary>
        private static void LogKaraokeFailure(string context, Exception ex) {
            lock (KaraokeLoggedContexts) {
                if (!KaraokeLoggedContexts.Add(context)) {
                    return;
                }
            }

            Core.Logging.Logger.Warn($"Караоке в шапке, {context}: {ex.Message} (повторы не логируются)");
        }

        // --- Karaoke implementation ---
        private void MainWindow_Loaded(object? sender, RoutedEventArgs e) {
            try {
                this.InitKaraokeLyrics();
                this.UpdateKaraokeHostWidth();
                this.ResetKaraokeToStart();
                this.StartKaraoke();
            }
            catch (Exception ex) {
                LogKaraokeFailure("MainWindow_Loaded", ex);
            }
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
            if (this.IsVisible) {
                this.ResumeKaraoke();
            }
            else {
                this.PauseKaraoke();
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e) {
            if (this.WindowState == WindowState.Minimized) {
                this.PauseKaraoke();
            }
            else {
                this.ResumeKaraoke();
            }
        }

        private void InitKaraokeLyrics() {
            var raw = @"Моя игра, 98, Баста здесь 2006.

Моя игра -
Она мне принадлежит и таким же, как и я.

Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.
Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.

Со мною все нормально, ну и что, что кровь из носа,
Со мною все нормально, просто я стал очень взрослым,
Со мной все хорошо, просто я забыл, как дышать,
Я начал игру, но забыл, как играть.

Все нормально, просто стало вдруг темно,
На юге стало холодно, на севере - тепло,
Остался я один, сам по себе, сам за себя.
Остался только бог, который смотрит на меня.


Я много раз ошибался, делал что-то не так,
Но я вставал и делал следующий шаг.
Я верил людям, которым верить нельзя,
Они пользовались этим, но поверьте мне зря.

Были люди, да, на которых мог я опереться,
С чистым сердцем помогали мне они.
Но мои враги хотели смерти для меня,
Но я разбил их планы, ведь это - моя игра.


Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.
Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.


Улицы несут в себе боль и разочарование,
Минуты страха, минуты отчаяния
Люди от боли, без бога сходят с ума,
Но кто-то скажет равнодушно - такова судьба.

А кто-то, играя в игру, забывает о правилах.
И поздно понимает, что фортуна его оставила.
Кто-то правила игры подстраивает под себя.
Чтобы победителем быть всегда.

В игры играют дяди с большими пушками,
Связываться с ними - это не игрушки.
На мушке окажешься ты в один миг,
Чик-чик до выстрела, останется лишь крик.

В игры играют дома, там, где тепло,
Играют в шашки, в шахматы, в домино, но...
Я играю в игру - она моя.
Она мне принадлежит и таким же, как и я.


Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.
Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.


Если хочешь играть - играй
Если хочешь летать - лети
Жизнь - это тоже игра,
Если ты упал - встань и иди!


Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.
Моя игра, моя игра
Она мне принадлежит и таким же, как и я.
Моя игра, моя игра
Здесь правила одни, и цель одна.



";

            this.karaoke.SetLyrics(raw);
        }

        private void UpdateKaraokeHostWidth() {
            try {
                if (this.KaraokeHost == null || this.karaoke.Lines.Length == 0) {
                    return;
                }

                // Use the same font as current line for measuring (bolder and larger)
                var fontFamily = this.KaraokeCurrentText?.FontFamily ?? new FontFamily("Segoe UI");
                var fontStyle = this.KaraokeCurrentText?.FontStyle ?? FontStyles.Normal;
                var fontWeight = this.KaraokeCurrentText?.FontWeight ?? FontWeights.SemiBold;
                var fontStretch = this.KaraokeCurrentText?.FontStretch ?? FontStretches.Normal;
                var fontSize = this.KaraokeCurrentText?.FontSize ?? 14.0;

                double pixelsPerDip = 1.0;
                try {
                    pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                }
                catch (Exception ex) {
                    LogKaraokeFailure("определение DPI, берём 1.0", ex);
                }

                double max = 0.0;
                var typeface = new Typeface(fontFamily, fontStyle, fontWeight, fontStretch);
                foreach (var line in this.karaoke.Lines) {
                    var text = line ?? string.Empty;
                    var ft = new FormattedText(
                        text,
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        Brushes.Transparent,
                        pixelsPerDip);
                    if (ft.WidthIncludingTrailingWhitespace > max) {
                        max = ft.WidthIncludingTrailingWhitespace;
                    }
                }

                // Add internal padding (actual Border.Padding left+right) and a small safety margin
                double pad = 0.0;
                try {
                    pad = this.KaraokeHost.Padding.Left + this.KaraokeHost.Padding.Right;
                }
                catch (Exception ex) {
                    LogKaraokeFailure("чтение отступов контейнера, берём 16", ex);
                    pad = 16.0;
                }

                // padding + safety; минимум и максимум — чтобы не уходить в крайности
                this.KaraokeHost.Width = KaraokeTicker.HostWidth(max, pad);
            }
            catch (Exception ex) {
                LogKaraokeFailure("подбор ширины контейнера", ex);
            }
        }

        private void ResetKaraokeToStart() {
            // индексы и время-база сбрасываются вместе — см. KaraokeTicker.ResetToStart
            this.karaoke.ResetToStart(DateTime.UtcNow);
            this.SetKaraokeTexts(current: string.Empty, next: this.karaoke.NextLine);
        }

        private void SetKaraokeTexts(string current, string next) {
            try {
                this.KaraokeCurrentText.Text = current;
                this.KaraokeNextText.Text = next;
            }
            catch (Exception ex) {
                LogKaraokeFailure("вывод текста строки", ex);
            }
        }

        private void StartKaraoke() {
            this.karaokePaused = false;
            try {
                // Make sure current line is visible when typing begins
                this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeNextText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeCurrentText.Opacity = 1.0;
                this.KaraokeNextText.Opacity = 0.8;
            }
            catch (Exception ex) {
                LogKaraokeFailure("сброс анимаций при старте", ex);
            }

            // Backdate last progress to emit at least one character on first tick
            try {
                this.karaoke.BackdateForFirstChar(DateTime.UtcNow);
            }
            catch (Exception ex) {
                LogKaraokeFailure("сдвиг отметки прогресса при старте", ex);
            }

            if (!this.karaokeTimer.IsEnabled) {
                this.karaokeTimer.Start();
            }

            // Emit first character ASAP to show clear typing start
            try {
                this.KaraokeTimer_Tick(this, EventArgs.Empty);
            }
            catch (Exception ex) {
                LogKaraokeFailure("первый тик", ex);
            }
        }

        private void PauseKaraoke() {
            this.karaokePaused = true;
            if (this.karaokeTimer.IsEnabled) {
                this.karaokeTimer.Stop();
            }
            // start pause accounting
            this.karaoke.BeginPause(DateTime.UtcNow);
        }

        private void ResumeKaraoke() {
            this.karaokePaused = false;
            try {
                // Ensure visibility after resume
                this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeNextText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeCurrentText.Opacity = 1.0;
                this.KaraokeNextText.Opacity = 0.8;
            }
            catch (Exception ex) {
                LogKaraokeFailure("сброс анимаций при возобновлении", ex);
            }

            // accumulate paused time
            // сдвигаем маркер последнего прогресса вперёд на время паузы, чтобы при возобновлении не "догоняло" сразу всю строку
            try {
                this.karaoke.EndPause(DateTime.UtcNow);
            }
            catch (Exception ex) {
                LogKaraokeFailure("учёт длительности паузы", ex);
            }

            if (!this.karaokeTimer.IsEnabled) {
                this.karaokeTimer.Start();
            }
        }

        private void KaraokeTimer_Tick(object? sender, EventArgs e) {
            if (this.karaokePaused || this.karaokeTransitionRunning) {
                return;
            }

            // Time-based incremental progression with per-tick cap to preserve typing feel
            try {
                var now = DateTime.UtcNow;
                int add = this.karaoke.PlanAdvance(now);
                if (add > 0) {
                    // ensure current line visible while typing
                    try {
                        this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, null);
                        if (this.KaraokeCurrentText.Opacity < 1.0) {
                            this.KaraokeCurrentText.Opacity = 1.0;
                        }
                    }
                    catch (Exception ex) {
                        LogKaraokeFailure("подсветка текущей строки при печати", ex);
                    }

                    var current = this.karaoke.Type(add);
                    this.SetKaraokeTexts(current, this.karaoke.NextLine);

                    // advance lastProgress by the actual time "spent" on produced chars
                    try {
                        this.karaoke.CommitProgress(add);
                    }
                    catch (Exception ex) {
                        LogKaraokeFailure("сдвиг отметки прогресса", ex);
                        this.karaoke.ResetProgressTo(now);
                    }

                    if (!this.karaoke.LineComplete) {
                        return; // keep typing
                    }
                }
            }
            catch (Exception ex) {
                LogKaraokeFailure("тик печати", ex);
            }

            // Если строка ещё не дописана (добавлять нечего в этот тик) — просто ждём следующий тик
            if (!this.karaoke.LineComplete) {
                return;
            }
            // Линия завершена — небольшая пауза, затем плавный переход к следующей
            _ = this.TransitionToNextLineAsync();
        }

        private async Task TransitionToNextLineAsync() {
            if (this.karaokeTransitionRunning) {
                return;
            }

            this.karaokeTransitionRunning = true;
            try {
                // Пауза на строке перед переходом
                await Task.Delay(this.k.PauseAfterLineMs);

                // Кроссфейд (длительности берём из настроек)
                try {
                    var fadeOut = new DoubleAnimation { From = 1.0, To = 0.0, Duration = TimeSpan.FromMilliseconds(this.k.FadeOutMs) };
                    this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                    var fadeIn = new DoubleAnimation { From = 0.0, To = 1.0, Duration = TimeSpan.FromMilliseconds(this.k.FadeInMs) };
                    this.KaraokeNextText.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                catch (Exception ex) {
                    LogKaraokeFailure("кроссфейд между строками", ex);
                }

                await Task.Delay(this.k.AfterTransitionDelayMs);

                // Смена индексов вместе со сбросом время-базы новой строки
                this.karaoke.MoveToNextLine(DateTime.UtcNow);

                // Обновляем тексты: текущий пустой, next — следующий
                // Сброс анимаций и видимостей перед началом новой строки
                this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeNextText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeCurrentText.Opacity = 1.0;
                this.KaraokeNextText.Opacity = 0.8; // вернуть стандартную
                this.SetKaraokeTexts(string.Empty, this.karaoke.NextLine);
            }
            catch (Exception ex) {
                LogKaraokeFailure("переход к следующей строке", ex);
            }
            finally {
                this.karaokeTransitionRunning = false;
            }
        }
    }
}
