// <copyright file="BootLog.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.IO;
    using System.Text;

    using ChillHub.Core.Logging;

    /// <summary>
    /// Журнал запуска. Пишется раньше обычного лога и не зависит от него: когда лаунчер
    /// не доходит до окна, boot.log — единственное, что объясняет, на каком шаге он встал.
    /// <para>
    /// boot.log лежит там же, где остальные логи клиента (см. <see cref="Logger.LogDirectory"/>),
    /// а не в %TEMP%, который чистится системой.
    /// </para>
    /// </summary>
    internal static class BootLog {
        /// <summary>Потолок boot.log: при превышении оставляем только последнюю часть файла.</summary>
        internal const long MaxBytes = 512 * 1024;

        /// <summary>Сколько байт хвоста сохраняем при обрезании boot.log.</summary>
        internal const int KeepBytes = 128 * 1024;

        private static readonly object BootLogLock = new object();

        /// <summary>
        /// Куда писать журнал. Отдельным швом — иначе тест, доводящий запуск до записи
        /// в журнал, писал бы в настоящий boot.log пользователя.
        /// </summary>
        internal static Func<string> PathProvider { get; set; } = GetPath;

        /// <summary>Возвращает журнал в настоящий каталог логов.</summary>
        internal static void ResetPathForTests() => PathProvider = GetPath;

        /// <summary>
        /// Дописывает строку в boot.log в формате «[ISO8601] текст» и не даёт файлу расти вечно.
        /// Никогда не бросает исключений.
        /// </summary>
        /// <param name="message">Что записать.</param>
        internal static void Append(string message) => AppendTo(PathProvider(), message);

        /// <summary>
        /// То же, но в заданный файл. Отдельным входом — иначе проверить обрезание и формат
        /// записи можно только записав в настоящий каталог логов пользователя.
        /// </summary>
        /// <param name="path">Файл журнала.</param>
        /// <param name="message">Что записать.</param>
        internal static void AppendTo(string path, string message) {
            try {
                var line = "[" + DateTime.Now.ToString("o") + "] " + message + "\r\n";
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                lock (BootLogLock) {
                    Trim(path, utf8);
                    File.AppendAllText(path, line, utf8);
                }
            }
            catch (Exception ex) {
                // Это сам журнал запуска: обращаться отсюда к Logger нельзя — получим рекурсию,
                // если недоступен тот же каталог. Остаётся отладочный вывод.
                System.Diagnostics.Debug.WriteLine("AppendBootLog: " + ex.Message);
            }
        }

        /// <summary>Простая обрезка с начала: оставляем последние <see cref="KeepBytes"/> байт.</summary>
        /// <param name="path">Файл журнала.</param>
        /// <param name="utf8">Кодировка файла.</param>
        internal static void Trim(string path, Encoding utf8) {
            try {
                if (!File.Exists(path)) {
                    return;
                }

                var len = new FileInfo(path).Length;
                if (len <= MaxBytes) {
                    return;
                }

                var bytes = File.ReadAllBytes(path);
                var keep = Math.Min(KeepBytes, bytes.Length);
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

        /// <summary>Путь к boot.log. Никогда не бросает: запуск не должен падать из-за журнала.</summary>
        /// <returns>Полный путь к файлу.</returns>
        internal static string GetPath() {
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
    }
}
