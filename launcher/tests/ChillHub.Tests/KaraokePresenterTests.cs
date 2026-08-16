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
