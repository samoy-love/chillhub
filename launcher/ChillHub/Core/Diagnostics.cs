// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.

namespace ChillHub.Core {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    public static class Diagnostics {
        /// <summary>
        /// Потолок всего бандла в БАЙТАХ UTF-8. На сервере (admin, feedbackMaxLogBytes) стоит 256 КиБ,
        /// и всё, что больше, молча обрезается. Держим запас.
        /// </summary>
        private const int BundleMaxBytes = 240 * 1024;

        /// <summary>Суммарный бюджет на содержимое логов внутри бандла.</summary>
        private const int LogsTotalBudgetBytes = 160 * 1024;

        /// <summary>Потолок хвоста одного файла лога.</summary>
        private const int LogTailBytes = 48 * 1024;

        /// <summary>
        /// Глубина обхода папки игр. Для разбора достаточно увидеть, какие игры установлены
        /// и есть ли у них подпапки: глубина 10 выкладывала наружу всё дерево пользователя
        /// целиком, а пользы не добавляла.
        /// </summary>
        private const int GamesTreeMaxDepth = 2;

        /// <summary>Сколько строк дерева папки игр максимум попадает в бандл.</summary>
        private const int GamesTreeMaxEntries = 200;

        /// <summary>
        /// Человекочитаемый перечень того, что уходит в бандл. Показывается пользователю
        /// в форме обратной связи: молча прикладывать конфиг и пути — нечестно.
        /// </summary>
        public static string[] BundleContents => new[] {
            "настройки лаунчера (config.json): адрес сервера, папка для игр, параметры загрузки",
            "версия лаунчера, версия Windows и .NET",
            "список установленных игр (имена папок, без содержимого файлов)",
            "последние записи журналов лаунчера и обновления",
            "контрольные суммы файлов самого лаунчера",
        };

        /// <summary>Общий бюджет на все секции логов: чтобы один болтливый файл не съел весь бандл.</summary>
        private sealed class LogBudget {
            public int Remaining { get; set; } = LogsTotalBudgetBytes;
        }

        public sealed record DiagnosticsBundle(string LogsMarkdown, Dictionary<string, string> SystemHints);

