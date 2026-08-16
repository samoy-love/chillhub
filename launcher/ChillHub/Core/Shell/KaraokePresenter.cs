// <copyright file="KaraokePresenter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Animation;
    using System.Windows.Threading;

    /// <summary>
    /// Караоке в шапке окна: печатает строки песни (см. <see cref="KaraokeLyrics"/>) по
    /// символу, показывает следующую строку бледнее и держит мигающий курсор.
    /// <para>
    /// Счёт символов и времени — в <see cref="KaraokeTicker"/>, здесь только связь с
    /// экраном: таймер, тексты, курсор, пауза при свёрнутом или неактивном окне. Раньше
    /// всё это (~350 строк) жило прямо в MainWindow вперемешку с треем, самообновлением
    /// и навигацией — по одной ошибке в тике караоке приходилось искать среди них.
    /// </para>
    /// <para>
    /// Любой сбой караоке — не повод ни ронять окно, ни заливать лог: каждая точка отказа
    /// логируется один раз за сессию (<see cref="LogFailure"/>), дальше — молча.
    /// </para>
    /// </summary>
    internal sealed class KaraokePresenter {
        private static readonly HashSet<string> LoggedContexts = new(StringComparer.Ordinal);

        private readonly KaraokeConfig config;
        private readonly KaraokeTicker ticker;
        private readonly FrameworkElement host;
        private readonly TextBlock current;
        private readonly TextBlock next;
        private readonly UIElement caret;
        private readonly DispatcherTimer timer;

        private bool paused;
        private bool transitionRunning;

        /// <summary>Initializes a new instance of the <see cref="KaraokePresenter"/> class.</summary>
        /// <param name="host">Контейнер строки — ему подбирается ширина под самую длинную строку.</param>
        /// <param name="current">Печатаемая строка.</param>
        /// <param name="next">Следующая строка, бледнее.</param>
        /// <param name="caret">Курсор после печатаемого текста.</param>
        /// <param name="config">Скорости и паузы; null — по умолчанию.</param>
        internal KaraokePresenter(FrameworkElement host, TextBlock current, TextBlock next, UIElement caret, KaraokeConfig? config = null) {
            this.host = host;
            this.current = current;
            this.next = next;
            this.caret = caret;
            this.config = config ?? new KaraokeConfig();
            this.ticker = new KaraokeTicker(this.config);

            // Render-приоритет и отсчёт по времени, а не по тикам: под нагрузкой тики
            // редеют, а скорость печати должна оставаться прежней.
            this.timer = new DispatcherTimer(DispatcherPriority.Render) {
                Interval = TimeSpan.FromMilliseconds(this.config.TimerTickMs),
            };
            this.timer.Tick += this.OnTick;
        }

        /// <summary>Строки песни — для тестов и подбора ширины.</summary>
        internal IReadOnlyList<string> Lines => this.ticker.Lines;

        /// <summary>Загружает текст, подбирает ширину и начинает печать с первой строки.</summary>
        /// <param name="lyrics">Текст песни построчно.</param>
        internal void Start(string lyrics) {
            try {
                this.ticker.SetLyrics(lyrics);
                this.FitHostWidth();
                this.ticker.ResetToStart(DateTime.UtcNow);
                this.SetTexts(string.Empty, this.ticker.NextLine);
                this.StartCaretBlink();
                this.Resume();
            }
            catch (Exception ex) {
                LogFailure("старт", ex);
            }
        }

        /// <summary>Останавливает печать (окно свёрнуто или неактивно) — время паузы не засчитывается.</summary>
        internal void Pause() {
            this.paused = true;
            this.timer.Stop();
            this.ticker.BeginPause(DateTime.UtcNow);
        }

        /// <summary>Возобновляет печать с того же символа, не «догоняя» пропущенное.</summary>
        internal void Resume() {
            this.paused = false;
            try {
                this.ticker.EndPause(DateTime.UtcNow);
                this.ResetOpacity();
                this.ticker.BackdateForFirstChar(DateTime.UtcNow);
            }
            catch (Exception ex) {
                LogFailure("возобновление", ex);
            }

            if (!this.timer.IsEnabled) {
                this.timer.Start();
            }

            this.OnTick(this, EventArgs.Empty);
        }

        private static void LogFailure(string context, Exception ex) {
            lock (LoggedContexts) {
                if (!LoggedContexts.Add(context)) {
                    return;
                }
            }

            Logging.Logger.Warn($"Караоке в шапке, {context}: {ex.Message} (повторы не логируются)");
        }

        /// <summary>
        /// Ширина контейнера — под самую длинную строку песни, чтобы шапка не дышала при
        /// смене строк и текст не переносился. Диапазон 260..800 — см. <see cref="KaraokeTicker.HostWidth"/>.
        /// </summary>
        private void FitHostWidth() {
            try {
                if (this.ticker.Lines.Length == 0) {
                    return;
                }

                double pixelsPerDip = 1.0;
                try {
                    pixelsPerDip = VisualTreeHelper.GetDpi(this.host).PixelsPerDip;
                }
                catch (Exception ex) {
                    LogFailure("определение DPI, берём 1.0", ex);
                }

                var typeface = new Typeface(this.current.FontFamily, this.current.FontStyle, this.current.FontWeight, this.current.FontStretch);
                double max = 0.0;
                foreach (var line in this.ticker.Lines) {
                    var ft = new FormattedText(
                        line ?? string.Empty,
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        this.current.FontSize,
                        Brushes.Transparent,
                        pixelsPerDip);
                    max = Math.Max(max, ft.WidthIncludingTrailingWhitespace);
                }

                // Слева от текста — значок и его отступ, справа — курсор: они входят в ширину контейнера
                var extras = this.host.Padding().Left + this.host.Padding().Right + 40;
                this.host.Width = KaraokeTicker.HostWidth(max, extras);
            }
            catch (Exception ex) {
                LogFailure("подбор ширины контейнера", ex);
            }
        }

        private void SetTexts(string typed, string upcoming) {
            this.current.Text = typed;
            this.next.Text = upcoming;
        }

        private void ResetOpacity() {
            this.current.BeginAnimation(UIElement.OpacityProperty, null);
            this.current.Opacity = 1.0;
        }

        /// <summary>Курсор мигает сам по себе, независимо от печати, — как в терминале.</summary>
        private void StartCaretBlink() {
            var blink = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(520)) {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            this.caret.BeginAnimation(UIElement.OpacityProperty, blink);
        }

        private void OnTick(object? sender, EventArgs e) {
            if (this.paused || this.transitionRunning) {
                return;
            }

            try {
                var add = this.ticker.PlanAdvance(DateTime.UtcNow);
                if (add > 0) {
                    this.SetTexts(this.ticker.Type(add), this.ticker.NextLine);
                    this.ticker.CommitProgress(add);
                }
            }
            catch (Exception ex) {
                LogFailure("тик печати", ex);
                this.ticker.ResetProgressTo(DateTime.UtcNow);
            }

            if (this.ticker.LineComplete) {
                _ = this.MoveToNextLineAsync();
            }
        }

        /// <summary>
        /// Строка дописана: пауза (короткая после строки, длинная на пустой — между куплетами),
        /// плавное угасание, следующая строка становится текущей и печатается с нуля. Пока идёт
        /// переход, тики ничего не делают.
        /// </summary>
        private async Task MoveToNextLineAsync() {
            if (this.transitionRunning) {
                return;
            }

            this.transitionRunning = true;
            try {
                // Пустая строка — граница куплета: стоит долго, как проигрыш в записи
                await Task.Delay(this.ticker.CurrentLine.Length == 0 ? this.config.PauseAfterEmptyLineMs : this.config.PauseAfterLineMs);
                if (this.paused) {
                    return;
                }

                var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(this.config.FadeOutMs));
                this.current.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                await Task.Delay(this.config.FadeOutMs);

                this.ticker.MoveToNextLine(DateTime.UtcNow);
                this.ResetOpacity();
                this.SetTexts(string.Empty, this.ticker.NextLine);
            }
            catch (Exception ex) {
                LogFailure("переход к следующей строке", ex);
            }
            finally {
                this.transitionRunning = false;
            }
        }
    }

    /// <summary>Отступы контейнера, если он их имеет (Border/Control), иначе нули.</summary>
    internal static class KaraokeHostExtensions {
        internal static Thickness Padding(this FrameworkElement host) => host switch {
            Border b => b.Padding,
            Control c => c.Padding,
            _ => default,
        };
    }
}
