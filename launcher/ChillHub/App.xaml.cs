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

            // Глобальные обработчики исключений и лог.
            // AppendBootLog, Logger и ErrorReporter гасят собственные ошибки, поэтому
            // оборачивать каждый их вызов в try не нужно — раньше это давало десяток пустых catch.
            try {
                // Подключаемся к консоли родителя, чтобы видеть вывод Console.WriteLine
                AttachToParentConsole();

                // Централизованный репортинг ошибок
                ChillHub.Core.ErrorReporter.InitGlobalHandlers();

                AppDomain.CurrentDomain.UnhandledException += (s, ex) => {
                    AppendBootLog($"UnhandledException: {ex.ExceptionObject}");
                    ConsoleErrorLine($"[FATAL] UnhandledException: {ex.ExceptionObject}");
                    Logger.Error("UnhandledException: " + ex.ExceptionObject);
                    if (ex.ExceptionObject is Exception real) {
                        ChillHub.Core.ErrorReporter.Report(real, "AppDomain.UnhandledException");
                    }

                    MessageBox.Show($"Необработанное исключение: {ex.ExceptionObject}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                };
                this.DispatcherUnhandledException += (s, ex) => {
                    AppendBootLog($"DispatcherUnhandledException: {ex.Exception.Message}\r\n{ex.Exception}");
                    ConsoleErrorLine($"[ERROR] {ex.Exception}");

                    // Logger.Error(Exception, ...) сам отправляет отчёт — второй вызов не нужен
                    Logger.Error(ex.Exception, "DispatcherUnhandledException");
                    MessageBox.Show($"Ошибка: {ex.Exception.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    ex.Handled = true;
                };

                // Подписка на необработанные исключения задач
                TaskScheduler.UnobservedTaskException += (s, ex) =>
                    Logger.Error(ex.Exception, "TaskScheduler.UnobservedTaskException");
            }
            catch (Exception ex) {
                // Без обработчиков лаунчер работоспособен, просто ошибки не попадут в отчёты.
                // Пишем в boot.log: обычный лог на этом этапе может быть ещё недоступен.
                AppendBootLog("Не удалось установить глобальные обработчики ошибок: " + ex);
                Logger.Warn("Не удалось установить глобальные обработчики ошибок: " + ex.Message);
            }

            // Разовая уборка каталога данных WebView2, оставшегося в папке установки
            // от версий без явного UserDataFolder. Делаем на старте, а не при первом
            // открытии новости: иначе у тех, кто новости не читает, он остаётся навсегда.
            try {
                ChillHub.Pages.NewsDetailPage.CleanupLegacyUserDataFolder();
            }
            catch (Exception ex) {
                Logger.Warn("Cleanup legacy WebView2 folder failed: " + ex.Message);
            }

            // Обезличенная статистика: «выстрелил и забыл», сеть не ждём.
            try {
                ChillHub.Core.Metrics.MetricsService.LauncherStart();
            }
            catch (Exception ex) {
                Logger.Warn("Не удалось отправить метрику запуска: " + ex.Message);
            }

            base.OnStartup(e);
        }

        private void Application_Startup(object sender, StartupEventArgs e) {
            BootTrace("Starting Application_Startup");

            // Шаг 1. Окно проверки/обновления лаунчера
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown; // не завершаем приложение при закрытии диалога

            var upd = new UpdateWindow();
            upd.SourceInitialized += (_, __) => ApplyWindowChrome(upd);
            BootTrace("Showing UpdateWindow");
            var ok = upd.ShowDialog() == true || upd.Proceed;
            BootTrace($"UpdateWindow result ok={ok}");
            if (!ok) {
                // Пользователь закрыл окно или обновление обязательно
                BootTrace("Shutting down after update dialog");
                this.Shutdown();
                return;
            }

            // Шаг 2. Основное окно
            var mw = new MainWindow();
            mw.SourceInitialized += (_, __) => ApplyWindowChrome(mw);
            this.MainWindow = mw;
            this.ShutdownMode = ShutdownMode.OnMainWindowClose; // возвращаем обычный режим
            BootTrace("Showing MainWindow");
            mw.Show();
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

        /// <summary>Одна запись о ходе запуска: и в boot.log, и в консоль родителя, если она есть.</summary>
        private static void BootTrace(string message) {
            AppendBootLog(message);
            ConsoleLine("[BOOT] " + message);
        }

        private static void ConsoleLine(string message) {
            try {
                Console.WriteLine(message);
            }
            catch (Exception ex) {
                // Консоли может не быть вовсе — это нормальный режим запуска из проводника
                AppendBootLog("Console.WriteLine недоступен: " + ex.Message);
            }
        }

        private static void ConsoleErrorLine(string message) {
            try {
                Console.Error.WriteLine(message);
            }
            catch (Exception ex) {
                AppendBootLog("Console.Error недоступен: " + ex.Message);
            }
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
                // Logger.Write гасит собственные ошибки, дополнительная защита не нужна
                Logger.Warn("Discord shutdown failed: " + ex.Message);
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
            catch (Exception ex) {
                // Каталог логов недоступен — пишем рядом с процессом.
                // Logger здесь звать нельзя: он сам мог не подняться по той же причине.
                System.Diagnostics.Debug.WriteLine("GetBootLogPath: " + ex.Message);
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
            catch (Exception ex) {
                // Это сам журнал запуска: обращаться отсюда к Logger нельзя — получим рекурсию,
                // если недоступен тот же каталог. Остаётся отладочный вывод.
                System.Diagnostics.Debug.WriteLine("AppendBootLog: " + ex.Message);
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
            catch (Exception ex) {
                // Не обрезали — файл просто продолжит расти; ронять запуск из-за этого нельзя
                System.Diagnostics.Debug.WriteLine("TrimBootLog: " + ex.Message);
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

        // ===== Console attach helpers =====
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int ATTACHPARENTPROCESS = -1;

        private static void AttachToParentConsole() {
            try {
                // Подключаемся к консоли родителя, если есть
                if (!AttachConsole(ATTACHPARENTPROCESS)) {
                    return; // запуск не из консоли — обычный сценарий, не ошибка
                }

                try {
                    Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    Console.InputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                }
                catch (Exception ex) {
                    // Кодировку переставить не вышло: вывод будет в кодировке консоли
                    AppendBootLog("Кодировка консоли не изменена: " + ex.Message);
                }
            }
            catch (Exception ex) {
                AppendBootLog("Подключение к консоли родителя не выполнено: " + ex.Message);
            }
        }
    }
}