        public static DiagnosticsBundle Build() {
            var sb = new StringBuilder(32 * 1024);
            var hints = new Dictionary<string, string>();
            try {
                sb.AppendLine("# ChillHub Diagnostics Bundle");
                sb.AppendLine($"Generated: {DateTime.UtcNow:O} (UTC)");
                sb.AppendLine();

                // Config dump
                sb.AppendLine("## Config");
                try {
                    // Берём путь у ConfigService: конфиг переехал в %APPDATA%\ChillHub,
                    // потому что %LOCALAPPDATA%\ChillHub — это каталог установки лаунчера.
                    var cfgPath = ChillHub.Core.ConfigService.ConfigFilePath;
                    hints["configPath"] = cfgPath;
                    if (File.Exists(cfgPath)) {
                        var json = File.ReadAllText(cfgPath, Encoding.UTF8);
                        sb.AppendLine("```json");
                        sb.AppendLine(json);
                        sb.AppendLine("```");
                    }
                    else {
                        sb.AppendLine("(config.json not found)");
                    }
                }
                catch (Exception ex) { sb.AppendLine($"(config read error: {ex.Message})"); }
                sb.AppendLine();

                // App root quick hashes (limited)
                sb.AppendLine("## Launcher Files (SHA-256)");
                try {
                    var asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    var appRoot = string.IsNullOrWhiteSpace(asmLoc) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(asmLoc)!;
                    hints["appRoot"] = appRoot;
                    AppendDirHashes(sb, appRoot, maxFiles: 200, maxBytesPerFile: 5 * 1024 * 1024);
                }
                catch (Exception ex) { sb.AppendLine($"(hash listing error: {ex.Message})"); }
                sb.AppendLine();

                // Games root folder tree: короткое дерево, без выкладывания всего диска наружу
                sb.AppendLine($"## Games Root Listing (folders, depth={GamesTreeMaxDepth})");
                try {
                    var gamesRoot = ChillHub.Core.ConfigService.Current.GamesPath;
                    hints["gamesRoot"] = gamesRoot;
                    AppendFolderTree(sb, gamesRoot, maxDepth: GamesTreeMaxDepth);
                }
                catch (Exception ex) { sb.AppendLine($"(games listing error: {ex.Message})"); }
                sb.AppendLine();

                // Логи клиента: одна секция вместо прежних «Logs» + «Temp Logs».
                // Путь берём у Logger, чтобы не разъезжаться с ним при переездах каталога.
                var budget = new LogBudget();
                sb.AppendLine("## Logs");
                try {
                    var logsDir = ChillHub.Core.Logging.Logger.LogDirectory;
                    hints["logsDir"] = logsDir;

                    // Собираем и активные, и архивные файлы после ротации (client.1.log и т.п.).
                    var files = new List<string>();
                    CollectLogFiles(files, logsDir, ChillHub.Core.Logging.Logger.LogFilePatterns);

                    // Старое расположение (%TEMP%\ChillHub) — у тех, кто ещё не перезапускался после переезда.
                    try {
                        var legacyDir = Path.Combine(Path.GetTempPath(), "ChillHub");
                        if (!string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(logsDir), StringComparison.OrdinalIgnoreCase)) {
                            hints["legacyLogsDir"] = legacyDir;
                            CollectLogFiles(files, legacyDir, new[] { "client*.log", "boot*.log" });
                        }
                    }
                    catch { }

                    AppendSpecificLogs(sb, files, maxFiles: 6, maxTailBytes: LogTailBytes, budget: budget);
                }
                catch (Exception ex) { sb.AppendLine($"(logs error: {ex.Message})"); }
                sb.AppendLine();

                // SelfUpdate logs (apply-update.log) produced by native updater
                sb.AppendLine("## SelfUpdate Logs");
                try {
                    var suRoot = Path.Combine(Path.GetTempPath(), "ChillHub", "SelfUpdate");
                    hints["selfUpdateRoot"] = suRoot;
                    var files = new List<string>();
                    if (Directory.Exists(suRoot)) {
                        foreach (var verDir in Directory.EnumerateDirectories(suRoot)) {
                            var log1 = Path.Combine(verDir, "apply-update.log");
                            if (File.Exists(log1)) {
                                files.Add(log1);
                            }

                            var updDir = Path.Combine(verDir, "updater");
                            if (Directory.Exists(updDir)) {
                                // include any *.log in updater dir if present
                                try { files.AddRange(Directory.GetFiles(updDir, "*.log", SearchOption.TopDirectoryOnly)); } catch { }
                            }
                        }
                    }
                    AppendSpecificLogs(sb, files, maxFiles: 4, maxTailBytes: LogTailBytes, budget: budget);
                }
                catch (Exception ex) { sb.AppendLine($"(selfupdate logs error: {ex.Message})"); }
                sb.AppendLine();

                // Feedback queue path hint (if present)
                try {
                    var qPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "feedback_queue.json");
                    if (File.Exists(qPath)) {
                        hints["feedbackQueuePath"] = qPath;
                    }
                }
                catch { }
            }
            catch { }

            // Абсолютные пути тянут за собой имя пользователя Windows. Для разбора отчёта оно
            // не нужно — заменяем на плейсхолдеры и в тексте бандла, и в подсказках.
            var redactedHints = new Dictionary<string, string>();
            foreach (var kv in hints) {
                redactedHints[kv.Key] = Redact(kv.Value);
            }

