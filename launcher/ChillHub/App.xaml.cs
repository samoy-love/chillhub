// <copyright file="App.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using System.Windows;

    using ChillHub.Core.Logging;
    using ChillHub.Core.Shell;

    public partial class App : Application {
        /// <inheritdoc/>
        protected override void OnStartup(StartupEventArgs e) {
            // Запрос ярлыка кладётся на диск ДО замка единственного экземпляра. Замок этот
            // запуск может и не пустить (лаунчер уже работает) — тогда он лишь сигналит
            // живой копии «покажи окно», а сигнал ничего не переносит, кроме самого факта.
            // Игру, которую просит ярлык, живая копия забирает именно из этого файла.
            Core.Shell.ShortcutRequestFile.Write(Core.Shell.ShortcutTarget.Parse(e?.Args));

            // Порядок шагов — в StartupSequence: он и есть поведение, и проверяется тестом.
            // Здесь остаётся только то, что без живого Application не работает.
            var startup = new StartupSequence {
                InstallGlobalHandlers = this.InstallGlobalHandlers,
                CleanupLegacyWebViewFolder = ChillHub.Pages.NewsDetailPage.CleanupLegacyUserDataFolder,
            };

            if (!startup.Run()) {
                this.Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        /// <inheritdoc/>
        protected override void OnExit(ExitEventArgs e) {
            new ShutdownSequence().Run();
            base.OnExit(e);
        }

        /// <summary>
        /// Глобальные обработчики исключений и лог. Остаются в App: подписка на
        /// <see cref="Application.DispatcherUnhandledException"/> возможна только у самого приложения.
        /// </summary>
        private void InstallGlobalHandlers() {
            // Подключаемся к консоли родителя, чтобы видеть вывод Console.WriteLine
            BootConsole.AttachToParent();

            // Централизованный репортинг ошибок
            ChillHub.Core.ErrorReporter.InitGlobalHandlers();

            AppDomain.CurrentDomain.UnhandledException += (s, ex) => {
                BootLog.Append($"UnhandledException: {ex.ExceptionObject}");
                BootConsole.ErrorLine($"[FATAL] UnhandledException: {ex.ExceptionObject}");
                Logger.Error("UnhandledException: " + ex.ExceptionObject);
                if (ex.ExceptionObject is Exception real) {
                    ChillHub.Core.ErrorReporter.Report(real, "AppDomain.UnhandledException");
                }

                // Пользователю — суть и куда смотреть. Стектрейс уже в логе и в
                // авто-отчёте: в окне он нечитаем и содержит пути с именем пользователя.
                MessageBox.Show(
                    "Произошла непредвиденная ошибка, лаунчер будет закрыт.\n\n"
                    + "Подробности записаны в журнал (кнопка «Открыть логи» на странице игры).",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };
            this.DispatcherUnhandledException += (s, ex) => {
                BootLog.Append($"DispatcherUnhandledException: {ex.Exception.Message}\r\n{ex.Exception}");
                BootConsole.ErrorLine($"[ERROR] {ex.Exception}");

                // Logger.Error(Exception, ...) сам отправляет отчёт — второй вызов не нужен
                Logger.Error(ex.Exception, "DispatcherUnhandledException");
                MessageBox.Show(
                    "Произошла ошибка, но лаунчер продолжит работу.\n\n"
                    + "Подробности записаны в журнал.",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ex.Handled = true;
            };

            // Подписка на необработанные исключения задач
            TaskScheduler.UnobservedTaskException += (s, ex) =>
                Logger.Error(ex.Exception, "TaskScheduler.UnobservedTaskException");
        }

        /// <summary>
        /// Точка входа в запуск. Сами шаги — в <see cref="RunStartupAsync"/>, а здесь
        /// только страховка: метод помечен async void, и без неё любое исключение после
        /// первого await улетало в общий обработчик диспетчера. Тот обещает «лаунчер
        /// продолжит работу» и гасит исключение — но продолжать было уже нечем.
        /// </summary>
        private async void Application_Startup(object sender, StartupEventArgs e)
            => await StartupGuard.RunAsync(
                this.RunStartupAsync,
                () => this.MainWindow?.IsVisible == true,
                ReportStartupFailure,
                this.Shutdown);

        /// <summary>
        /// Пользователю — что случилось и что делать. Стектрейс уже в журнале: в окне он
        /// нечитаем и содержит пути с именем пользователя.
        /// </summary>
        /// <param name="ex">Что помешало запуску.</param>
        private static void ReportStartupFailure(Exception ex) {
            BootLog.Append($"Application_Startup failed: {ex}");
            BootConsole.ErrorLine($"[FATAL] Application_Startup: {ex}");
            Logger.Error(ex, "Application_Startup");
            MessageBox.Show(
                "Не удалось открыть лаунчер.\n\n"
                + "Попробуйте запустить его ещё раз. Если не поможет — перезагрузите компьютер.\n\n"
                + "Подробности записаны в журнал.",
                "Ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private async Task RunStartupAsync() {
            BootConsole.Trace("Starting Application_Startup");

            // Шаг 1. Проверка/обновление лаунчера. Окно показываем, только если
            // есть что показать — версия актуальна и рассказывать нечего, экран
            // самообновления только тормозил бы каждый обычный запуск.
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown; // не завершаем приложение при закрытии диалога

            var precheck = await UpdateWindow.PrecheckAsync();
            bool ok;
            if (!precheck.NeedsWindow) {
                BootConsole.Trace("Skipping UpdateWindow: launcher is up to date");
                ok = true;
            }
            else {
                var upd = new UpdateWindow(precheck);
                upd.SourceInitialized += (_, __) => ApplyWindowChrome(upd);
                BootConsole.Trace("Showing UpdateWindow");
                ok = StartupSequence.ShouldShowMainWindow(upd.ShowDialog(), upd.Proceed);
                BootConsole.Trace($"UpdateWindow result ok={ok}");
            }

            if (!ok) {
                // Пользователь закрыл окно или обновление обязательно
                BootConsole.Trace("Shutting down after update dialog");
                this.Shutdown();
                return;
            }

            // Шаг 2. Основное окно
            var mw = new MainWindow();
            mw.SourceInitialized += (_, __) => ApplyWindowChrome(mw);
            this.MainWindow = mw;
            this.ShutdownMode = ShutdownMode.OnMainWindowClose; // возвращаем обычный режим

            // Повторный запуск лаунчера (ярлык, вторая копия), пока этот экземпляр уже жив,
            // сигналит сюда через именованное событие (см. SingleInstance) — без этого второй
            // клик по ярлыку, пока лаунчер свёрнут в трей, никак не поднимал его окно.
            Core.SingleInstance.StartListeningForShowRequests(() =>
                mw.Dispatcher.Invoke(() => {
                    mw.ShowAndActivate();

                    // Повторный запуск мог прийти с ярлыка игры — тогда мало поднять окно,
                    // надо открыть ту игру, на которую нажали.
                    mw.HandleShortcutRequest(Core.Shell.ShortcutRequestFile.Consume());
                }));

            // Окно всегда открывается развёрнутым на экран, а не в трей — WindowState тут
            // не персистится между запусками, но выставляем явно: значение по умолчанию из
            // XAML не должно зависеть от того, как ОС запустила процесс (например, ярлык
            // с «Запуск: свёрнутым»).
            mw.WindowState = WindowState.Normal;
            BootConsole.Trace("Showing MainWindow");
            mw.Show();
            mw.Activate();

            // Запуск с ярлыка: игру просит эта же копия лаунчера, и запрос она забирает
            // из того же файла, что и живая копия, — путь один на оба случая.
            mw.HandleShortcutRequest(Core.Shell.ShortcutRequestFile.Consume());
        }

        /// <summary>Оформление заголовка окна и иконка — украшение, окно должно открыться и без них.</summary>
        private static void ApplyWindowChrome(Window window) {
            try {
                Core.UI.AcrylicHelper.ApplyTitleBarTheme(window, true);
                TryApplyIcon(window);
            }
            catch (Exception ex) {
                Logger.Warn($"Оформление окна '{window?.GetType().Name}' не применено: {ex.Message}");
            }
        }

        private static void TryApplyIcon(Window w) {
            try {
                string[] candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
                    Path.Combine(Environment.CurrentDirectory, "Assets", "app.ico"),
                };
                foreach (var p in candidates) {
                    if (File.Exists(p)) {
                        var uri = new Uri(p, UriKind.Absolute);
                        var icon = new System.Windows.Media.Imaging.BitmapImage(uri);
                        w.Icon = icon;
                        break;
                    }
                }
            }
            catch (Exception ex) {
                // Без иконки окно откроется с системной — не повод падать
                Logger.Warn("Не удалось применить иконку окна: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Страховка запуска: у аварии на старте должен быть исход, видимый пользователю.
    /// <para>
    /// ПРОЦЕСС БЕЗ ОКНА — ХУЖЕ ЧЕСТНОГО ВЫХОДА. Шаги запуска идут в async void, и до
    /// появления окна режим завершения стоит «только явно»: исключение (самый близкий
    /// источник — значок в трее, который не создаётся при перезапуске explorer.exe)
    /// оставляло живой ChillHub.exe без окна и без значка. Снять его можно было только
    /// диспетчером задач, а всё это время он держал замок единственного экземпляра, и
    /// повторный запуск лаунчера молча не стартовал.
    /// </para>
    /// <para>
    /// Окно уже на экране — авария относится к тому, что после него (например, к
    /// открытию игры по ярлыку), и гасить лаунчер незачем: работать есть с чем.
    /// </para>
    /// </summary>
    internal static class StartupGuard {
        /// <summary>Выполняет шаги запуска, не давая аварии оставить процесс без исхода.</summary>
        /// <param name="steps">Сами шаги запуска.</param>
        /// <param name="windowIsUp">Главное окно уже показано пользователю.</param>
        /// <param name="report">Рассказать пользователю и журналу, что случилось.</param>
        /// <param name="shutdown">Завершить приложение (и отпустить замок единственного экземпляра).</param>
        /// <returns>Задача, завершающаяся вместе с запуском.</returns>
        internal static async Task RunAsync(
            Func<Task> steps, Func<bool> windowIsUp, Action<Exception> report, Action shutdown) {
            try {
                await steps().ConfigureAwait(true);
            }
            catch (Exception ex) {
                try {
                    report(ex);
                }
                catch (Exception reportFailed) {
                    // Даже рассказать не вышло — тем более нельзя остаться висеть без окна.
                    BootLog.Append($"StartupGuard: сообщить об аварии не удалось: {reportFailed}");
                }

                if (!windowIsUp()) {
                    shutdown();
                }
            }
        }
    }
}
