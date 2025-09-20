// <copyright file="App.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
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
                AppDomain.CurrentDomain.UnhandledException += (s, ex) => {
                    try {
                        File.AppendAllText(GetBootLogPath(), $"[" + DateTime.Now.ToString("o") + $"] UnhandledException: {ex.ExceptionObject}\r\n");
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
                    }
                    catch {
                    }
                    MessageBox.Show($"Необработанное исключение: {ex.ExceptionObject}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                };
                this.DispatcherUnhandledException += (s, ex) => {
                    try {
                        File.AppendAllText(GetBootLogPath(), $"[" + DateTime.Now.ToString("o") + $"] DispatcherUnhandledException: {ex.Exception.Message}\r\n{ex.Exception}\r\n");
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
                    }
                    catch {
                    }
                    MessageBox.Show($"Ошибка: {ex.Exception.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    ex.Handled = true;
                };
            }
            catch {
            }
            base.OnStartup(e);
        }

        private void Application_Startup(object sender, StartupEventArgs e) {
            try {
                File.AppendAllText(GetBootLogPath(), "[" + DateTime.Now.ToString("o") + "] Starting Application_Startup\r\n");
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
                File.AppendAllText(GetBootLogPath(), "[" + DateTime.Now.ToString("o") + "] Showing UpdateWindow\r\n");
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
                File.AppendAllText(GetBootLogPath(), "[" + DateTime.Now.ToString("o") + $"] UpdateWindow result ok={ok}\r\n");
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
                    File.AppendAllText(GetBootLogPath(), "[" + DateTime.Now.ToString("o") + "] Shutting down after update dialog\r\n");
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
                File.AppendAllText(GetBootLogPath(), "[" + DateTime.Now.ToString("o") + "] Showing MainWindow\r\n");
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

        private static string GetBootLogPath() {
            try {
                var dir = Path.Combine(Path.GetTempPath(), "ChillHub");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "boot.log");
            }
            catch {
                return Path.Combine(Environment.CurrentDirectory, "boot.log");
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
