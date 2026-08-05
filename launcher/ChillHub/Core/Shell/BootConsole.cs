// <copyright file="BootConsole.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    /// Вывод хода запуска в консоль родительского процесса, если лаунчер запустили из неё.
    /// Консоли обычно нет — это штатный запуск из проводника, а не ошибка.
    /// </summary>
    internal static class BootConsole {
        private const int ATTACHPARENTPROCESS = -1;

        /// <summary>Одна запись о ходе запуска: и в boot.log, и в консоль родителя, если она есть.</summary>
        /// <param name="message">Что записать.</param>
        internal static void Trace(string message) {
            BootLog.Append(message);
            Line("[BOOT] " + message);
        }

        internal static void Line(string message) {
            try {
                Console.WriteLine(message);
            }
            catch (Exception ex) {
                // Консоли может не быть вовсе — это нормальный режим запуска из проводника
                BootLog.Append("Console.WriteLine недоступен: " + ex.Message);
            }
        }

        internal static void ErrorLine(string message) {
            try {
                Console.Error.WriteLine(message);
            }
            catch (Exception ex) {
                BootLog.Append("Console.Error недоступен: " + ex.Message);
            }
        }

        internal static void AttachToParent() {
            try {
                // Подключаемся к консоли родителя, если есть
                if (!NativeMethods.AttachConsole(ATTACHPARENTPROCESS)) {
                    return; // запуск не из консоли — обычный сценарий, не ошибка
                }

                try {
                    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                }
                catch (Exception ex) {
                    // Кодировку переставить не вышло: вывод будет в кодировке консоли
                    BootLog.Append("Кодировка консоли не изменена: " + ex.Message);
                }
            }
            catch (Exception ex) {
                BootLog.Append("Подключение к консоли родителя не выполнено: " + ex.Message);
            }
        }

        private static class NativeMethods {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool AttachConsole(int dwProcessId);
        }
    }
}
