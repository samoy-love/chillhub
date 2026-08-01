using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using ChillHub.Update;

internal static class Program
{
    // Коды возврата: 0 — успех, 2 — были ошибки копирования (обновление НЕ применено полностью), 3 — фатальная ошибка.
    private const int ExitOk = 0;
    private const int ExitCopyErrors = 2;
    private const int ExitFatal = 3;

    // Все служебные списки пишем в UTF-8 БЕЗ BOM: BOM ломает сверку размера/хеша
    // (например, launcher.version становится 10 байт вместо 8).
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public static async Task<int> Main(string[] args)
    {
        // Simple args parser: expects --key value pairs
        static Dictionary<string, string?> ParseArgs(string[] a)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < a.Length; i++)
            {
                var tok = a[i];
                if (!tok.StartsWith("--"))
                {
                    continue;
                }
                var key = tok;
                string? val = null;
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) { val = a[++i]; }
                dict[key] = val;
            }
            return dict;
        }

        var argsMap = ParseArgs(args);
        string Req(string key)
        {
            if (!argsMap.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
            {
                throw new ArgumentException($"Missing required option {key}");
            }
            return v!;
        }
        string Opt(string key, string def = "") => (argsMap.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) ? v! : def;

        var src = Req("--src");
        var dst = Req("--dst");
        var exe = Req("--exe");
        var parentStr = Opt("--parent", "0");
        _ = int.TryParse(parentStr, out var parent);
        var log = Req("--log");
        var files = Opt("--files", string.Empty);
        var dirs = Opt("--dirs", string.Empty);
        var del = Opt("--del", string.Empty);
        var strip = Opt("--strip-prefix", string.Empty);
        // --auto-strip false отключает автоопределение корневой папки архива:
        // лаунчер считает strip-prefix сам по манифесту и передаёт его явно (см. A10).
        var autoStrip = !string.Equals(Opt("--auto-strip", "true"), "false", StringComparison.OrdinalIgnoreCase);
        var preserve = Opt("--preserve", PreserveMatcher.DefaultRulesArg);
        var newVersion = Opt("--version", string.Empty);

        Directory.CreateDirectory(Path.GetDirectoryName(log) ?? Path.GetTempPath());
        void Log(string msg)
        {
            try { File.AppendAllText(log, $"[{DateTime.Now:O}] {msg}\r\n", Utf8NoBom); } catch { }
        }

        var copyErrors = 0;
        var copyOk = 0;

        try
        {
                // Log all options and basic file stats
                string ExistsStat(string? p) => string.IsNullOrWhiteSpace(p) ? "<null>" : ($"'{p}' exists={(File.Exists(p) ? "file" : Directory.Exists(p) ? "dir" : "no")} ");
                Log($"Updater start\n  --src={ExistsStat(src)}\n  --dst={ExistsStat(dst)}\n  --exe={ExistsStat(exe)}\n  --parent={parent}\n  --log='{log}'\n  --files={ExistsStat(files)}\n  --dirs={ExistsStat(dirs)}\n  --del={ExistsStat(del)}\n  --strip-prefix='{strip}'\n  --auto-strip={autoStrip}\n  --preserve='{preserve}'");
                // Wait parent
                if (parent > 0)
                {
                    try { var proc = Process.GetProcessById(parent); proc.WaitForExit(); Log($"Parent {parent} exited"); } catch { }
                }
                // Ensure dst
                try { Directory.CreateDirectory(dst); } catch { }

                // Detect strip prefix if not provided (только если автоопределение разрешено)
                if (string.IsNullOrWhiteSpace(strip) && autoStrip)
                {
                    try
                    {
                        // Prefer detection from FILES list if present: require a single shared top-level segment
                        string? detected = null;
                        if (!string.IsNullOrWhiteSpace(files) && File.Exists(files))
                        {
                            var lines = File.ReadAllLines(files, Encoding.UTF8)
                                .Select(l => (l ?? string.Empty).Replace('\\','/').Trim('/'))
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToArray();
                            var firstSegs = lines
                                .Select(l => l.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            if (firstSegs.Length == 1)
                            {
                                var candidate = firstSegs[0];
                                var allHave = lines.All(l => l.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase));
                                if (allHave && Directory.Exists(Path.Combine(src, candidate)))
                                {
                                    detected = candidate;
                                }
                            }
                        }
                        // Fallback: top-level of SRC has exactly one directory and no files
                        if (detected == null && Directory.Exists(src))
                        {
                            var topFiles = Directory.EnumerateFiles(src, "*", SearchOption.TopDirectoryOnly).Any();
                            var topDirs = Directory.EnumerateDirectories(src, "*", SearchOption.TopDirectoryOnly).ToArray();
                            if (!topFiles && topDirs.Length == 1)
                            {
                                detected = Path.GetFileName(topDirs[0]);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(detected))
                        {
                            strip = detected!;
                        }
                    }
                    catch { }
                }
                Log($"effective strip-prefix='{strip}'");

                // Пути из списков — данные, а не команды. Апдейтер пишет в папку УСТАНОВКИ
                // и работает с правами пользователя (а после UAC — и выше), поэтому запись
                // по пути с ".." или "C:\..." уводит файл куда угодно: в автозагрузку,
                // в System32, в чужой профиль. Проверяем ВСЕ списки до первой операции
                // и отказываемся целиком: частично применённое обновление хуже неприменённого.
                if (!ValidateLists(new[] { files, dirs, del }, strip, Log))
                {
                    Log("FATAL: списки содержат небезопасные пути, обновление не применялось");
                    return ExitFatal;
                }

                // Preserve rules: единый матчер, общий с лаунчером (ChillHub.Update.PreserveMatcher)
                var matcher = new PreserveMatcher(preserve);
                try { Log($"preserve rules: [{string.Join(", ", matcher.Rules)}]"); } catch { }

                bool ShouldPreserve(string rel, string reason)
                    => matcher.ShouldPreserve(rel, m => Log($"skip {reason}: {m}"));

                // Log lists content if provided
                try
                {
                    if (!string.IsNullOrWhiteSpace(files))
                    {
                        if (File.Exists(files))
                        {
                            var lines = File.ReadAllLines(files, Encoding.UTF8);
                            Log($"FILES list: path='{files}', count={lines.Length}");
                            foreach (var l in lines)
                            {
                                Log($"  FILE: {l}");
                            }
                        }
                        else
                        {
                            Log($"FILES list missing: '{files}'");
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(dirs))
                    {
                        if (File.Exists(dirs))
                        {
                            var lines = File.ReadAllLines(dirs, Encoding.UTF8);
                            Log($"DIRS list: path='{dirs}', count={lines.Length}");
                            foreach (var l in lines)
                            {
                                Log($"  DIR: {l}");
                            }
                        }
                        else
                        {
                            Log($"DIRS list missing: '{dirs}'");
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(del))
                    {
                        if (File.Exists(del))
                        {
                            var lines = File.ReadAllLines(del, Encoding.UTF8);
                            Log($"DEL list: path='{del}', count={lines.Length}");
                            foreach (var l in lines)
                            {
                                Log($"  DEL: {l}");
                            }
                        }
                        else
                        {
                            Log($"DEL list missing: '{del}'");
                        }
                    }
                }
                catch (Exception ex) { Log($"lists log error: {ex.Message}"); }

                // Copy function. Возвращает true при успехе; неудачи считаем — они влияют на exit code (A7).
                async Task<bool> CopyFileAsync(string sourceFile, string destFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    if (File.Exists(destFile))
                    {
                        try { var fi = new FileInfo(destFile); fi.IsReadOnly = false; } catch { }
                    }
                    const int maxAttempts = 10;
                    var attempt = 0;
                    while (true)
                    {
                        try
                        {
                            using (var srcFs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var dstFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                            {
                                await srcFs.CopyToAsync(dstFs);
                            }
                            copyOk++;
                            return true;
                        }
                        catch (Exception ex)
                        {
                            attempt++;
                            if (attempt >= maxAttempts)
                            {
                                copyErrors++;
                                Log($"copy FAILED (giving up after {maxAttempts}) {sourceFile} -> {destFile}: {ex.Message}");
                                return false;
                            }
                            var delay = Math.Min(5000, 200 * (int)Math.Pow(2, Math.Max(0, attempt - 1)));
                            Log($"copy retry {attempt}/{maxAttempts} {sourceFile}: {ex.Message}; {delay}ms");
                            await Task.Delay(delay);
                        }
                    }
                }

                // A12. Диффовый режим: лаунчер посчитал план против ПАПКИ УСТАНОВКИ и скачал
                // только изменившиеся файлы. Значит SRC — это не полный пакет, а дифф,
                // и «остаточное зеркалирование» всего SRC больше не нужно (и вредно: оно
                // делало полный проход по несуществующим файлам).
                var haveFileList = !string.IsNullOrWhiteSpace(files) && File.Exists(files);
                var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // If file list provided, copy them first (diff), respecting strip-prefix
                if (haveFileList)
                {
                    foreach (var rel in File.ReadAllLines(files, Encoding.UTF8))
                    {
                        var clean = (rel ?? string.Empty).Replace('\\','/').Trim('/');
                        if (string.IsNullOrWhiteSpace(clean))
                        {
                            continue;
                        }
                        if (ShouldPreserve(clean, "copy")) { continue; }
                        if (PreserveMatcher.IsUpdaterArtifact(clean)) { Log($"skip copy updater artifact {clean}"); continue; }
                        var srcRel = clean;
                        var dstRel = string.IsNullOrWhiteSpace(strip) ? clean : clean.StartsWith(strip + "/", StringComparison.OrdinalIgnoreCase) ? clean.Substring(strip.Length + 1) : clean;
                        var s = ManifestPath.Combine(src, srcRel);
                        var d = ManifestPath.Combine(dst, dstRel);
                        if (!File.Exists(s))
                        {
                            Log($"diff src missing {srcRel}");
                            continue;
                        }
                        copied.Add(srcRel);
                        await CopyFileAsync(s, d);
                    }
                }

                // Residual mirror of all SRC files (ensures runtimes/, prereqs/ etc.).
                // Только для полного пакета (список файлов не передан): при диффе SRC содержит
                // ровно то, что надо скопировать, и оно уже скопировано выше.
                if (!haveFileList && Directory.Exists(src))
                {
                    foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(src, s).Replace('\\','/');
                        if (matcher.ShouldPreserve(rel)) { continue; }
                        // Служебные файлы апдейтера в папку установки не переносим никогда (A6).
                        if (PreserveMatcher.IsUpdaterArtifact(rel)) { Log($"mirror skip updater artifact {rel}"); continue; }
                        var dstRel = string.IsNullOrWhiteSpace(strip) ? rel : rel.StartsWith(strip + "/", StringComparison.OrdinalIgnoreCase) ? rel.Substring(strip.Length + 1) : rel;
                        var d = Path.Combine(dst, dstRel.Replace('/', Path.DirectorySeparatorChar));
                        // Cheap skip: same size
                        try
                        {
                            if (File.Exists(d))
                            {
                                var s1 = new FileInfo(s).Length; var s2 = new FileInfo(d).Length;
                                if (s1 == s2)
                                {
                                    continue;
                                }
                            }
                        }
                        catch { }
                        await CopyFileAsync(s, d);
                    }
                }

                // Диагностика диффа: всё, что лежит в SRC, но не попало в список копирования.
                // В норме таких файлов нет; если появились — значит лаунчер и апдейтер разошлись.
                if (haveFileList && Directory.Exists(src))
                {
                    try
                    {
                        foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                        {
                            var rel = Path.GetRelativePath(src, s).Replace('\\', '/');
                            if (copied.Contains(rel) || matcher.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel))
                            {
                                continue;
                            }
                            Log($"diff: SRC file not in FILES list, skipped: {rel}");
                        }
                    }
                    catch (Exception ex) { Log($"diff audit error: {ex.Message}"); }
                }

                // Deletions
                if (!string.IsNullOrWhiteSpace(del) && File.Exists(del))
                {
                    foreach (var rel in File.ReadAllLines(del, Encoding.UTF8))
                    {
                        var clean = (rel ?? string.Empty).Replace('\\','/').Trim('/');
                        if (string.IsNullOrWhiteSpace(clean))
                        {
                            continue;
                        }
                        if (ShouldPreserve(clean, "delete")) { continue; }
                        var delPath = ManifestPath.Combine(dst, clean);
                        try { if (File.Exists(delPath)) { var fi = new FileInfo(delPath); fi.IsReadOnly = false; File.Delete(delPath); Log($"deleted {clean}"); } } catch (Exception ex) { Log($"delete failed {clean}: {ex.Message}"); }
                    }
                }

                // Empty dirs
                if (!string.IsNullOrWhiteSpace(dirs) && File.Exists(dirs))
                {
                    foreach (var rel in File.ReadAllLines(dirs, Encoding.UTF8))
                    {
                        var clean = (rel ?? string.Empty).Replace('\\','/').Trim('/');
                        if (string.IsNullOrWhiteSpace(clean))
                        {
                            continue;
                        }
                        var p = ManifestPath.Combine(dst, clean);
                        try { Directory.CreateDirectory(p); } catch { }
                    }
                }

                // Разовая очистка уже засорённых инсталляций: служебные файлы апдейтера,
                // которые прошлые версии копировали прямо в папку установки (A6).
                CleanupUpdaterArtifacts(dst, Log);

                // Full union hash compare
                try
                {
                    var map = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var logFileName = Path.GetFileName(log);
                    bool IgnoreForHash(string rel)
                    {
                        var r = (rel ?? string.Empty).Replace('\\','/').Trim('/');
                        if (string.IsNullOrEmpty(r))
                        {
                            return true;
                        }
                        // ignore updater artifacts and logs/lists
                        if (string.Equals(Path.GetFileName(r), logFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        if (PreserveMatcher.IsUpdaterArtifact(r))
                        {
                            return true;
                        }
                        // preserve-файлы намеренно расходятся — они не участвуют в сверке
                        if (matcher.ShouldPreserve(r))
                        {
                            return true;
                        }
                        return false;
                    }
                    if (haveFileList)
                    {
                        // A12. При диффе сверять всю папку установки с SRC бессмысленно: в SRC лежат
                        // только изменившиеся файлы, остальные дали бы «SRC missing» на каждый файл.
                        // Проверяем ровно то, что должны были скопировать.
                        foreach (var rel in copied)
                        {
                            if (!IgnoreForHash(rel))
                            {
                                map.Add(rel);
                            }
                        }
                    }
                    else
                    {
                        if (Directory.Exists(src))
                        {
                            foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                            {
                                var rel = Path.GetRelativePath(src, s).Replace('\\','/');
                                if (!IgnoreForHash(rel))
                                {
                                    map.Add(rel);
                                }
                            }
                        }
                        if (Directory.Exists(dst))
                        {
                            foreach (var d in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories))
                            {
                                var rel = Path.GetRelativePath(dst, d).Replace('\\','/');
                                if (!string.IsNullOrWhiteSpace(strip) && !rel.StartsWith(strip + "/", StringComparison.OrdinalIgnoreCase))
                                {
                                    rel = strip + "/" + rel;
                                }
                                if (!IgnoreForHash(rel))
                                {
                                    map.Add(rel);
                                }
                            }
                        }
                    }
                    int ok = 0, mm = 0, missS = 0, missD = 0, total = 0;
                    foreach (var key in map)
                    {
                        total++;
                        var relSrc = key;
                        var relDst = string.IsNullOrWhiteSpace(strip) ? key : key.StartsWith(strip + "/", StringComparison.OrdinalIgnoreCase) ? key.Substring(strip.Length + 1) : key;
                        var sp = ManifestPath.Combine(src, relSrc);
                        var dp = ManifestPath.Combine(dst, relDst);
                        var se = File.Exists(sp);
                        var de = File.Exists(dp);
                        if (!se) { missS++; Log($"hash: SRC missing {relSrc}"); continue; }
                        if (!de)
                        {
                            missD++; Log($"hash: DST missing {relDst}");
                            continue;
                        }
                        var h1 = Sha256Hex(sp); var h2 = Sha256Hex(dp);
                        if (!string.IsNullOrEmpty(h1) && h1.Equals(h2, StringComparison.OrdinalIgnoreCase)) { ok++; Log($"hash ok {relDst} {h2}"); }
                        else { mm++; Log($"hash MISMATCH {relDst} src={h1} dst={h2}"); }
                    }
                    Log($"hash union summary: total={total} ok={ok} mismatch={mm} src_missing={missS} dst_missing={missD}");
                }
                catch (Exception ex) { Log($"hash union compare error: {ex.Message}"); }

                // Write version marker (if provided).
                // UTF-8 без BOM и без завершающего перевода строки — ровно как пишет installer.nsi.
                try
                {
                    if (!string.IsNullOrWhiteSpace(newVersion))
                    {
                        var marker = Path.Combine(dst, "launcher.version");
                        try { Directory.CreateDirectory(Path.GetDirectoryName(marker)!); } catch { }
                        File.WriteAllText(marker, newVersion.Trim(), Utf8NoBom);
                        Log($"wrote version marker: {marker} = '{newVersion.Trim()}'");
                    }
                }
                catch (Exception ex) { Log($"version marker write error: {ex.Message}"); }

                // Итог по копированию (A7): при ненулевом счётчике ошибок обновление применено НЕ полностью.
                if (copyErrors > 0)
                {
                    Log($"COPY SUMMARY: FAILED. ok={copyOk} errors={copyErrors}. Update was NOT applied completely; exit code {ExitCopyErrors}.");
                }
                else
                {
                    Log($"COPY SUMMARY: OK. ok={copyOk} errors=0");
                }

                // Start
                try
                {
                    await Task.Delay(150);
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = dst,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                    Log($"Start issued for {exe}");
                }
                catch (Exception ex) { Log($"start phase error: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Log($"fatal: {ex}");
            return ExitFatal;
        }

        return copyErrors > 0 ? ExitCopyErrors : ExitOk;
    }

    /// <summary>
    /// Проверяет, что все пути в переданных списках безопасны.
    /// <para>
    /// Проверяется ровно та форма пути, которая потом уходит в Path.Combine
    /// (после замены слешей и обрезки краевых) — иначе проверка и использование
    /// смотрели бы на разные строки.
    /// </para>
    /// </summary>
    /// <param name="listPaths">Пути к файлам списков (filelist/emptydirs/deletelist).</param>
    /// <param name="strip">Префикс корневой папки архива — он тоже подставляется в пути.</param>
    /// <param name="log">Логгер.</param>
    /// <returns>true, если всё безопасно.</returns>
    private static bool ValidateLists(IEnumerable<string?> listPaths, string strip, Action<string> log)
    {
        var ok = true;

        if (!string.IsNullOrWhiteSpace(strip))
        {
            var reason = ManifestPath.Describe(strip.Replace('\\', '/').Trim('/'));
            if (reason != null)
            {
                log($"REJECT strip-prefix '{strip}': {reason}");
                ok = false;
            }
        }

        foreach (var listPath in listPaths)
        {
            if (string.IsNullOrWhiteSpace(listPath) || !File.Exists(listPath))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(listPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                log($"REJECT list '{listPath}': не читается ({ex.Message})");
                return false;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var clean = (lines[i] ?? string.Empty).Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(clean))
                {
                    continue;
                }

                var reason = ManifestPath.Describe(clean);
                if (reason != null)
                {
                    log($"REJECT '{listPath}' строка {i + 1}: '{clean}' — {reason}");
                    ok = false;
                }
            }
        }

        return ok;
    }

    /// <summary>
    /// Удаляет из папки установки служебные файлы апдейтера, оставшиеся от прошлых версий
    /// (filelist.txt / deletelist.txt / emptydirs.txt / apply-update.log / apply-update.cmd и подпапку updater\).
    /// </summary>
    private static void CleanupUpdaterArtifacts(string dst, Action<string> log)
    {
        try
        {
            foreach (var name in PreserveMatcher.UpdaterArtifactFiles)
            {
                var p = Path.Combine(dst, name);
                try
                {
                    if (File.Exists(p))
                    {
                        new FileInfo(p) { IsReadOnly = false }.Refresh();
                        File.Delete(p);
                        log($"cleanup: removed stale updater artifact {name}");
                    }
                }
                catch (Exception ex) { log($"cleanup failed {name}: {ex.Message}"); }
            }

            var dir = Path.Combine(dst, PreserveMatcher.UpdaterArtifactDir);
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    log($"cleanup: removed stale updater directory '{PreserveMatcher.UpdaterArtifactDir}'");
                }
            }
            catch (Exception ex) { log($"cleanup failed dir '{PreserveMatcher.UpdaterArtifactDir}': {ex.Message}"); }
        }
        catch (Exception ex) { log($"cleanup error: {ex.Message}"); }
    }

    private static string Sha256Hex(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var buf = new byte[262144];
            int r;
            while ((r = fs.Read(buf, 0, buf.Length)) > 0)
            {
                sha.TransformBlock(buf, 0, r, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch { return string.Empty; }
    }
}
