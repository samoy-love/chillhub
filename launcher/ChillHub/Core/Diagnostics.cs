// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.

namespace ChillHub.Core {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    public static class Diagnostics {
        public sealed record DiagnosticsBundle(string LogsMarkdown, Dictionary<string, string> SystemHints);

        public static DiagnosticsBundle Build()
        {
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
                    } else {
                        sb.AppendLine("(config.json not found)");
                    }
                } catch (Exception ex) { sb.AppendLine($"(config read error: {ex.Message})"); }
                sb.AppendLine();

                // App root quick hashes (limited)
                sb.AppendLine("## Launcher Files (SHA-256)");
                try {
                    var asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    var appRoot = string.IsNullOrWhiteSpace(asmLoc) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(asmLoc)!;
                    hints["appRoot"] = appRoot;
                    AppendDirHashes(sb, appRoot, maxFiles: 200, maxBytesPerFile: 5 * 1024 * 1024);
                } catch (Exception ex) { sb.AppendLine($"(hash listing error: {ex.Message})"); }
                sb.AppendLine();

                // Games root folder tree (depth up to 10)
                sb.AppendLine("## Games Root Listing (folders, depth=10)");
                try {
                    var gamesRoot = ChillHub.Core.ConfigService.Current.GamesPath;
                    hints["gamesRoot"] = gamesRoot;
                    AppendFolderTree(sb, gamesRoot, maxDepth: 10);
                } catch (Exception ex) { sb.AppendLine($"(games listing error: {ex.Message})"); }
                sb.AppendLine();

                // Logs
                sb.AppendLine("## Logs");
                try {
                    var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChillHub", "logs");
                    hints["logsDir"] = logsDir;
                    AppendLogs(sb, logsDir, new[] { "launcher*.log", "updater*.log" }, maxFiles: 4, maxTailBytes: 150 * 1024);
                } catch (Exception ex) { sb.AppendLine($"(logs error: {ex.Message})"); }
                sb.AppendLine();

