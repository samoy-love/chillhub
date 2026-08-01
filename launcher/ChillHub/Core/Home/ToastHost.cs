// <copyright file="ToastHost.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Animation;

    /// <summary>
    /// Ненавязчивое всплывающее уведомление в углу окна: показ с анимацией, автоскрытие,
    /// перебивание предыдущего сообщения новым. Работает поверх пары элементов из XAML
    /// (контейнер + текстовый блок), собственной разметки не создаёт.
    /// </summary>
    internal sealed class ToastHost {
        private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan OverwriteOutDuration = TimeSpan.FromMilliseconds(140);
        private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(220);

        private readonly FrameworkElement host;
        private readonly TextBlock text;

        private CancellationTokenSource? cts;
        private bool initialized;

        internal ToastHost(FrameworkElement host, TextBlock text) {
            this.host = host;
            this.text = text;
        }

        /// <summary>Показывает сообщение; предыдущее, если есть, аккуратно убирается.</summary>
        internal void Show(string message, TimeSpan? duration = null) {
            this.EnsureTransform();
            var dur = duration ?? DefaultDuration;

            // Отменяем предыдущую анимацию: новое сообщение перебивает старое.
            // Освобождает источник его собственный показ (в finally RunAsync) — иначе
            // отменённая анимация обращалась бы к уже уничтоженному объекту.
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref this.cts, cts);
            try {
                previous?.Cancel();
            }
            catch (ObjectDisposedException) {
                // Предыдущий показ уже завершился и освободил свой источник
            }

            _ = this.RunAsync(message, dur, cts);
        }

        private async Task RunAsync(string message, TimeSpan dur, CancellationTokenSource cts) {
            var ct = cts.Token;
            try {
                // Если тост сейчас виден — быстро убираем его перед показом нового текста
                if (this.host.Visibility == Visibility.Visible && this.host.Opacity > 0.1) {
                    await this.AnimateAsync(fadeIn: false, OverwriteOutDuration, ct).ConfigureAwait(true);
                }

                this.text.Text = message;
                this.host.Visibility = Visibility.Visible;
                await this.AnimateAsync(fadeIn: true, FadeInDuration, ct).ConfigureAwait(true);

                await Task.Delay(dur, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested) {
                    return;
                }

                await this.AnimateAsync(fadeIn: false, FadeOutDuration, ct).ConfigureAwait(true);
                if (!ct.IsCancellationRequested) {
                    this.host.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException) {
                // Обычный сценарий: тост перебит следующим сообщением — молча выходим.
            }
            catch (Exception ex) {
                // Уведомление второстепенно: сбой анимации не должен всплывать в UI-поток.
                Logging.Logger.Warn($"ToastHost: показ уведомления прерван: {ex.Message}");
            }
            finally {
                Interlocked.CompareExchange(ref this.cts, null, cts);
                cts.Dispose();
            }
        }

        private void EnsureTransform() {
            if (this.initialized) {
                return;
            }

            if (this.host.RenderTransform is not TranslateTransform) {
                this.host.RenderTransform = new TranslateTransform(0, 20);
            }

            this.host.Opacity = 0;
            this.host.Visibility = Visibility.Collapsed;
            this.initialized = true;
        }

        private async Task AnimateAsync(bool fadeIn, TimeSpan duration, CancellationToken ct) {
            var tcs = new TaskCompletionSource<bool>();

            // Регистрацию на токене обязательно освобождаем: иначе делегат каждой анимации
            // остаётся висеть на источнике до его отмены, а тостов за сессию сотни.
            var registration = default(CancellationTokenRegistration);
            try {
                if (this.host.RenderTransform is not TranslateTransform translate) {
                    translate = new TranslateTransform(0, 0);
                    this.host.RenderTransform = translate;
                }

                var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
                var animOpacity = new DoubleAnimation {
                    From = fadeIn ? (double?)0.0 : this.host.Opacity,
                    To = fadeIn ? 1.0 : 0.0,
                    Duration = new Duration(duration),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop,
                };
                var animY = new DoubleAnimation {
                    From = fadeIn ? (double?)20.0 : translate.Y,
                    To = fadeIn ? 0.0 : 10.0,
                    Duration = new Duration(duration),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop,
                };

                int completed = 0;
                void CheckDone() {
                    if (++completed >= 2) {
                        tcs.TrySetResult(true);
                    }
                }

                animOpacity.Completed += (s, e) => {
                    this.host.Opacity = fadeIn ? 1.0 : 0.0;
                    if (ct.IsCancellationRequested) {
                        tcs.TrySetCanceled(ct);
                    }
                    else {
                        CheckDone();
                    }
                };
                animY.Completed += (s, e) => {
                    translate.Y = fadeIn ? 0.0 : 10.0;
                    if (ct.IsCancellationRequested) {
                        tcs.TrySetCanceled(ct);
                    }
                    else {
                        CheckDone();
                    }
                };

                this.host.BeginAnimation(UIElement.OpacityProperty, animOpacity);
                translate.BeginAnimation(TranslateTransform.YProperty, animY);

                if (ct.CanBeCanceled) {
                    registration = ct.Register(() => {
                        this.host.BeginAnimation(UIElement.OpacityProperty, null);
                        translate.BeginAnimation(TranslateTransform.YProperty, null);
                        tcs.TrySetCanceled();
                    });
                }
            }
            catch (Exception ex) {
                // Не удалось анимировать — показываем/прячем мгновенно, текст пользователь всё равно увидит.
                Logging.Logger.Warn($"ToastHost.Animate: {ex.Message}");
                this.host.Opacity = fadeIn ? 1.0 : 0.0;
                if (!fadeIn) {
                    this.host.Visibility = Visibility.Collapsed;
                }

                tcs.TrySetException(ex);
            }

            try {
                await tcs.Task.ConfigureAwait(true);
            }
            finally {
                registration.Dispose();
            }
        }
    }
}
