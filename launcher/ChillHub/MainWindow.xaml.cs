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

    public partial class MainWindow : Window {
        // Karaoke C4: two-line typewriter + crossfade
        // Use Render-priority DispatcherTimer and time-based character progression to keep constant speed under UI load
        private readonly DispatcherTimer karaokeTimer = new DispatcherTimer(DispatcherPriority.Render);
        private string[] karaokeLines = Array.Empty<string>();
        private int karaokeLineIndex = 0;
        private int karaokeCharIndex = 0;
        private bool karaokePaused = false;
        private bool karaokeTransitionRunning = false;
        // time-base for current line typing
        private DateTime karaokeLineStartAtUtc;
        private TimeSpan karaokePausedAccum = TimeSpan.Zero;
        private DateTime? karaokePauseStartedUtc = null;
        private DateTime karaokeLastProgressAtUtc;

        // --- Настройки караоке (одно место) ---
        // Все параметры поведения караоке сосредоточены в одном объекте ниже (см. KaraokeConfig)
        private readonly KaraokeConfig k = new KaraokeConfig();

        // Централизованная конфигурация караоке
        private sealed class KaraokeConfig {
            // Интервал печати одного символа (мс): меньше -> быстрее
            public int CharIntervalMs { get; init; } = 60;
            // Пауза после завершения строки перед переходом (мс)
            public int PauseAfterLineMs { get; init; } = 380;
            // Длительность затухания текущей строки (мс)
            public int FadeOutMs { get; init; } = 50;
            // Длительность появления следующей строки (мс)
            public int FadeInMs { get; init; } = 70;
            // Доп. задержка после анимации (мс)
            public int AfterTransitionDelayMs { get; init; } = 0;
            // Ограничение на макс. число символов, добавляемых за один тик (чтобы не "перескакивало" строку)
            public int MaxAdvanceCharsPerTick { get; init; } = 1;
            // Интервал тиков таймера (мс) — немного чаще печати, чтобы не пропускать символы
            public int TimerTickMs => Math.Max(10, this.CharIntervalMs / 2);
        }

        /// <summary>
        /// Единственный экземпляр главной страницы. Раньше каждый клик по «Каталогу» создавал
        /// новый HomePage, а вместе с ним — ещё один FeedbackService со своей копией очереди и
        /// своим 10-секундным таймером, который никто не останавливал: таймер старой страницы
        /// перезаписывал feedback_queue.json без нового сообщения, и оно терялось навсегда.
        /// </summary>
        private Pages.HomePage? homePage;

        public MainWindow() {
            this.InitializeComponent();
            Console.WriteLine("[BOOT] Showing MainWindow");
            this.NavigateToHome();

            // Karaoke setup
            // Используем собранные настройки выше
            this.karaokeTimer.Interval = TimeSpan.FromMilliseconds(this.k.TimerTickMs);
            this.karaokeTimer.Tick += this.KaraokeTimer_Tick;
            this.Loaded += this.MainWindow_Loaded;
            this.IsVisibleChanged += this.MainWindow_IsVisibleChanged;
            this.StateChanged += this.MainWindow_StateChanged;
            this.Activated += (s, e) => this.ResumeKaraoke();
            this.Deactivated += (s, e) => this.PauseKaraoke();

            // Режим технических работ (задача 25): баннер в шапке появляется и исчезает сам,
            // по ответам сервера. Опрос переживает недоступный сервер молча.
            try {
                Core.Maintenance.MaintenanceService.Changed += this.OnMaintenanceChanged;
                this.Closed += (s, e) => {
                    Core.Maintenance.MaintenanceService.Changed -= this.OnMaintenanceChanged;
                    Core.Maintenance.MaintenanceService.Stop();
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
                if (this.ContentFrame.Content is Pages.HomePage) {
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

                if (state is not { Enabled: true }) {
                    this.MaintenanceBanner.Visibility = Visibility.Collapsed;
                    this.MaintenanceBannerText.Text = string.Empty;
                    return;
                }

                this.MaintenanceBannerText.Text = state.BuildBannerText();
                this.MaintenanceBanner.Visibility = Visibility.Visible;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "MainWindow.ApplyMaintenanceState");
            }
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e) {
            try {
                // Do not re-open Settings if it's already shown
                if (this.ContentFrame.Content is Pages.SettingsPage) {
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

            // Разбиваем на строки, удаляем чисто пустые, но оставляем одинарные пустые как паузу
            var lines = raw.Replace("\r\n", "\n").Split('\n');
            this.karaokeLines = lines
                .Select(l => (l ?? string.Empty).TrimEnd())
                .ToArray();
            if (this.karaokeLines.Length == 0) {
                this.karaokeLines = new[] { string.Empty };
            }
        }

        private void UpdateKaraokeHostWidth() {
            try {
                if (this.KaraokeHost == null || this.karaokeLines == null || this.karaokeLines.Length == 0) {
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
                foreach (var line in this.karaokeLines) {
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
                double width = Math.Ceiling(max) + pad + 12; // padding + safety

                // Set a minimum and maximum to avoid extremes
                width = Math.Max(260, Math.Min(width, 800));
                this.KaraokeHost.Width = width;
            }
            catch (Exception ex) {
                LogKaraokeFailure("подбор ширины контейнера", ex);
            }
        }

        private void ResetKaraokeToStart() {
            this.karaokeLineIndex = 0;
            this.karaokeCharIndex = 0;
            this.SetKaraokeTexts(current: string.Empty, next: this.GetNextKaraokeLine());
            // reset time-base
            this.karaokeLineStartAtUtc = DateTime.UtcNow;
            this.karaokePausedAccum = TimeSpan.Zero;
            this.karaokePauseStartedUtc = null;
            this.karaokeLastProgressAtUtc = this.karaokeLineStartAtUtc;
        }

        private string GetCurrentKaraokeLine() {
            if (this.karaokeLines.Length == 0) {
                return string.Empty;
            }

            return this.karaokeLines[Math.Clamp(this.karaokeLineIndex, 0, this.karaokeLines.Length - 1)] ?? string.Empty;
        }

        private string GetNextKaraokeLine() {
            if (this.karaokeLines.Length == 0) {
                return string.Empty;
            }

            var idx = (this.karaokeLineIndex + 1) % this.karaokeLines.Length;
            return this.karaokeLines[idx] ?? string.Empty;
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
                this.karaokeLastProgressAtUtc = DateTime.UtcNow.AddMilliseconds(-this.k.CharIntervalMs);
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
            if (this.karaokePauseStartedUtc == null) {
                this.karaokePauseStartedUtc = DateTime.UtcNow;
            }
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
            if (this.karaokePauseStartedUtc != null) {
                var pausedDur = (DateTime.UtcNow - this.karaokePauseStartedUtc.Value);
                this.karaokePausedAccum += pausedDur;
                // сдвигаем маркер последнего прогресса вперёд на время паузы, чтобы при возобновлении не "догоняло" сразу всю строку
                try {
                    this.karaokeLastProgressAtUtc += pausedDur;
                }
                catch (Exception ex) {
                    LogKaraokeFailure("учёт длительности паузы", ex);
                }

                this.karaokePauseStartedUtc = null;
            }
            if (!this.karaokeTimer.IsEnabled) {
                this.karaokeTimer.Start();
            }
        }

        private void KaraokeTimer_Tick(object? sender, EventArgs e) {
            if (this.karaokePaused || this.karaokeTransitionRunning) {
                return;
            }

            var line = this.GetCurrentKaraokeLine();

            // Time-based incremental progression with per-tick cap to preserve typing feel
            try {
                var now = DateTime.UtcNow;
                var deltaMs = (now - this.karaokeLastProgressAtUtc).TotalMilliseconds;
                int add = (int)Math.Floor(deltaMs / Math.Max(1.0, this.k.CharIntervalMs));
                if (add > 0) {
                    if (add > this.k.MaxAdvanceCharsPerTick) {
                        add = this.k.MaxAdvanceCharsPerTick;
                    }
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

                    var newIndex = Math.Min(line.Length, this.karaokeCharIndex + add);
                    this.karaokeCharIndex = newIndex;
                    var current = line.Substring(0, this.karaokeCharIndex);
                    this.SetKaraokeTexts(current, this.GetNextKaraokeLine());

                    // advance lastProgress by the actual time "spent" on produced chars
                    var spentMs = add * this.k.CharIntervalMs;
                    try {
                        this.karaokeLastProgressAtUtc = this.karaokeLastProgressAtUtc.AddMilliseconds(spentMs);
                    }
                    catch (Exception ex) {
                        LogKaraokeFailure("сдвиг отметки прогресса", ex);
                        this.karaokeLastProgressAtUtc = now;
                    }

                    if (this.karaokeCharIndex < line.Length) {
                        return; // keep typing
                    }
                }
            }
            catch (Exception ex) {
                LogKaraokeFailure("тик печати", ex);
            }

            // Если строка ещё не дописана (добавлять нечего в этот тик) — просто ждём следующий тик
            if (this.karaokeCharIndex < line.Length) {
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

                // Смена индексов
                this.karaokeLineIndex = (this.karaokeLineIndex + 1) % this.karaokeLines.Length;
                this.karaokeCharIndex = 0;

                // Обновляем тексты: текущий пустой, next — следующий
                // Сброс анимаций и видимостей перед началом новой строки
                this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeNextText.BeginAnimation(UIElement.OpacityProperty, null);
                this.KaraokeCurrentText.Opacity = 1.0;
                this.KaraokeNextText.Opacity = 0.8; // вернуть стандартную
                this.SetKaraokeTexts(string.Empty, this.GetNextKaraokeLine());
                // reset time-base for new line
                this.karaokeLineStartAtUtc = DateTime.UtcNow;
                this.karaokePausedAccum = TimeSpan.Zero;
                this.karaokePauseStartedUtc = null;
                this.karaokeLastProgressAtUtc = this.karaokeLineStartAtUtc;
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
