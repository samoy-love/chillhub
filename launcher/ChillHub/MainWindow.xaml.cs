// <copyright file="MainWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
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
        private readonly DispatcherTimer karaokeTimer = new DispatcherTimer();
        private string[] karaokeLines = Array.Empty<string>();
        private int karaokeLineIndex = 0;
        private int karaokeCharIndex = 0;
        private bool karaokePaused = false;
        private bool karaokeTransitionRunning = false;
        private bool karaokeStarted = false;

        // --- Настройки караоке (собраны вместе) ---
        // Интервал таймера набора символов (мс): чем меньше, тем быстрее печатает
        private int karaokeCharIntervalMs = 55;

        // Пауза после завершения строки перед началом перехода (мс)
        private int karaokePauseAfterLineMs = 300;

        // Длительность плавного исчезновения текущей строки (мс) — меньше = быстрее переход
        private int karaokeFadeOutMs = 50;

        // Длительность плавного появления следующей строки (мс) — меньше = быстрее переход
        private int karaokeFadeInMs = 70;

        // Короткая задержка после анимации перед фактической сменой строки (мс) — 0, чтобы убрать "затуп"
        private int karaokeAfterTransitionDelayMs = 0;

        // Начальная задержка перед запуском караоке после первичной проверки файлов (мс)
        private int karaokeInitialDelayMs = 1200;

        public MainWindow() {
            this.InitializeComponent();
            Console.WriteLine("[BOOT] Showing MainWindow");
            this.ContentFrame.Navigate(new Pages.HomePage());

            // Karaoke setup
            // Используем собранные настройки выше
            this.karaokeTimer.Interval = TimeSpan.FromMilliseconds(this.karaokeCharIntervalMs);
            this.karaokeTimer.Tick += this.KaraokeTimer_Tick;
            this.Loaded += this.MainWindow_Loaded;
            this.IsVisibleChanged += this.MainWindow_IsVisibleChanged;
            this.StateChanged += this.MainWindow_StateChanged;
            this.Activated += (s, e) => this.ResumeKaraoke();
            this.Deactivated += (s, e) => this.PauseKaraoke();
        }

        private void CatalogBtn_Click(object sender, RoutedEventArgs e) {
            this.ContentFrame.Navigate(new Pages.HomePage());
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

        // --- Karaoke implementation ---
        private void MainWindow_Loaded(object? sender, RoutedEventArgs e) {
            try {
                this.InitKaraokeLyrics();
                this.UpdateKaraokeHostWidth();
                this.ResetKaraokeToStart();

                // Запуск караоке откладываем до завершения первичной проверки файлов на главной странице
                this.TryHookHomepageForKaraokeStart();
            }
            catch {
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
                catch {
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
                catch {
                    pad = 16.0;
                }
                double width = Math.Ceiling(max) + pad + 12; // padding + safety

                // Set a minimum and maximum to avoid extremes
                width = Math.Max(260, Math.Min(width, 800));
                this.KaraokeHost.Width = width;
            }
            catch {
            }
        }

        private void ResetKaraokeToStart() {
            this.karaokeLineIndex = 0;
            this.karaokeCharIndex = 0;
            this.SetKaraokeTexts(current: string.Empty, next: this.GetNextKaraokeLine());
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
            catch {
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
            catch {
            }
            if (!this.karaokeTimer.IsEnabled) {
                this.karaokeTimer.Start();
            }
            this.karaokeStarted = true;
        }

        private async void StartKaraokeWithInitialDelayAsync() {
            if (this.karaokeStarted) {
                return;
            }
            try {
                await Task.Delay(this.karaokeInitialDelayMs);
            }
            catch {
            }
            this.StartKaraoke();
        }

        private void TryHookHomepageForKaraokeStart() {
            try {
                if (this.ContentFrame?.Content is Pages.HomePage hp) {
                    // Если первичная проверка уже завершена — стартуем сразу с задержкой
                    if (hp.IsInitialVerificationCompleted) {
                        this.StartKaraokeWithInitialDelayAsync();
                        return;
                    }

                    // Иначе подпишемся на событие и запустим один раз
                    void Handler() {
                        try {
                            hp.InitialVerificationCompleted -= Handler; // отписка
                        }
                        catch {
                        }
                        this.StartKaraokeWithInitialDelayAsync();
                    }
                    hp.InitialVerificationCompleted += Handler;
                }
            }
            catch {
            }
        }

        private void PauseKaraoke() {
            this.karaokePaused = true;
            if (this.karaokeTimer.IsEnabled) {
                this.karaokeTimer.Stop();
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
            catch {
            }
            // Не запускаем таймер до явного старта караоке после первичной проверки
            if (this.karaokeStarted && !this.karaokeTimer.IsEnabled) {
                this.karaokeTimer.Start();
            }
        }

        private void KaraokeTimer_Tick(object? sender, EventArgs e) {
            if (this.karaokePaused || this.karaokeTransitionRunning) {
                return;
            }

            var line = this.GetCurrentKaraokeLine();

            if (this.karaokeCharIndex < line.Length) {
                // While typing, ensure current line is visible
                try {
                    this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, null);
                    if (this.KaraokeCurrentText.Opacity < 1.0) {
                        this.KaraokeCurrentText.Opacity = 1.0;
                    }
                }
                catch {
                }
                this.karaokeCharIndex++;
                var current = line.Substring(0, this.karaokeCharIndex);
                this.SetKaraokeTexts(current, this.GetNextKaraokeLine());
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
                await Task.Delay(this.karaokePauseAfterLineMs);

                // Кроссфейд (длительности берём из настроек)
                try {
                    var fadeOut = new DoubleAnimation { From = 1.0, To = 0.0, Duration = TimeSpan.FromMilliseconds(this.karaokeFadeOutMs) };
                    this.KaraokeCurrentText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                    var fadeIn = new DoubleAnimation { From = 0.0, To = 1.0, Duration = TimeSpan.FromMilliseconds(this.karaokeFadeInMs) };
                    this.KaraokeNextText.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                catch {
                }

                await Task.Delay(this.karaokeAfterTransitionDelayMs);

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
            }
            catch {
            }
            finally {
                this.karaokeTransitionRunning = false;
            }
        }
    }
}
