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
    using ChillHub.Core.Metrics;

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

        /// <summary>
        /// Папка, в которой на время этой сессии включены моды; null — запуск без модов.
        /// <para>
        /// Срок жизни включённого загрузчика — это и есть срок сессии: пока игра идёт, моды
        /// нужны, а как только она закрылась, папку возвращают в состояние без модов. Иначе
        /// следующий запуск ИЗ STEAM, мимо лаунчера, молча поднял бы моды: игре всё равно,
        /// кто её стартовал, — winhttp.dll грузится сам (см. Mods.DoorstopConfig).
        /// </para>
        /// <para>
        /// Поле переживает закрытие лаунчера вместе с самой записью: если он умер раньше
        /// игры, папку вернёт реконсиляция при следующем запуске.
        /// </para>
        /// </summary>
        public string? ModdedDir { get; set; }
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
        private static readonly string DefaultAppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");

        /// <summary>
        /// Подменённый на время теста каталог. AsyncLocal, а не обычное поле: прогон идёт
        /// параллельными классами, и подмена в одном не должна уводить файлы у другого —
        /// тот же приём, что у очереди обратной связи.
        /// </summary>
        private static readonly AsyncLocal<string?> ScopedAppDir = new AsyncLocal<string?>();

        private static readonly object FileLock = new object();

        private static int reconciled;

        private static string AppDir => ScopedAppDir.Value ?? DefaultAppDir;

        private static string PlaytimePath => Path.Combine(AppDir, "playtime.json");

        private static string PendingPath => Path.Combine(AppDir, "playtime.sessions.json");

        /// <summary>
        /// Уводит файлы наигранного времени в отдельный каталог — для тестов.
        /// <para>
        /// Без шва проверить нечего: и подсчёт времени, и выключение модов после сессии
        /// живут в файлах, а трогать в прогоне настоящий %APPDATA% пользователя нельзя.
        /// </para>
        /// </summary>
        /// <param name="dir">Каталог, играющий роль %APPDATA%\ChillHub.</param>
        /// <returns>Объект, возвращающий файлы на настоящее место.</returns>
        internal static IDisposable OverrideDirForTests(string dir) => new AppDirOverride(dir);

        /// <summary>Закрывает сессию так же, как это делает выход процесса игры, — для тестов.</summary>
        /// <param name="processId">Номер процесса, под которым сессия заводилась.</param>
        /// <param name="endUtc">Момент окончания.</param>
        internal static void FinishForTests(int processId, DateTime endUtc) => FinishSession(processId, endUtc);

        /// <summary>Забывает отметку о проделанной реконсиляции — для тестов.</summary>
        internal static void ResetForTests() => Interlocked.Exchange(ref reconciled, 0);

        /// <summary>
        /// Подбирает незакрытые сессии прошлого запуска: досматривает те игры, что ещё
        /// бегут, и закрывает остальные — а вместе с ними выключает моды в папках, где их
        /// включал прошлый запуск лаунчера.
        /// <para>
        /// Вызывается на старте главной страницы явно, а не по первому обращению за
        /// цифрами: возврат чужой папки в состояние без модов не должен зависеть от того,
        /// открыл ли кто-то витрину с игрой.
        /// </para>
        /// </summary>
        internal static void EnsureStarted() => EnsureReconciled();

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
        /// <param name="gameId">Игра.</param>
        /// <param name="process">Процесс игры — именно игры, а не Steam (см. GameProcessFinder).</param>
        /// <param name="moddedDir">
        /// Папка, в которой перед запуском включили моды; null — запуск без модов. По
        /// окончании сессии моды в ней выключаются обратно.
        /// </param>
        internal static void BeginSession(string gameId, Process process, string? moddedDir = null) {
            if (string.IsNullOrWhiteSpace(gameId) || process == null) {
                return;
            }

            EnsureReconciled();

            try {
                lock (FileLock) {
                    var pending = LoadPendingLocked();

                    // Один и тот же процесс могли найти дважды: через Steam игру ищут
                    // ожиданием, и два подряд нажатия «Играть» дают два поиска на одну
                    // игру. Перезапись сдвинула бы начало сессии вперёд и потеряла первые
                    // минуты, поэтому побеждает та запись, что появилась раньше.
                    if (pending.ContainsKey(PendingKey(process.Id))) {
                        return;
                    }

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
                        ModdedDir = string.IsNullOrWhiteSpace(moddedDir) ? null : moddedDir,
                    };
                    SavePendingLocked(pending);
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.BeginSession({gameId}): {ex.Message}");
            }

            WatchAsync(process.Id, process);
        }

        /// <summary>
        /// «142 ч» — суммарное время, округлённое вниз до часа; меньше часа — «25 мин».
        /// Раньше первые запуски давали «0 ч в игре» — цифра, которая выглядит как отсутствие
        /// данных, а не как двадцать минут игры.
        /// </summary>
        internal static string FormatTotal(long totalSeconds) {
            var hours = totalSeconds / 3600;
            if (hours > 0) {
                return $"{hours} ч";
            }

            var minutes = Math.Max(1, totalSeconds / 60);
            return $"{minutes} мин";
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
            PendingSession session;
            lock (FileLock) {
                var pending = LoadPendingLocked();
                var key = PendingKey(processId);
                if (!pending.TryGetValue(key, out session!)) {
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

                // Метрика не участвует в блокировке файла: MetricsService.Report сам
                // уходит в фоновую задачу и ничего не бросает при недоступном сервере.
                MetricsService.GameSession(session.GameId, elapsed * 1000);
            }

            // Моды выключаются ПОСЛЕ снятия блокировки файлов: это запись в чужую папку
            // игры, и держать ради неё замок над playtime.json незачем.
            if (!string.IsNullOrWhiteSpace(session.ModdedDir)) {
                Mods.DoorstopConfig.SetEnabled(session.ModdedDir, false);
                Logging.Logger.Info($"[mods] сессия окончена, моды в '{session.ModdedDir}' выключены");
            }
        }

        private static string PendingKey(int processId) => processId.ToString();

        /// <summary>Возвращает файлы на настоящее место после <see cref="OverrideDirForTests"/>.</summary>
        private sealed class AppDirOverride : IDisposable {
            private readonly string? previous;

            internal AppDirOverride(string dir) {
                this.previous = ScopedAppDir.Value;
                ScopedAppDir.Value = dir;
            }

            public void Dispose() => ScopedAppDir.Value = this.previous;
        }

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
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                // Атомарная запись — тот же ChillHub.Update.AtomicFile, которым уже пользуется
                // самообновление для launcher.version (и теперь FileHashCache.PruneAndSave). Без
                // неё убитый посреди записи процесс (закрытие игры — частое событие) оставляет
                // обрезанный JSON; LoadAllLocked ловит ошибку парсинга и тихо возвращает пустой
                // словарь — вся история наигранного времени пропадала бы бесследно.
                ChillHub.Update.AtomicFile.WriteAllText(PlaytimePath, json, Core.SelfUpdate.SelfUpdateRules.Utf8NoBom);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.SaveAll: {ex.Message}");
            }
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
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                ChillHub.Update.AtomicFile.WriteAllText(PendingPath, json, Core.SelfUpdate.SelfUpdateRules.Utf8NoBom);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"PlaytimeStore.SavePending: {ex.Message}");
            }
        }
    }
}
