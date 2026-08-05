// <copyright file="SingleInstance.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// Замок на второй экземпляр лаунчера.
    /// <para>
    /// Две копии лаунчера — это две независимые синхронизации одной и той же папки игры:
    /// один экземпляр качает файл, второй в этот же момент считает его лишним и удаляет,
    /// а маркер незавершённого обновления снимает тот, кто закончил первым. Общего замка
    /// на папку игры нет, и делать его на каждую операцию дороже и хуже, чем просто не
    /// давать запустить второй экземпляр.
    /// </para>
    /// </summary>
    public static class SingleInstance {
        /// <summary>
        /// Сколько ждать освобождения замка, прежде чем сдаться, мс.
        /// <para>
        /// Ноль тут не годится: апдейтер ждёт выхода лаунчера с ограничением по времени и
        /// по его истечении перезапускает лаунчер, не дождавшись. Отказ без ожидания
        /// превратил бы этот редкий случай в «после обновления лаунчер не запускается».
        /// </para>
        /// </summary>
        private const int AcquireTimeoutMs = 7000;

        /// <summary>Имя замка. Local — то есть на сеанс пользователя, а не на всю машину.</summary>
        private const string MutexName = @"Local\ChillHub.SingleInstance";

        /// <summary>
        /// Держится всё время жизни процесса: собери его сборщик мусора — замок отпустится
        /// и второй экземпляр запустится как ни в чём не бывало.
        /// </summary>
        private static Mutex? mutex;

        /// <summary>
        /// Пытается занять замок. Если его держит другой экземпляр — выводит его окно
        /// на передний план, чтобы пользователь увидел уже запущенный лаунчер.
        /// </summary>
        /// <returns>true, если запускаться можно.</returns>
        public static bool TryAcquire() => TryAcquire(AcquireTimeoutMs);

        /// <summary>
        /// То же самое, но с задаваемым ожиданием: тестам нужен короткий таймаут, иначе
        /// проверка «второй экземпляр не стартует» стоила бы прогону семь секунд.
        /// </summary>
        /// <param name="timeoutMs">Сколько ждать освобождения замка.</param>
        /// <returns>true, если запускаться можно.</returns>
        internal static bool TryAcquire(int timeoutMs) {
            try {
                mutex = new Mutex(initiallyOwned: false, MutexName);
            }
            catch (Exception ex) {
                // Не смогли даже создать замок — это не повод не запускать лаунчер
                ChillHub.Core.Logging.Logger.Warn($"SingleInstance: не удалось создать замок: {ex.Message}");
                return true;
            }

            if (WaitFor(0)) {
                return true;
            }

            // Замок занят. Обычный случай — пользователь запустил лаунчер второй раз:
            // показываем ему уже открытое окно и тихо уходим.
            if (TryFocusRunningInstance()) {
                ChillHub.Core.Logging.Logger.Info("SingleInstance: лаунчер уже запущен, показываем его окно");
                return false;
            }

            // Окна не нашли: другой экземпляр либо ещё поднимается, либо доживает
            // последние мгновения после самообновления. Ждём — см. AcquireTimeoutMs.
            if (WaitFor(timeoutMs)) {
                return true;
            }

            ChillHub.Core.Logging.Logger.Warn(
                $"SingleInstance: замок занят, окна предыдущего экземпляра нет; сдались через {timeoutMs} мс");
            return false;
        }

        /// <summary>
        /// Отпускает замок. Нужно только тестам: у живого лаунчера замок держится всё
        /// время работы и снимается вместе с процессом. Вызывать из того же потока,
        /// который его занял, — владение мьютексом принадлежит потоку.
        /// </summary>
        internal static void ReleaseForTests() {
            try {
                mutex?.ReleaseMutex();
            }
            catch {
                // Не нашим потоком или не занимали вовсе — тестам это не мешает
            }

            try {
                mutex?.Dispose();
            }
            catch {
            }

            mutex = null;
        }

        /// <summary>Занимает замок, считая брошенный своим.</summary>
        /// <param name="timeoutMs">Сколько ждать.</param>
        /// <returns>true, если замок наш.</returns>
        private static bool WaitFor(int timeoutMs) {
            try {
                return mutex!.WaitOne(timeoutMs, exitContext: false);
            }
            catch (AbandonedMutexException) {
                // Прошлый владелец умер, не отпустив замок: владение переходит к нам —
                // это и есть штатное «лаунчер убили из диспетчера».
                return true;
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"SingleInstance: ожидание замка не удалось: {ex.Message}");
                return true;
            }
        }

        /// <summary>Выводит окно уже запущенного экземпляра на передний план.</summary>
        /// <returns>true, если такое окно нашлось.</returns>
        private static bool TryFocusRunningInstance() {
            try {
                var self = Process.GetCurrentProcess();
                foreach (var other in Process.GetProcessesByName(self.ProcessName)) {
                    try {
                        if (other.Id == self.Id) {
                            continue;
                        }

                        var handle = other.MainWindowHandle;
                        if (handle == IntPtr.Zero) {
                            continue;
                        }

                        NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(handle);
                        return true;
                    }
                    catch {
                        // Процесс мог закрыться прямо сейчас — смотрим следующий
                    }
                    finally {
                        other.Dispose();
                    }
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"SingleInstance: не удалось показать окно запущенного экземпляра: {ex.Message}");
            }

            return false;
        }

        private static class NativeMethods {
            internal const int SW_RESTORE = 9;

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        }
    }
}
