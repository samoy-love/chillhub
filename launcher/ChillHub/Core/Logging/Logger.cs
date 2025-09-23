// <copyright file="Logger.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Logging {
    using System;
    using System.IO;
    using System.Text;

    public static class Logger {
        private static readonly object @lock = new object();

        // MVP requirement: no client logs persisted. Allow opt-in via env var only.
        private static readonly bool enabled = string.Equals(Environment.GetEnvironmentVariable("CHILLHUB_CLIENT_LOG"), "1", StringComparison.Ordinal);

        private static string LogFilePath {
            get {
                try {
                    var dir = Path.Combine(Path.GetTempPath(), "ChillHub");
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "client.log");
                }
                catch {
                    return Path.Combine(Environment.CurrentDirectory, "client.log");
                }
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Error(Exception ex, string? message = null) {
            Write("ERROR", (message == null ? string.Empty : message + ": ") + ex.ToString());
            try { ChillHub.Core.ErrorReporter.Report(ex, message ?? "exception"); } catch { }
        }

        private static void Write(string level, string message) {
            try {
                if (!enabled) {
                    // No-op in MVP unless explicitly enabled via env var
                    return;
                }

                var line = "[" + DateTime.Now.ToString("o") + "] " + level + " " + message + "\r\n";
                lock (@lock) {
                    File.AppendAllText(LogFilePath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch {
            }
        }
    }
}
