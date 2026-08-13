// <copyright file="PlaytimeStore.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;

    /// <summary>Накопленное время игры в одну игру и сведения о последней сессии.</summary>
    public sealed class PlaytimeEntry {
        /// <summary>Суммарное наигранное время в секундах.</summary>
        public long TotalSeconds { get; set; }

        /// <summary>Момент окончания последней сессии (UTC), либо null — сессий ещё не было.</summary>
        public DateTime? LastSessionAt { get; set; }

        /// <summary>Длительность последней сессии в секундах.</summary>
        public long LastSessionSeconds { get; set; }
    }

    /// <summary>Незакрытая на момент записи сессия: игра запущена, но выход процесса ещё не увидели.</summary>
    internal sealed class PendingSession {
        public string GameId { get; set; } = string.Empty;

        public int ProcessId { get; set; }

        /// <summary>
        /// Время старта процесса в тиках — вместе с ProcessId страхует от повторного использования
        /// PID другим процессом системой между закрытием лаунчера и его следующим запуском.
        /// </summary>
        public long ProcessStartTimeTicks { get; set; }

        public DateTime SessionStartUtc { get; set; }
    }

    /// <summary>
    /// Хранит наигранное время игроков: `%APPDATA%\ChillHub\playtime.json` (сводка по играм) и
    /// `%APPDATA%\ChillHub\playtime.sessions.json` (незакрытые сессии, служебный файл).
    /// <para>
    /// Каталог — тот же, что использует <see cref="ConfigService"/> для config.json, а не
    /// %LOCALAPPDATA%\ChillHub: этот каталог занят под установку лаунчера (exe/dll/runtimes),
    /// и любой пользовательский файл там попадает в пакет самообновления — тот же повод, из-за
    /// которого конфиг когда-то переехал в %APPDATA% (см. комментарий в Core/Config.cs).
    /// </para>
    /// <para>
    /// Сессия считается на выходе процесса ИГРЫ, а не лаунчера: старт запоминается в
    /// playtime.sessions.json, и если лаунчер закрылся раньше игры, при следующем запуске
    /// <see cref="EnsureReconciled"/> находит незакрытую сессию и либо снова дожидается выхода
    /// (если игра всё ещё бежит), либо закрывает её сразу (если игра уже закрылась, пока
    /// лаунчера не было — точный момент выхода в этом случае не известен, берём момент
    /// обнаружения: недосчитанные минуты лучше потерянной сессии).
    /// </para>
    /// </summary>
    internal static class PlaytimeStore {
        private static readonly string AppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");

        private static readonly object FileLock = new object();

        private static int reconciled;

        private static string PlaytimePath => Path.Combine(AppDir, "playtime.json");

        private static string PendingPath => Path.Combine(AppDir, "playtime.sessions.json");

        /// <summary>Сводка по одной игре. Реконсилирует незакрытые сессии перед чтением.</summary>
        internal static PlaytimeEntry Get(string gameId) {
            EnsureReconciled();
            var all = LoadAll();
            return all.TryGetValue(gameId, out var entry) ? entry : new PlaytimeEntry();
        }

        /// <summary>
        /// Запоминает старт сессии игры и заводит фоновое ожидание её выхода.
        /// Вызывается сразу после успешного <c>Process.Start</c>.
        /// </summary>
        internal static void BeginSession(string gameId, Process process) {
            if (string.IsNullOrWhiteSpace(gameId) || process == null) {
                return;
            }

            EnsureReconciled();

            try {
                lock (FileLock) {
                    var pending = LoadPendingLocked();
                    // Keyed by process id, not gameId: launching the same game twice
                    // (a second instance while the first is still running) must not
                    // let the second BeginSession overwrite the first session's entry
                    // — that silently corrupted/dropped playtime for whichever
                    // process exited first.
                    pending[PendingKey(process.Id)] = new PendingSession {
                        GameId = gameId,
                        ProcessId = process.Id,
                        ProcessStartTimeTicks = SafeStartTimeTicks(process),
                        SessionStartUtc = DateTime.UtcNow,
                    };
                    SavePendingLocked(pending);
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.BeginSession({gameId}): {ex.Message}");
            }

            WatchAsync(process.Id, process);
        }

        /// <summary>«142 ч» — суммарное время, округлённое вниз до часа.</summary>
        internal static string FormatTotal(long totalSeconds) {
            var hours = totalSeconds / 3600;
            return $"{hours} ч";
        }

        /// <summary>«вчера, 2ч 10м» / «сегодня, 45м» / «3 дня назад, 1ч 05м» / «нет данных».</summary>
        internal static string FormatLastSession(DateTime? lastSessionAtUtc, long lastSessionSeconds) {
            if (lastSessionAtUtc is not DateTime atUtc) {
                return "нет данных";
            }

            var local = atUtc.ToLocalTime();
            var today = DateTime.Now.Date;
            var days = (today - local.Date).Days;

            string when = days switch {
                <= 0 => "сегодня",
                1 => "вчера",
                _ when days > 1 && days < 7 => $"{days} {HomeFormat.PluralizeDayRu(days)} назад",
                _ => local.ToString("dd.MM.yyyy"),
            };

            var duration = FormatDurationShort(lastSessionSeconds);
            return string.IsNullOrEmpty(duration) ? when : $"{when}, {duration}";
        }

        /// <summary>«2ч 10м» / «45м» / «" (меньше минуты) — компактная длительность сессии.</summary>
        private static string FormatDurationShort(long seconds) {
            if (seconds <= 0) {
                return string.Empty;
            }

            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1) {
                return $"{(int)ts.TotalHours}ч {ts.Minutes:00}м";
            }

            return ts.Minutes > 0 ? $"{ts.Minutes}м" : "меньше минуты";
        }

        /// <summary>Гоняет реконсиляцию ровно один раз за время жизни процесса лаунчера.</summary>
        private static void EnsureReconciled() {
            if (Interlocked.Exchange(ref reconciled, 1) == 1) {
                return;
            }

            ReconcilePending();
        }

        private static void ReconcilePending() {
            try {
                Dictionary<string, PendingSession> pending;
                lock (FileLock) {
                    pending = LoadPendingLocked();
                }

                if (pending.Count == 0) {
                    return;
                }

                foreach (var kv in new List<KeyValuePair<string, PendingSession>>(pending)) {
                    var session = kv.Value;

                    Process? proc = TryGetSameProcess(session);
                    if (proc != null && !SafeHasExited(proc)) {
                        // Игра всё ещё бежит — досмотрим до конца в этом запуске лаунчера.
                        WatchAsync(session.ProcessId, proc);
                        continue;
                    }

                    FinishSession(session.ProcessId, DateTime.UtcNow);
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.ReconcilePending: {ex.Message}");
            }
        }

        private static Process? TryGetSameProcess(PendingSession session) {
            try {
                var proc = Process.GetProcessById(session.ProcessId);
                return SafeStartTimeTicks(proc) == session.ProcessStartTimeTicks ? proc : null;
            }
            catch {
                // Процесса с таким PID уже нет
                return null;
            }
        }

        private static void WatchAsync(int processId, Process process) {
            _ = Task.Run(() => {
                try {
                    process.WaitForExit();
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"PlaytimeStore.WatchAsync(pid={processId}): {ex.Message}");
                }
                finally {
                    FinishSession(processId, DateTime.UtcNow);
                }
            });
        }

        private static void FinishSession(int processId, DateTime endUtc) {
            lock (FileLock) {
                var pending = LoadPendingLocked();
                var key = PendingKey(processId);
                if (!pending.TryGetValue(key, out var session)) {
                    return; // уже закрыта другим потоком/запуском
                }

                pending.Remove(key);
                SavePendingLocked(pending);

                var elapsed = Math.Max(0, (long)(endUtc - session.SessionStartUtc).TotalSeconds);

                var all = LoadAllLocked();
                if (!all.TryGetValue(session.GameId, out var entry)) {
                    entry = new PlaytimeEntry();
                    all[session.GameId] = entry;
                }

                entry.TotalSeconds += elapsed;
                entry.LastSessionAt = endUtc;
                entry.LastSessionSeconds = elapsed;
                SaveAllLocked(all);
            }
        }

        private static string PendingKey(int processId) => processId.ToString();

        private static long SafeStartTimeTicks(Process p) {
            try {
                return p.StartTime.Ticks;
            }
            catch {
                return 0;
            }
        }

        private static bool SafeHasExited(Process p) {
            try {
                return p.HasExited;
            }
            catch {
                return true;
            }
        }

        private static Dictionary<string, PlaytimeEntry> LoadAll() {
            lock (FileLock) {
                return LoadAllLocked();
            }
        }

        private static Dictionary<string, PlaytimeEntry> LoadAllLocked() {
            try {
                if (!File.Exists(PlaytimePath)) {
                    return new Dictionary<string, PlaytimeEntry>();
                }

                var json = File.ReadAllText(PlaytimePath);
                return JsonSerializer.Deserialize<Dictionary<string, PlaytimeEntry>>(json)
                       ?? new Dictionary<string, PlaytimeEntry>();
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.LoadAll: {ex.Message}");
                return new Dictionary<string, PlaytimeEntry>();
            }
        }

        private static void SaveAllLocked(Dictionary<string, PlaytimeEntry> data) {
            try {
                Directory.CreateDirectory(AppDir);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                WriteAllTextAtomic(PlaytimePath, json);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.SaveAll: {ex.Message}");
            }
        }

        // Пишем через временный файл + File.Move — тот же приём, что FileHashCache.PruneAndSave.
        // Без него убитый посреди File.WriteAllText процесс (закрытие игры — частое событие)
        // оставляет обрезанный JSON; LoadAllLocked ловит ошибку парсинга и тихо возвращает
        // пустой словарь — вся история наигранного времени пропадала бы бесследно.
        private static void WriteAllTextAtomic(string path, string content) {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }

        private static Dictionary<string, PendingSession> LoadPendingLocked() {
            try {
                if (!File.Exists(PendingPath)) {
                    return new Dictionary<string, PendingSession>();
                }

                var json = File.ReadAllText(PendingPath);
                return JsonSerializer.Deserialize<Dictionary<string, PendingSession>>(json)
                       ?? new Dictionary<string, PendingSession>();
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.LoadPending: {ex.Message}");
                return new Dictionary<string, PendingSession>();
            }
        }

        private static void SavePendingLocked(Dictionary<string, PendingSession> data) {
            try {
                Directory.CreateDirectory(AppDir);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                WriteAllTextAtomic(PendingPath, json);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.SavePending: {ex.Message}");
            }
        }
    }
}
