// <copyright file="App.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using System.Windows;

    using ChillHub.Core;
    using ChillHub.Core.Logging;

    public partial class App : Application {
        /// <inheritdoc/>
        protected override void OnStartup(StartupEventArgs e) {
            // Раннее применение темы
            _ = ConfigService.Current;

            // Глобальные обработчики исключений и лог
            try {
                // Подключаемся к консоли родителя, чтобы видеть вывод Console.WriteLine
                try {
                    AttachToParentConsole();
                }
                catch {
                }
                // Централизованный репортинг ошибок
                try { ChillHub.Core.ErrorReporter.InitGlobalHandlers(); } catch { }
                AppDomain.CurrentDomain.UnhandledException += (s, ex) => {
                    try {
                        AppendBootLog($"UnhandledException: {ex.ExceptionObject}");
                    }
                    catch {
                    }
                    try {
                        Console.Error.WriteLine($"[FATAL] UnhandledException: {ex.ExceptionObject}");
                    }
                    catch {
                    }
                    try {
                        Logger.Error("UnhandledException: " + ex.ExceptionObject);
                        if (ex.ExceptionObject is Exception real) {
                            ChillHub.Core.ErrorReporter.Report(real, "AppDomain.UnhandledException");
                        }
                    }
                    catch {
                    }
                    MessageBox.Show($"Необработанное исключение: {ex.ExceptionObject}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                };
                this.DispatcherUnhandledException += (s, ex) => {
                    try {
                        AppendBootLog($"DispatcherUnhandledException: {ex.Exception.Message}\r\n{ex.Exception}");
                    }
                    catch {
                    }
                    try {
                        Console.Error.WriteLine($"[ERROR] {ex.Exception}");
                    }
                    catch {
                    }
                    try {
                        Logger.Error(ex.Exception, "DispatcherUnhandledException");
                        ChillHub.Core.ErrorReporter.Report(ex.Exception, "DispatcherUnhandledException");
                    }
                    catch {
                    }
                    MessageBox.Show($"Ошибка: {ex.Exception.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    ex.Handled = true;
                };
                // Подписка на необработанные исключения задач
                try {
                    TaskScheduler.UnobservedTaskException += (s, ex) => {
                        try { Logger.Error(ex.Exception, "TaskScheduler.UnobservedTaskException"); } catch { }
                        try { ChillHub.Core.ErrorReporter.Report(ex.Exception, "TaskScheduler.UnobservedTaskException"); } catch { }
                    };
                }
                catch { }
            }
            catch {
            }

            // Разовая уборка каталога данных WebView2, оставшегося в папке установки
            // от версий без явного UserDataFolder. Делаем на старте, а не при первом
            // открытии новости: иначе у тех, кто новости не читает, он остаётся навсегда.
            try {
                ChillHub.Pages.NewsDetailPage.CleanupLegacyUserDataFolder();
            }
            catch (Exception ex) {
                try { Logger.Warn("Cleanup legacy WebView2 folder failed: " + ex.Message); } catch { }
            }

            base.OnStartup(e);
        }

        private void Application_Startup(object sender, StartupEventArgs e) {
            try {
                AppendBootLog("Starting Application_Startup");
            }
            catch {
            }
            try {
                Console.WriteLine("[BOOT] Starting Application_Startup");
            }
            catch {
            }

            // Шаг 1. Окно проверки/обновления лаунчера
            var prevMode = this.ShutdownMode;
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown; // не завершаем приложение при закрытии диалога

            var upd = new UpdateWindow();
            upd.SourceInitialized += (_, __) => {
                try {
                    Core.UI.AcrylicHelper.ApplyTitleBarTheme(upd, true);
                    TryApplyIcon(upd);
                }
                catch {
                }
            };
            try {
                AppendBootLog("Showing UpdateWindow");
            }
            catch {
            }
            try {
                Console.WriteLine("[BOOT] Showing UpdateWindow");
            }
            catch {
            }
            var ok = upd.ShowDialog() == true || upd.Proceed;
            try {
                AppendBootLog($"UpdateWindow result ok={ok}");
            }
            catch {
            }
            try {
                Console.WriteLine($"[BOOT] UpdateWindow result ok={ok}");
            }
            catch {
            }
            if (!ok) {
                // Пользователь закрыл окно или обновление обязательно
                try {
                    AppendBootLog("Shutting down after update dialog");
                }
                catch {
                }
                try {
                    Console.WriteLine("[BOOT] Shutting down after update dialog");
                }
                catch {
                }
                this.Shutdown();
                return;
            }

            // Шаг 2. Основное окно
            var mw = new MainWindow();
            mw.SourceInitialized += (_, __) => {
                try {
                    Core.UI.AcrylicHelper.ApplyTitleBarTheme(mw, true);
                    TryApplyIcon(mw);
                }
                catch {
                }
            };
            this.MainWindow = mw;
            this.ShutdownMode = ShutdownMode.OnMainWindowClose; // возвращаем обычный режим
            try {
                AppendBootLog("Showing MainWindow");
            }
            catch {
            }
            try {
                Console.WriteLine("[BOOT] Showing MainWindow");
            }
            catch {
            }
            mw.Show();
        }

        /// <summary>Потолок boot.log: при превышении оставляем только последнюю часть файла.</summary>
        private const long BootLogMaxBytes = 512 * 1024;

        /// <summary>Сколько байт хвоста сохраняем при обрезании boot.log.</summary>
        private const int BootLogKeepBytes = 128 * 1024;

        private static readonly object bootLogLock = new object();

        /// <summary>
        /// boot.log лежит там же, где остальные логи клиента (см. <see cref="Logger.LogDirectory"/>),
        /// а не в %TEMP%, который чистится системой.
        /// </summary>
        /// <inheritdoc/>
        protected override void OnExit(ExitEventArgs e) {
            // Снимаем статус в Discord, иначе он останется висеть после закрытия лаунчера.
            // Метод сам ничего не делает, если интеграция не настроена или Discord не запущен.
            try {
                ChillHub.Core.DiscordRichPresence.Shutdown();
            }
            catch (Exception ex) {
                try { Logger.Warn("Discord shutdown failed: " + ex.Message); } catch { }
            }

            // Останавливаем опрос режима технических работ (задача 25)
            try {
                ChillHub.Core.Maintenance.MaintenanceService.Stop();
            }
            catch (Exception ex) {
                // Logger.Write гасит собственные ошибки, дополнительная защита не нужна
                Logger.Warn("Maintenance poll stop failed: " + ex.Message);
            }

            base.OnExit(e);
        }

        private static string GetBootLogPath() {
            try {
                var dir = Logger.LogDirectory;
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "boot.log");
            }
            catch {
                return Path.Combine(Environment.CurrentDirectory, "boot.log");
            }
        }

        /// <summary>
        /// Дописывает строку в boot.log в формате «[ISO8601] текст» и не даёт файлу расти вечно.
        /// Никогда не бросает исключений.
        /// </summary>
        private static void AppendBootLog(string message) {
            try {
                var path = GetBootLogPath();
                var line = "[" + DateTime.Now.ToString("o") + "] " + message + "\r\n";
                var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                lock (bootLogLock) {
                    TrimBootLog(path, utf8);
                    File.AppendAllText(path, line, utf8);
                }
            }
            catch {
            }
        }

        /// <summary>Простая обрезка с начала: оставляем последние BootLogKeepBytes байт.</summary>
        private static void TrimBootLog(string path, System.Text.Encoding utf8) {
            try {
                if (!File.Exists(path)) {
                    return;
                }

                var len = new FileInfo(path).Length;
                if (len <= BootLogMaxBytes) {
                    return;
                }

                var bytes = File.ReadAllBytes(path);
                var keep = Math.Min(BootLogKeepBytes, bytes.Length);
                var tail = new byte[keep];
                Buffer.BlockCopy(bytes, bytes.Length - keep, tail, 0, keep);
                var text = utf8.GetString(tail);

                // Первая строка после обрезки почти наверняка неполная — отбрасываем её.
                var nl = text.IndexOf('\n');
                if (nl >= 0 && nl + 1 < text.Length) {
                    text = text.Substring(nl + 1);
                }

                File.WriteAllText(path, "[" + DateTime.Now.ToString("o") + "] INFO boot.log truncated\r\n" + text, utf8);
            }
            catch {
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
            catch {
            }
        }

        // ===== Console attach helpers =====
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int ATTACHPARENTPROCESS = -1;

        private static void AttachToParentConsole() {
            try {
                // Подключаемся к консоли родителя, если есть
                if (AttachConsole(ATTACHPARENTPROCESS)) {
                    try {
                        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                        Console.InputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    }
                    catch {
                    }
                }
            }
            catch {
            }
        }
    }
}