                // Boot/client logs in TEMP (App boot, Logger)
                sb.AppendLine("## Temp Logs");
                try {
                    var tmpCh = Path.Combine(Path.GetTempPath(), "ChillHub");
                    var bootLog = Path.Combine(tmpCh, "boot.log");
                    var clientLog = Path.Combine(tmpCh, "client.log");
                    hints["tempDir"] = tmpCh;
                    AppendSpecificLogs(sb, new[]{bootLog, clientLog}, maxFiles: 2, maxTailBytes: 120*1024);
                } catch (Exception ex) { sb.AppendLine($"(temp logs error: {ex.Message})"); }
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
                            if (File.Exists(log1)) files.Add(log1);
                            var updDir = Path.Combine(verDir, "updater");
                            if (Directory.Exists(updDir)) {
                                // include any *.log in updater dir if present
                                try { files.AddRange(Directory.GetFiles(updDir, "*.log", SearchOption.TopDirectoryOnly)); } catch {}
                            }
                        }
                    }
                    AppendSpecificLogs(sb, files, maxFiles: 6, maxTailBytes: 160*1024);
                } catch (Exception ex) { sb.AppendLine($"(selfupdate logs error: {ex.Message})"); }
                sb.AppendLine();

                // Feedback queue path hint (if present)
                try {
                    var qPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "feedback_queue.json");
                    if (File.Exists(qPath)) hints["feedbackQueuePath"] = qPath;
                } catch { }
            } catch { }
            return new DiagnosticsBundle(sb.ToString(), hints);
        }

        private static void AppendFolderTree(StringBuilder sb, string root, int maxDepth) {
            try {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { sb.AppendLine("(games root not found)"); return; }
                sb.AppendLine($"Root: {root}");
                void Walk(string dir, int depth) {
                    if (depth > maxDepth) return;
                    string indent = new string(' ', Math.Max(0, (depth-1))) + (depth>0?"":"");
                    if (depth == 0) {
                        // top-level: list immediate folders
                        foreach (var d in SafeGetDirs(dir)) {
                            sb.AppendLine("- " + d);
                            Walk(d, depth+1);
                        }
                    } else {
                        foreach (var d in SafeGetDirs(dir)) {
                            // show relative path from root for readability
                            string rel = MakeRelative(root, d);
                            sb.AppendLine("  "+new string(' ', Math.Max(0, (depth-1)*2))+"- "+rel);
                            Walk(d, depth+1);
                        }
                    }
                }
                Walk(root, 0);
            } catch (Exception ex) { sb.AppendLine($"(listing error: {ex.Message})"); }

            static IEnumerable<string> SafeGetDirs(string p) { try { return Directory.GetDirectories(p); } catch { return Array.Empty<string>(); } }
            static string MakeRelative(string root, string path) {
                try {
                    var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
                    var q = Path.GetFullPath(path);
                    if (q.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return q.Substring(r.Length).Replace(Path.DirectorySeparatorChar,'/');
                } catch {}
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
                    } catch (Exception ex) { sb.AppendLine($"- {path} (error: {ex.Message})"); }
                }
            } catch (Exception ex) { sb.AppendLine($"(hash error: {ex.Message})"); }
        }

        private static string ComputeSha256(string file) {
            try {
                using var fs = File.OpenRead(file);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            } catch { return string.Empty; }
        }

        private static void AppendLogs(StringBuilder sb, string logsDir, string[] patterns, int maxFiles, int maxTailBytes) {
            try {
                if (!Directory.Exists(logsDir)) { sb.AppendLine($"(logs dir not found: {logsDir})"); return; }
                var files = new List<string>();
                foreach (var pat in patterns) {
                    try { files.AddRange(Directory.GetFiles(logsDir, pat, SearchOption.TopDirectoryOnly)); } catch { }
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                if (files.Count == 0) { sb.AppendLine("(no log files matched)"); return; }
                int used = 0;
                foreach (var f in files) {
                    if (used >= maxFiles) { sb.AppendLine($"(limit reached: {maxFiles} files)"); break; }
                    sb.AppendLine($"### {f}");
                    try {
                        var bytes = File.ReadAllBytes(f);
                        if (bytes.Length > maxTailBytes) {
                            var tail = new byte[maxTailBytes];
                            Buffer.BlockCopy(bytes, bytes.Length - maxTailBytes, tail, 0, maxTailBytes);
                            sb.AppendLine("```log");
                            sb.AppendLine(Encoding.UTF8.GetString(tail));
                            sb.AppendLine("```\n(tail only)");
                        } else {
                            sb.AppendLine("```log");
                            sb.AppendLine(Encoding.UTF8.GetString(bytes));
                            sb.AppendLine("```");
                        }
                    } catch (Exception ex) { sb.AppendLine($"(read error: {ex.Message})"); }
                    used++;
                }
            } catch (Exception ex) { sb.AppendLine($"(logs listing error: {ex.Message})"); }
        }

        private static void AppendSpecificLogs(StringBuilder sb, IEnumerable<string> filesIn, int maxFiles, int maxTailBytes)
        {
            try {
                var files = new List<string>();
                foreach (var f in filesIn) { if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) files.Add(f); }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                if (files.Count == 0) { sb.AppendLine("(no temp/selfupdate logs found)"); return; }
                int used = 0;
                foreach (var f in files) {
                    if (used >= maxFiles) { sb.AppendLine($"(limit reached: {maxFiles} files)"); break; }
                    sb.AppendLine($"### {f}");
                    try {
                        var bytes = File.ReadAllBytes(f);
                        if (bytes.Length > maxTailBytes) {
                            var tail = new byte[maxTailBytes];
                            Buffer.BlockCopy(bytes, bytes.Length - maxTailBytes, tail, 0, maxTailBytes);
                            sb.AppendLine("```log");
                            sb.AppendLine(Encoding.UTF8.GetString(tail));
                            sb.AppendLine("```\n(tail only)");
                        } else {
                            sb.AppendLine("```log");
                            sb.AppendLine(Encoding.UTF8.GetString(bytes));
                            sb.AppendLine("```");
                        }
                    } catch (Exception ex) { sb.AppendLine($"(read error: {ex.Message})"); }
                    used++;
                }
            } catch (Exception ex) { sb.AppendLine($"(specific logs error: {ex.Message})"); }
        }
    }
}
