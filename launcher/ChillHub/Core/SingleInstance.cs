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
        /// Имя именованного события «покажи окно». Второй запуск сигналит по нему вместо
        /// (или в дополнение к) поиска окна по хендлу — хендл ищется через
        /// <see cref="Process.MainWindowHandle"/>, а он не находит окно, свёрнутое в трей
        /// через <c>Window.Hide()</c>: с точки зрения этого API у процесса тогда нет
        /// главного окна вовсе, и второй экземпляр раньше просто стартовал бы поверх
        /// первого, а не поднимал его из трея.
        /// </summary>
        private const string ShowEventName = @"Local\ChillHub.ShowRequested";

        /// <summary>
        /// Держится всё время жизни процесса: собери его сборщик мусора — замок отпустится
        /// и второй экземпляр запустится как ни в чём не бывало.
        /// </summary>
        private static Mutex? mutex;

        /// <summary>Держит поток-слушатель <see cref="ShowEventName"/> живым, пока жив процесс.</summary>
        private static EventWaitHandle? showEvent;

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
        /// Запускает фоновый поток, который ждёт сигнала «покажи окно» от следующего запуска
        /// лаунчера, и на каждый сигнал вызывает <paramref name="onShowRequested"/>.
        /// <para>
        /// Вызывается победителем замка один раз, сразу после <see cref="TryAcquire()"/> —
        /// без этого слушателя <see cref="TryFocusRunningInstance"/> у второго экземпляра
        /// сигналит в пустоту, и окно, свёрнутое в трей, никто не поднимает.
        /// Колбэк вызывается из потока-слушателя, не из UI-потока — маршалить в
        /// Dispatcher обязан вызывающий.
        /// </para>
        /// </summary>
        /// <param name="onShowRequested">Что сделать при получении сигнала.</param>
        internal static void StartListeningForShowRequests(Action onShowRequested) {
            try {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            }
            catch (Exception ex) {
                // Без слушателя лаунчер работоспособен — просто повторный запуск не поднимет
                // окно из трея, а стартует поверх (см. существующий handle-based fallback).
                ChillHub.Core.Logging.Logger.Warn($"SingleInstance: не удалось создать событие показа: {ex.Message}");
                return;
            }

            var thread = new Thread(() => {
                while (true) {
                    try {
                        if (!showEvent.WaitOne()) {
                            continue;
                        }
                    }
                    catch (ObjectDisposedException) {
                        return;
                    }
                    catch (Exception ex) {
                        ChillHub.Core.Logging.Logger.Warn($"SingleInstance: ожидание сигнала показа не удалось: {ex.Message}");
                        return;
                    }

                    try {
                        onShowRequested();
                    }
                    catch (Exception ex) {
                        ChillHub.Core.Logging.Logger.Warn($"SingleInstance: обработчик сигнала показа упал: {ex.Message}");
                    }
                }
            }) {
                IsBackground = true,
                Name = "ChillHub.ShowRequestListener",
            };
            thread.Start();
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

        /// <summary>
        /// Выводит окно уже запущенного экземпляра на передний план.
        /// <para>
        /// Сигнал именованным событием <see cref="ShowEventName"/> — побочный эффект,
        /// отправляется независимо от исхода: это единственный способ достучаться до окна,
        /// свёрнутого в трей через <c>Window.Hide()</c>, у которого нет хендла с точки зрения
        /// <see cref="Process.MainWindowHandle"/>. Но сам факт, что
        /// <see cref="EventWaitHandle"/> создался и <c>Set()</c> не бросил исключение, НЕ
        /// значит, что другой экземпляр действительно есть и слушает — это создаёт/открывает
        /// Win32-объект вне зависимости от того, слушает ли его кто-то, и раньше это ложно
        /// приводило к <see cref="TryAcquire(int)"/>, отдающему false немедленно, даже когда
        /// занятый замок вот-вот освободится (сценарий самообновления: апдейтер ждёт выхода
        /// старой копии и полагается на настоящее ожидание в <see cref="WaitFor"/>).
        /// Возвращаемое значение поэтому основано только на том, нашёлся ли другой процесс
        /// с тем же именем — через хендл окна или просто по факту существования процесса.
        /// </para>
        /// </summary>
        /// <returns>true, если другой процесс лаунчера действительно найден.</returns>
        private static bool TryFocusRunningInstance() {
            SignalShowRequested();

            try {
                var self = Process.GetCurrentProcess();
                var otherFound = false;
                foreach (var other in Process.GetProcessesByName(self.ProcessName)) {
                    try {
                        if (other.Id == self.Id) {
                            continue;
                        }

                        otherFound = true;

                        var handle = other.MainWindowHandle;
                        if (handle == IntPtr.Zero) {
                            // Хендла нет — скорее всего окно свёрнуто в трей (Window.Hide()).
                            // Показать его тут нечем, но сигнал events выше уже ушёл слушателю.
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

                return otherFound;
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"SingleInstance: не удалось показать окно запущенного экземпляра: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Сигналит именованным событием <see cref="ShowEventName"/> запущенному экземпляру:
        /// поднимет его окно, только если тот действительно слушает (см.
        /// <see cref="StartListeningForShowRequests"/>) — событие создаётся с
        /// <see cref="EventResetMode.AutoReset"/>, поэтому сигнал без слушателя просто
        /// теряется, а не копится. Чисто побочный эффект: успех/неудача не говорит, есть ли
        /// реально другой экземпляр — см. <see cref="TryFocusRunningInstance"/>.
        /// </summary>
        private static void SignalShowRequested() {
            try {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                ev.Set();
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"SingleInstance: не удалось отправить сигнал показа: {ex.Message}");
            }
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
