// <copyright file="UiThreadSupport.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Diagnostics;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Controls;
    using System.Windows.Interop;
    using System.Windows.Threading;

    using Xunit;

    /// <summary>
    /// Прогон тела теста на выделенном STA-потоке с работающим диспетчером.
    /// <para>
    /// WPF-элементы создаются только на STA-потоке, а всё, что уходит в интерфейс через
    /// <c>Dispatcher.InvokeAsync</c> или анимацию, без запущенного цикла диспетчера просто
    /// зависнет. Ни <c>Application</c>, ни окна при этом не поднимаются: видимое окно
    /// в прогоне тестов — это заблокированный CI и мигающие скриншоты.
    /// </para>
    /// </summary>
    internal static class UiThread {
        /// <summary>Выполняет тело на UI-потоке и возвращает управление после его завершения.</summary>
        internal static void Run(Func<Task> body) {
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

        /// <summary>Синхронное тело: та же обёртка для тестов, которым асинхронность не нужна.</summary>
        internal static void Run(Action body) => Run(() => {
            body();
            return Task.CompletedTask;
        });

        /// <summary>
        /// Даёт диспетчеру прокрутить отложенную работу — отрисовку, тики анимаций
        /// и продолжения задач, поставленные в очередь.
        /// </summary>
        internal static async Task Settle(int rounds = 20) {
            for (var i = 0; i < rounds; i++) {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Yield();
            }
        }

        /// <summary>
        /// Крутит очередь диспетчера, пока условие не станет истинным.
        /// Ожиданием по таймеру пользоваться нельзя: на загруженной машине оно мигает.
        /// </summary>
        internal static async Task WaitUntil(Func<bool> condition, string what) {
            var sw = Stopwatch.StartNew();
            while (!condition()) {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"не дождались: {what}");
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Корень визуального дерева для тестов, которым нужна работающая анимация.
    /// <para>
    /// Проверено опытом: на голом STA-потоке с диспетчером анимации WPF не тикают вовсе —
    /// система времени заводится только при наличии цели отрисовки, и <c>Completed</c>
    /// не наступает никогда. Поэтому здесь создаётся настоящий HWND — но как WS_POPUP,
    /// без WS_VISIBLE и без единого вызова показа: окна на экране не появляется,
    /// прогон ничего не мигает и ничего не перехватывает у пользователя.
    /// </para>
    /// </summary>
    internal sealed class OffscreenVisualRoot : IDisposable {
        /// <summary>WS_POPUP: окно без рамки и, главное, без WS_VISIBLE.</summary>
        private const int WsPopup = unchecked((int)0x80000000);

        private readonly HwndSource source;

        internal OffscreenVisualRoot() {
            this.source = new HwndSource(new HwndSourceParameters("chillhub-tests") {
                Width = 320,
                Height = 240,
                WindowStyle = WsPopup,
            });

            this.Root = new Grid { Width = 320, Height = 240 };
            this.source.RootVisual = this.Root;
        }

        /// <summary>Панель, в которую тест кладёт проверяемые элементы.</summary>
        internal Grid Root { get; }

        /// <summary>Кладёт элемент в дерево — без этого анимация на нём не пойдёт.</summary>
        internal T Add<T>(T element)
            where T : System.Windows.UIElement {
            this.Root.Children.Add(element);
            return element;
        }

        public void Dispose() => this.source.Dispose();
    }
}
