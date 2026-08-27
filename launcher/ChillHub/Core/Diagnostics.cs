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
        /// Потолок всего бандла в БАЙТАХ UTF-8.
        /// <para>
        /// Держать его согласованным с сервером ОБЯЗАТЕЛЬНО: там стоит
        /// <c>feedback.MaxLogBytes</c> (столько же) и <c>MaxBodyBytes</c> = вдвое больше плюс
        /// запас, потому что JSON-экранирование лога, полного переводов строк, заметно
        /// раздувает тело запроса. Если бандл превысит серверный лимит тела, запрос будет
        /// отвергнут ЦЕЛИКОМ — отчёт не обрежется, а пропадёт. Третье звено — nginx
        /// (client_max_body_size для /feedback/submit), он должен пропускать больше сервера.
        /// </para>
        /// </summary>
        private const int BundleMaxBytes = 1024 * 1024;

        /// <summary>
        /// Суммарный бюджет на содержимое логов внутри бандла.
        /// <para>
        /// Поднят со 160 КиБ: к журналам лаунчера добавился журнал загрузчика модов, а он
        /// один разговорчивее всего остального вместе взятого. При прежнем бюджете хвост
        /// BepInEx вытеснял из отчёта записи самого лаунчера — то есть ровно тот кусок, по
        /// которому видно, что лаунчер вообще делал перед падением.
        /// </para>
        /// </summary>
        private const int LogsTotalBudgetBytes = 512 * 1024;

        /// <summary>
        /// Потолок хвоста одного файла лога. Поднят с 48 КиБ: 48 КиБ — это меньше минуты
        /// работы BepInEx с полусотней плагинов, и стек падения оказывался выше отрезанной
        /// границы.
        /// </summary>
        private const int LogTailBytes = 128 * 1024;

        /// <summary>
        /// Имя файла с версией установленного набора модов (лежит в папке игры).
        /// Совпадает с меткой, которую бережёт синхронизация (<c>IntegrityChecker</c>):
        /// диагностика читает ровно те файлы, что кладёт установка модов.
        /// </summary>
        private const string ModsVersionFileName = ".mods.version";

        /// <summary>Имя манифеста набора модов. В бандл идут только имя и размер: внутри перечень всех файлов набора.</summary>
        private const string ModsManifestFileName = ".mods.manifest.json";

        /// <summary>Конфиг доорстопа: им игра подхватывает загрузчик модов. Мал и целиком идёт в бандл.</summary>
        private const string DoorstopConfigFileName = "doorstop_config.ini";

        /// <summary>Папка загрузчика модов внутри папки игры.</summary>
        private const string BepInExDirName = "BepInEx";

        /// <summary>Главный журнал загрузчика модов: без него разбирать вылеты нечем.</summary>
        private const string BepInExLogFileName = "LogOutput.log";

        /// <summary>
        /// Потолок хвоста журнала модов. Меньше общего <see cref="LogTailBytes"/> намеренно:
        /// бюджет у секций общий, а модифицированных игр у пользователя может быть несколько,
        /// и одна из них не должна забрать весь бандл у остальных и у логов лаунчера.
        /// </summary>
        private const int ModsLogTailBytes = 48 * 1024;

        /// <summary>Сколько модифицированных игр разбираем: дальше начинается пересказ всей библиотеки.</summary>
        private const int ModsMaxGames = 6;

        /// <summary>Потолок мелкого файла модов (версия, doorstop_config.ini), который идёт целиком.</summary>
        private const int ModsSmallFileMaxBytes = 8 * 1024;

        /// <summary>
        /// Глубина обхода папки BepInEx. Двух уровней хватает, чтобы увидеть и разделы
        /// (plugins, patchers, config), и что в них лежит; глубже начинаются ресурсы модов.
        /// </summary>
        private const int ModsTreeMaxDepth = 2;

        /// <summary>Сколько строк дерева BepInEx максимум попадает в бандл.</summary>
        private const int ModsTreeMaxEntries = 120;

        /// <summary>
        /// Глубина обхода папки игр. Для разбора достаточно увидеть, какие игры установлены
        /// и есть ли у них подпапки: глубина 10 выкладывала наружу всё дерево пользователя
        /// целиком, а пользы не добавляла.
        /// </summary>
        private const int GamesTreeMaxDepth = 2;

        /// <summary>Сколько строк дерева папки игр максимум попадает в бандл.</summary>
        private const int GamesTreeMaxEntries = 200;

        /// <summary>Общий бюджет на все секции логов: чтобы один болтливый файл не съел весь бандл.</summary>
        private sealed class LogBudget {
            public int Remaining { get; set; } = LogsTotalBudgetBytes;
        }

        public sealed record DiagnosticsBundle(string LogsMarkdown, Dictionary<string, string> SystemHints);

        public static DiagnosticsBundle Build() {
            var sb = new StringBuilder(32 * 1024);
            var hints = new Dictionary<string, string>();
            try {
                sb.AppendLine("# Chill Hub Diagnostics Bundle");
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

                // Общий бюджет на всё, что читается с диска как текст. Секции тратят его по
                // порядку следования, поэтому порядок здесь — это порядок важности.
                var budget = new LogBudget();

                // Моды живут ВНУТРИ папки игры: лаунчер их только раскладывает, дальше игру
                // грузит doorstop, а плагины — BepInEx. Когда игра падает на старте, разбирать
                // нечего без четырёх вещей: какой набор модов стоит (.mods.version), из чего он
                // собран (манифест), чем его грузят (doorstop_config.ini) и что сказал сам
                // загрузчик (LogOutput.log). Секция стоит ВЫШЕ логов лаунчера намеренно: логи
                // лаунчера объёмнее и при общем бюджете вытеснили бы её целиком.
                sb.AppendLine("## Mods");
                try {
                    AppendModsSections(sb, ChillHub.Core.ConfigService.Current.GamesPath, budget);
                }
                catch (Exception ex) { sb.AppendLine($"(mods error: {ex.Message})"); }
                sb.AppendLine();

                // Логи клиента: одна секция вместо прежних «Logs» + «Temp Logs».
                // Путь берём у Logger, чтобы не разъезжаться с ним при переездах каталога.
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

            return new DiagnosticsBundle(TrimToBudget(Redact(sb.ToString()), BundleMaxBytes), redactedHints);
        }

        /// <summary>
        /// Убирает из текста имя пользователя Windows: сначала полный путь к профилю,
        /// затем само имя (оно встречается и в путях вида C:\Users\ivan\...).
        /// Публичный, потому что редактировать нужно не только бандл диагностики:
        /// текст исключения в авто-отчёте тоже полон путей с именем пользователя.
        /// </summary>
        /// <param name="text">Исходный текст.</param>
        /// <returns>Текст без имени пользователя Windows.</returns>
        public static string Redact(string text) {
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

        /// <summary>
        /// Приводит текст к бюджету в байтах UTF-8, вырезая СЕРЕДИНУ.
        /// <para>
        /// Обрезать хвост нельзя: там самые свежие записи лога, ради которых отчёт и
        /// собирают. Обрезать начало тоже нельзя: там конфигурация и версии, без которых
        /// непонятно, на чём воспроизводить. Поэтому оставляем оба края и явно помечаем
        /// вырезанное — молчаливая потеря куска хуже, чем видимая.
        /// </para>
        /// <para>
        /// Отдельные разделы уже ограничены своими бюджетами (см. LogsTotalBudgetBytes и
        /// соседние), так что этот потолок — страховка на случай, когда их сумма всё равно
        /// выходит за предел.
        /// </para>
        /// </summary>
        /// <param name="text">Исходный текст бандла.</param>
        /// <param name="maxBytes">Бюджет в байтах UTF-8.</param>
        /// <returns>Текст, укладывающийся в бюджет.</returns>
        internal static string TrimToBudget(string text, int maxBytes) {
            if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes) {
                return text;
            }

            var marker = $"{Environment.NewLine}{Environment.NewLine}... середина вырезана: бандл не помещался в {maxBytes / 1024} КиБ ...{Environment.NewLine}{Environment.NewLine}";
            var budget = maxBytes - Encoding.UTF8.GetByteCount(marker);
            if (budget <= 0) {
                return marker;
            }

            // Начало — контекст, хвост — свежие события; хвосту отдаём больше.
            var headBudget = budget / 3;
            var tailBudget = budget - headBudget;

            var head = TakeBytesFromStart(text, headBudget);
            var tail = TakeBytesFromEnd(text, tailBudget);
            return head + marker + tail;
        }

        /// <summary>Берёт префикс, укладывающийся в бюджет байт, не разрывая суррогатные пары.</summary>
        /// <param name="text">Текст.</param>
        /// <param name="maxBytes">Бюджет в байтах.</param>
        /// <returns>Префикс.</returns>
        private static string TakeBytesFromStart(string text, int maxBytes) {
            var count = 0;
            for (var i = 0; i < text.Length;) {
                var step = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                var size = Encoding.UTF8.GetByteCount(text.Substring(i, step));
                if (count + size > maxBytes) {
                    return text.Substring(0, i);
                }

                count += size;
                i += step;
            }

            return text;
        }

        /// <summary>Берёт суффикс, укладывающийся в бюджет байт, не разрывая суррогатные пары.</summary>
        /// <param name="text">Текст.</param>
        /// <param name="maxBytes">Бюджет в байтах.</param>
        /// <returns>Суффикс.</returns>
        private static string TakeBytesFromEnd(string text, int maxBytes) {
            var count = 0;
            for (var i = text.Length; i > 0;) {
                var step = char.IsLowSurrogate(text[i - 1]) && i - 2 >= 0 ? 2 : 1;
                var size = Encoding.UTF8.GetByteCount(text.Substring(i - step, step));
                if (count + size > maxBytes) {
                    return text.Substring(i);
                }

                count += size;
                i -= step;
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

        /// <summary>
        /// Пишет секцию «## Mods»: по одной подсекции на каждую игру со следами модов.
        /// Ванильные игры пропускаются — их папки уже перечислены в дереве выше, и повторять
        /// их здесь значит выкладывать наружу лишнее и тратить бюджет впустую.
        /// </summary>
        /// <param name="sb">Текст бандла.</param>
        /// <param name="gamesRoot">Папка игр из конфига.</param>
        /// <param name="budget">Общий бюджет на содержимое файлов.</param>
        private static void AppendModsSections(StringBuilder sb, string gamesRoot, LogBudget budget) {
            if (string.IsNullOrWhiteSpace(gamesRoot) || !Directory.Exists(gamesRoot)) {
                sb.AppendLine("(games root not found)");
                return;
            }

            string[] gameDirs;
            try {
                gameDirs = Directory.GetDirectories(gamesRoot);
            }
            catch (Exception ex) {
                sb.AppendLine($"(games listing error: {ex.Message})");
                return;
            }

            // Порядок обхода файловой системы не гарантирован, а бандлы разных запусков
            // сравнивают глазами: стабильная сортировка экономит время при разборе.
            Array.Sort(gameDirs, StringComparer.OrdinalIgnoreCase);

            var reported = 0;
            foreach (var gameDir in gameDirs) {
                if (!HasModTraces(gameDir)) {
                    continue;
                }

                if (reported >= ModsMaxGames) {
                    sb.AppendLine($"(limit reached: {ModsMaxGames} modded games)");
                    break;
                }

                reported++;
                AppendGameMods(sb, gameDir, budget);
            }

            if (reported == 0) {
                sb.AppendLine("(no modded games found)");
            }
        }

        /// <summary>
        /// Есть ли в папке игры хоть один след модов. Достаточно одного: набор бывает
        /// снесён наполовину — например, файлы BepInEx остались, а метка версии пропала, —
        /// и именно такие половинчатые установки и приходят в жалобах.
        /// </summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <returns><see langword="true"/>, если моды ставились.</returns>
        private static bool HasModTraces(string gameDir) {
            try {
                return File.Exists(Path.Combine(gameDir, ModsVersionFileName))
                    || File.Exists(Path.Combine(gameDir, ModsManifestFileName))
                    || File.Exists(Path.Combine(gameDir, DoorstopConfigFileName))
                    || Directory.Exists(Path.Combine(gameDir, BepInExDirName));
            }
            catch {
                return false;
            }
        }

        /// <summary>Пишет состояние модов одной игры.</summary>
        /// <param name="sb">Текст бандла.</param>
        /// <param name="gameDir">Папка игры.</param>
        /// <param name="budget">Общий бюджет на содержимое файлов.</param>
        private static void AppendGameMods(StringBuilder sb, string gameDir, LogBudget budget) {
            var name = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            sb.AppendLine($"### {name}");

            // Метка версии набора: одна строка, идёт целиком.
            AppendModsSmallFile(sb, Path.Combine(gameDir, ModsVersionFileName), ModsVersionFileName, "text", budget);

            // Манифест — только имя и размер. Внутри перечень всех файлов набора с хешами:
            // он не помещается в бандл и для разбора не нужен, а вот его отсутствие или
            // подозрительный размер — уже симптом.
            sb.AppendLine($"#### {ModsManifestFileName}");
            try {
                var manifest = Path.Combine(gameDir, ModsManifestFileName);
                if (File.Exists(manifest)) {
                    sb.AppendLine($"- {ModsManifestFileName} [size={new FileInfo(manifest).Length} bytes]");
                }
                else {
                    sb.AppendLine("(not found)");
                }
            }
            catch (Exception ex) { sb.AppendLine($"(manifest error: {ex.Message})"); }

            // doorstop_config.ini: правится руками чаще всего остального, и «моды не
            // подхватываются» обычно объясняется именно им (enabled=false, чужой путь).
            AppendModsSmallFile(sb, Path.Combine(gameDir, DoorstopConfigFileName), DoorstopConfigFileName, "ini", budget);

            var bepInEx = Path.Combine(gameDir, BepInExDirName);
            sb.AppendLine($"#### {BepInExDirName} (tree, depth={ModsTreeMaxDepth})");
            if (Directory.Exists(bepInEx)) {
                AppendModsTree(sb, bepInEx);
            }
            else {
                sb.AppendLine("(not found)");
            }

            sb.AppendLine($"#### {BepInExDirName}/{BepInExLogFileName}");
            var modsLog = Path.Combine(bepInEx, BepInExLogFileName);
            if (File.Exists(modsLog)) {
                AppendFileTail(sb, modsLog, ModsLogTailBytes, budget);
            }
            else {
                sb.AppendLine("(not found)");
            }
        }

        /// <summary>Пишет мелкий файл модов целиком (при неожиданном размере — его хвост).</summary>
        /// <param name="sb">Текст бандла.</param>
        /// <param name="path">Путь к файлу.</param>
        /// <param name="title">Заголовок подсекции.</param>
        /// <param name="fence">Язык блока кода в markdown.</param>
        /// <param name="budget">Общий бюджет на содержимое файлов.</param>
        private static void AppendModsSmallFile(StringBuilder sb, string path, string title, string fence, LogBudget budget) {
            sb.AppendLine($"#### {title}");
            if (!File.Exists(path)) {
                sb.AppendLine("(not found)");
                return;
            }

            AppendFileTail(sb, path, ModsSmallFileMaxBytes, budget, fence);
        }

        /// <summary>
        /// Дерево папки BepInEx: и папки, и файлы. Файлы здесь важнее папок — по именам
        /// dll видно, какие плагины стоят и не задвоился ли один из них.
        /// </summary>
        /// <param name="sb">Текст бандла.</param>
        /// <param name="root">Папка BepInEx.</param>
        private static void AppendModsTree(StringBuilder sb, string root) {
            var emitted = 0;
            var stopped = false;

            void Walk(string dir, int depth) {
                // Ровно ModsTreeMaxDepth уровней имён: BepInEx/plugins и BepInEx/plugins/*.dll.
                if (depth >= ModsTreeMaxDepth || stopped) {
                    return;
                }

                var indent = "  " + new string(' ', depth * 2);
                foreach (var d in SafeGetDirs(dir)) {
                    if (Stop()) {
                        return;
                    }

                    sb.AppendLine(indent + "- " + Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/");
                    emitted++;
                    Walk(d, depth + 1);
                    if (stopped) {
                        return;
                    }
                }

                foreach (var f in SafeGetFiles(dir)) {
                    if (Stop()) {
                        return;
                    }

                    long size;
                    try { size = new FileInfo(f).Length; }
                    catch { size = -1; }
                    sb.AppendLine(indent + "- " + Path.GetFileName(f) + (size >= 0 ? $" [{size} bytes]" : string.Empty));
                    emitted++;
                }
            }

            Walk(root, 0);
            if (emitted == 0) {
                sb.AppendLine("(empty)");
            }

            bool Stop() {
                if (emitted < ModsTreeMaxEntries) {
                    return false;
                }

                if (!stopped) {
                    stopped = true;
                    sb.AppendLine($"(limit reached: {ModsTreeMaxEntries} entries)");
                }

                return true;
            }

            static IEnumerable<string> SafeGetDirs(string p) { try { return Directory.GetDirectories(p); } catch { return Array.Empty<string>(); } }
            static IEnumerable<string> SafeGetFiles(string p) { try { return Directory.GetFiles(p); } catch { return Array.Empty<string>(); } }
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

        /// <summary>
        /// Открывает файл на чтение, не мешая тому, кто его уже пишет.
        /// <para>
        /// <c>File.OpenRead</c> и <c>File.ReadAllBytes</c> просят <see cref="FileShare.Read"/>,
        /// то есть запрещают писать в файл всем остальным. Логгер держит client.log открытым
        /// на запись (иначе строка стоила бы открытия файла), и такое чтение падало бы с
        /// «файл занят» — молча, в catch, превращая логи в отчёте в «(read error)».
        /// </para>
        /// </summary>
        /// <param name="file">Путь к файлу.</param>
        /// <returns>Поток на чтение.</returns>
        private static FileStream OpenShared(string file)
            => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        private static string ComputeSha256(string file) {
            try {
                using var fs = OpenShared(file);
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

                    sb.AppendLine($"### {f}");
                    AppendFileTail(sb, f, maxTailBytes, budget);
                    used++;
                }
            }
            catch (Exception ex) { sb.AppendLine($"(specific logs error: {ex.Message})"); }
        }

        /// <summary>
        /// Дописывает содержимое файла (при превышении бюджета — только его хвост) и
        /// списывает потраченное с общего бюджета.
        /// <para>
        /// Не даём одному болтливому файлу съесть весь бандл: сервер режет всё, что больше
        /// feedbackMaxLogBytes, и обрезка была бы молчаливой. Хвост, а не начало: момент
        /// отказа всегда в конце файла.
        /// </para>
        /// <para>
        /// Заголовок пишет вызывающий: у журналов лаунчера это полный путь, у секции модов —
        /// имя файла внутри игры.
        /// </para>
        /// </summary>
        /// <param name="sb">Текст бандла.</param>
        /// <param name="file">Путь к файлу.</param>
        /// <param name="maxTailBytes">Потолок на этот файл.</param>
        /// <param name="budget">Общий бюджет на содержимое файлов.</param>
        /// <param name="fence">Язык блока кода в markdown.</param>
        private static void AppendFileTail(StringBuilder sb, string file, int maxTailBytes, LogBudget budget, string fence = "log") {
            if (budget.Remaining <= 0) {
                sb.AppendLine("(log budget exhausted; file omitted)");
                return;
            }

            var allowance = Math.Min(maxTailBytes, budget.Remaining);
            try {
                byte[] bytes;
                bool trimmed;
                using (var fs = OpenShared(file)) {
                    // Читаем сразу хвост, а не файл целиком: журнал загрузчика модов у
                    // игрока с полусотней плагинов бывает в десятки мегабайт, и прежнее
                    // чтение целиком поднимало их в память ради 48 КиБ.
                    var length = fs.Length;
                    var take = (int)Math.Min(length, allowance);
                    trimmed = length > take;
                    if (trimmed) {
                        fs.Seek(length - take, SeekOrigin.Begin);
                    }

                    bytes = new byte[take];
                    fs.ReadExactly(bytes);
                }

                sb.AppendLine("```" + fence);
                sb.AppendLine(Encoding.UTF8.GetString(bytes));
                if (trimmed) {
                    sb.AppendLine("```\n(tail only)");
                }
                else {
                    sb.AppendLine("```");
                }

                budget.Remaining -= bytes.Length;
            }
            catch (Exception ex) { sb.AppendLine($"(read error: {ex.Message})"); }
        }
    }
}
