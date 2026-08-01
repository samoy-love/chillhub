// <copyright file="Logger.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Logging {
    using System;
    using System.IO;
    using System.Text;

    public static class Logger {
        /// <summary>Потолок размера активного файла лога.</summary>
        private const long MaxFileBytes = 5L * 1024 * 1024;

        /// <summary>Сколько архивных копий храним (client.1.log ... client.3.log).</summary>
        private const int MaxArchives = 3;

        private static readonly object @lock = new object();

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Логи пишутся ПО УМОЛЧАНИЮ: без них обратная связь и авто-отчёты приходят пустыми.
        // CHILLHUB_CLIENT_LOG=0 (false/off/no) — выключить, =1 (true/on/yes) — явно включить.
        private static readonly bool enabled = ResolveEnabled();

        // Логи лежат рядом с остальным пользовательским состоянием (%APPDATA%\ChillHub),
        // а не в %TEMP%, который чистится системой вместе с отчётами.
        private static readonly string logDirectory = ResolveLogDirectory();

        /// <summary>Текущий размер активного файла; -1 — ещё не считали с диска.</summary>
        private static long currentSize = -1;

        /// <summary>Каталог с логами клиента. Остальные части приложения должны брать путь отсюда.</summary>
        public static string LogDirectory => logDirectory;

        /// <summary>Активный файл лога клиента.</summary>
        public static string LogFilePath => Path.Combine(logDirectory, "client.log");

        /// <summary>Включена ли запись логов.</summary>
        public static bool IsEnabled => enabled;

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Error(Exception ex, string? message = null) {
            Write("ERROR", (message == null ? string.Empty : message + ": ") + ex.ToString());
            try { ChillHub.Core.ErrorReporter.Report(ex, message ?? "exception"); } catch { }
        }

        /// <summary>
        /// Шаблоны файлов, которые относятся к логам клиента (включая архивные после ротации).
        /// Используется диагностикой, чтобы не хардкодить имена.
        /// </summary>
        public static string[] LogFilePatterns => new[] { "client*.log", "boot*.log" };

        private static bool ResolveEnabled() {
            try {
                var raw = Environment.GetEnvironmentVariable("CHILLHUB_CLIENT_LOG");
                if (string.IsNullOrWhiteSpace(raw)) {
                    return true;
                }

                switch (raw.Trim().ToLowerInvariant()) {
                    case "0":
                    case "false":
                    case "off":
                    case "no":
                        return false;
                    default:
                        return true;
                }
            }
            catch {
                return true;
            }
        }

        private static string ResolveLogDirectory() {
            try {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ChillHub",
                    "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch {
            }

            try {
                var dir = Path.Combine(Path.GetTempPath(), "ChillHub");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch {
                return Environment.CurrentDirectory;
            }
        }

        private static void Write(string level, string message) {
            try {
                if (!enabled) {
                    return;
                }

                // Формат строки менять нельзя: на него смотрит человек в отчётах.
                var line = "[" + DateTime.Now.ToString("o") + "] " + level + " " + message + "\r\n";
                var bytes = Utf8.GetByteCount(line);
                var path = LogFilePath;

                lock (@lock) {
                    if (currentSize < 0) {
                        try { currentSize = File.Exists(path) ? new FileInfo(path).Length : 0; } catch { currentSize = 0; }
                    }

                    if (currentSize + bytes > MaxFileBytes) {
                        // Ротация — это только переименования (без копирования данных),
                        // поэтому блокировка держится ровно столько, сколько нужно.
                        Rotate(path);
                        currentSize = 0;
                    }

                    File.AppendAllText(path, line, Utf8);
                    currentSize += bytes;
                }
            }
            catch {
                // Логгер не имеет права ронять приложение.
                try { lock (@lock) { currentSize = -1; } } catch { }
            }
        }

        /// <summary>client.log -&gt; client.1.log -&gt; ... -&gt; client.N.log, самый старый удаляем.</summary>
        private static void Rotate(string path) {
            try {
                var dir = Path.GetDirectoryName(path) ?? logDirectory;
                var name = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);

                string Archive(int i) => Path.Combine(dir, name + "." + i.ToString() + ext);

                try {
                    var oldest = Archive(MaxArchives);
                    if (File.Exists(oldest)) {
                        File.Delete(oldest);
                    }
                }
                catch { }

                for (var i = MaxArchives - 1; i >= 1; i--) {
                    try {
                        var src = Archive(i);
                        if (File.Exists(src)) {
                            File.Move(src, Archive(i + 1), overwrite: true);
                        }
                    }
                    catch { }
                }

                if (File.Exists(path)) {
                    File.Move(path, Archive(1), overwrite: true);
                }
            }
            catch {
                // Не смогли ротировать — пробуем просто обнулить файл, чтобы он не рос вечно.
                try { File.WriteAllText(path, string.Empty, Utf8); } catch { }
            }
        }
    }
}