            return new DiagnosticsBundle(Redact(sb.ToString()), redactedHints);
        }

        /// <summary>
        /// Убирает из текста имя пользователя Windows: сначала полный путь к профилю,
        /// затем само имя (оно встречается и в путях вида C:\Users\ivan\...).
        /// </summary>
        private static string Redact(string text) {
            if (string.IsNullOrEmpty(text)) {
                return text ?? string.Empty;
            }

            try {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile)) {
                    text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

                    // В json конфиг путь попадает с экранированными слешами
                    text = text.Replace(profile.Replace(@"\", @"\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                }

                var user = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3) {
                    text = text.Replace(user, "%USER%", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) {
                // Лучше отдать неотредактированный текст, чем не отдать ничего:
                // без бандла разбор жалобы невозможен.
                System.Diagnostics.Debug.WriteLine("Diagnostics.Redact: " + ex.Message);
            }

            return text;
        }

        private static void AppendFolderTree(StringBuilder sb, string root, int maxDepth) {
            try {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { sb.AppendLine("(games root not found)"); return; }
                sb.AppendLine($"Root: {root}");
                int emitted = 0;
                void Walk(string dir, int depth) {
                    if (depth > maxDepth || emitted >= GamesTreeMaxEntries) {
                        return;
                    }

                    foreach (var d in SafeGetDirs(dir)) {
                        if (emitted >= GamesTreeMaxEntries) {
                            sb.AppendLine($"(limit reached: {GamesTreeMaxEntries} folders)");
                            return;
                        }

                        // Показываем путь относительно корня: абсолютный всё равно есть в Root
                        string rel = depth == 0 ? Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar)) : MakeRelative(root, d);
                        sb.AppendLine("  " + new string(' ', Math.Max(0, depth * 2)) + "- " + rel);
                        emitted++;
                        Walk(d, depth + 1);
                    }
                }
                Walk(root, 0);
            }
            catch (Exception ex) { sb.AppendLine($"(listing error: {ex.Message})"); }

            static IEnumerable<string> SafeGetDirs(string p) { try { return Directory.GetDirectories(p); } catch { return Array.Empty<string>(); } }
            static string MakeRelative(string root, string path) {
                try {
                    var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var q = Path.GetFullPath(path);
                    if (q.StartsWith(r, StringComparison.OrdinalIgnoreCase)) {
                        return q.Substring(r.Length).Replace(Path.DirectorySeparatorChar, '/');
                    }
                }
                catch { }
                return path;
            }
        }

        private static void AppendDirHashes(StringBuilder sb, string root, int maxFiles, int maxBytesPerFile) {
            try {
                if (!Directory.Exists(root)) { sb.AppendLine($"(not found: {root})"); return; }
                sb.AppendLine($"Root: {root}");
                int count = 0;
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                    if (count >= maxFiles) { sb.AppendLine($"(limit reached: {maxFiles} files)"); break; }
                    try {
                        var fi = new FileInfo(path);
                        if (fi.Length > maxBytesPerFile) { sb.AppendLine($"- {path} [size={fi.Length} bytes, skipped hashing]"); continue; }
                        var sha = ComputeSha256(path);
                        sb.AppendLine($"- {path}  {sha}");
                        count++;
                    }
                    catch (Exception ex) { sb.AppendLine($"- {path} (error: {ex.Message})"); }
                }
            }
            catch (Exception ex) { sb.AppendLine($"(hash error: {ex.Message})"); }
        }

        private static string ComputeSha256(string file) {
            try {
                using var fs = File.OpenRead(file);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Складывает в <paramref name="files"/> существующие файлы логов каталога по маскам.
        /// Свежие — первыми: при исчерпании бюджета обрезается самое старое, а не самое нужное.
        /// </summary>
        private static void CollectLogFiles(List<string> files, string dir, string[] patterns) {
            try {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) {
                    return;
                }

                var found = new List<string>();
                foreach (var pat in patterns ?? Array.Empty<string>()) {
                    try {
                        found.AddRange(Directory.GetFiles(dir, pat, SearchOption.TopDirectoryOnly));
                    }
                    catch { }
                }

                found.Sort((a, b) => {
                    try {
                        return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
                    }
                    catch {
                        return StringComparer.OrdinalIgnoreCase.Compare(a, b);
                    }
                });

                foreach (var f in found) {
                    if (!files.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) {
                        files.Add(f);
                    }
                }
            }
            catch { }
        }

        private static void AppendSpecificLogs(StringBuilder sb, IEnumerable<string> filesIn, int maxFiles, int maxTailBytes, LogBudget budget) {
            try {
                var files = new List<string>();
                foreach (var f in filesIn) {
                    if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) {
                        files.Add(f);
                    }
                }

                if (files.Count == 0) { sb.AppendLine("(no log files found)"); return; }
                int used = 0;
                foreach (var f in files) {
                    if (used >= maxFiles) { sb.AppendLine($"(limit reached: {maxFiles} files)"); break; }
                    if (budget.Remaining <= 0) { sb.AppendLine("(log budget exhausted; remaining files omitted)"); break; }

                    // Не даём одному болтливому файлу съесть весь бандл: сервер режет всё,
                    // что больше feedbackMaxLogBytes, и обрезка была бы молчаливой.
                    var allowance = Math.Min(maxTailBytes, budget.Remaining);
                    sb.AppendLine($"### {f}");
                    try {
                        var bytes = File.ReadAllBytes(f);
                        if (bytes.Length > allowance) {
                            var tail = new byte[allowance];
                            Buffer.BlockCopy(bytes, bytes.Length - allowance, tail, 0, allowance);
                            sb.AppendLine("```log");
                            sb.AppendLine(Encoding.UTF8.GetString(tail));
                            sb.AppendLine("```\n(tail only)");
                            budget.Remaining -= allowance;
                        }
                        else {
                            sb.AppendLine("```log");
                            sb.AppendLine(Encoding.UTF8.GetString(bytes));
                            sb.AppendLine("```");
                            budget.Remaining -= bytes.Length;
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"(read error: {ex.Message})"); }
                    used++;
                }
            }
            catch (Exception ex) { sb.AppendLine($"(specific logs error: {ex.Message})"); }
        }
    }
}
