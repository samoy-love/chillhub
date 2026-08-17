// <copyright file="KaraokePresenterTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Threading;

    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Связка караоке с экраном: таймер, тексты, переходы между строками. Проверяется на
    /// настоящем диспетчере в отдельном STA-потоке — без окна, но с теми же событиями,
    /// которые окно шлёт презентеру.
    /// </summary>
    public class KaraokePresenterTests {
        private const string FirstLine = "Моя игра, 98, Баста здесь 2006, и строка эта длинная";
        private const string SecondLine = "Она мне принадлежит";
        private const string Lyrics = FirstLine + "\n\n" + SecondLine;

        /// <summary>
        /// Окно активируется и становится видимым раньше, чем загружается, поэтому
        /// <c>Resume</c> прилетает до <c>Start</c>. Раньше пустой текст в этот момент считался
        /// «дописанным», начинался переход к следующей строке, и после старта он перескакивал
        /// первую строку песни — караоке начиналось с пустой строки.
        /// </summary>
        [Fact]
        public void ВозобновлениеДоСтартаНеПерескакиваетПервуюСтроку() {
            OnUi(async () => {
                var current = new TextBlock();
                var next = new TextBlock();
                var config = new KaraokeConfig {
                    CharIntervalMs = 50,
                    PauseAfterLineMs = 20,
                    PauseAfterEmptyLineMs = 20,
                    FadeOutMs = 10,
                };
                var presenter = new KaraokePresenter(new Border(), current, next, new Border(), config);

                presenter.Resume();
                presenter.Pause();
                presenter.Resume();

                presenter.Start(Lyrics);
                await Task.Delay(400);

                Assert.NotEmpty(current.Text);
                Assert.StartsWith(current.Text, FirstLine, StringComparison.Ordinal);
                Assert.Equal(string.Empty, next.Text);
            });
        }

        /// <summary>
        /// Повторный старт во время перехода между строками: старый переход, дождавшись
        /// своей паузы, не должен сдвигать новую песню на строку вперёд.
        /// </summary>
        [Fact]
        public void ПереходСтарогоЗапускаНеСдвигаетНовыйЗапуск() {
            OnUi(async () => {
                var current = new TextBlock();
                var next = new TextBlock();
                var config = new KaraokeConfig {
                    CharIntervalMs = 5,
                    PauseAfterLineMs = 200,
                    PauseAfterEmptyLineMs = 200,
                    FadeOutMs = 10,
                };
                var presenter = new KaraokePresenter(new Border(), current, next, new Border(), config);

                // Короткая строка дописывается почти сразу, и презентер уходит в паузу перед переходом
                presenter.Start("ab\nвторая");
                await Task.Delay(100);
                Assert.Equal("ab", current.Text);

                presenter.Start(Lyrics);
                await Task.Delay(400);

                Assert.StartsWith(current.Text, FirstLine, StringComparison.Ordinal);
                Assert.Equal(string.Empty, next.Text);
            });
        }

        /// <summary>Дописанная строка после паузы и угасания уступает место следующей, а та печатается с нуля.</summary>
        [Fact]
        public void ДописаннаяСтрокаСменяетсяСледующей() {
            OnUi(async () => {
                var current = new TextBlock();
                var next = new TextBlock();
                var config = new KaraokeConfig {
                    CharIntervalMs = 5,
                    PauseAfterLineMs = 30,
                    PauseAfterEmptyLineMs = 30,
                    FadeOutMs = 10,
                };
                var presenter = new KaraokePresenter(new Border(), current, next, new Border(), config);

                presenter.Start("ab\n" + FirstLine + "\n" + SecondLine);
                await Task.Delay(400);

                Assert.NotEmpty(current.Text);
                Assert.StartsWith(current.Text, FirstLine, StringComparison.Ordinal);
                Assert.Equal(SecondLine, next.Text);
                Assert.Equal(1.0, current.Opacity);
            });
        }

        /// <summary>Пауза после старта останавливает печать, возобновление продолжает её с того же места.</summary>
        [Fact]
        public void ПаузаОстанавливаетПечать() {
            OnUi(async () => {
                var current = new TextBlock();
                var presenter = new KaraokePresenter(new Border(), current, new TextBlock(), new Border(), new KaraokeConfig { CharIntervalMs = 20 });

                presenter.Start(Lyrics);
                await Task.Delay(150);
                presenter.Pause();
                var typed = current.Text;
                Assert.NotEmpty(typed);

                await Task.Delay(150);
                Assert.Equal(typed, current.Text);

                presenter.Resume();
                await Task.Delay(150);
                Assert.True(current.Text.Length > typed.Length, "после возобновления печать должна продолжиться");
            });
        }

        /// <summary>
        /// Пауза гасит и мигание курсора. Раньше она останавливала только таймер печати,
        /// а бесконечная анимация курсора продолжала тикать на UI-потоке — спрятанный
        /// в трей лаунчер мигал курсором, которого никто не видит, круглые сутки.
        /// </summary>
        [Fact]
        public void ПаузаГаситМиганиеКурсора() {
            OnUi(async () => {
                var caret = new Border();
                var presenter = new KaraokePresenter(new Border(), new TextBlock(), new TextBlock(), caret, new KaraokeConfig { CharIntervalMs = 20 });

                presenter.Start(Lyrics);
                await Task.Delay(50);
                Assert.True(caret.HasAnimatedProperties, "у идущего караоке курсор должен мигать");

                presenter.Pause();
                Assert.False(caret.HasAnimatedProperties, "на паузе анимация курсора должна быть снята");
                Assert.Equal(1.0, caret.Opacity);

                presenter.Resume();
                Assert.True(caret.HasAnimatedProperties, "после возобновления курсор должен мигать снова");
            });
        }

        private static void OnUi(Func<Task> body) {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() => {
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = dispatcher.InvokeAsync(async () => {
                    try {
                        await body();
                    }
                    catch (Exception ex) {
                        failure = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally {
                        dispatcher.InvokeShutdown();
                    }
                });

                Dispatcher.Run();
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "тест интерфейса не уложился в отведённое время");
            failure?.Throw();
        }
    }
}
