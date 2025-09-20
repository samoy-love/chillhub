using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

internal static class Program
{
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
        var preserve = Opt("--preserve", "config.json");

        Directory.CreateDirectory(Path.GetDirectoryName(log) ?? Path.GetTempPath());
        void Log(string msg)
        {
            try { File.AppendAllText(log, $"[{DateTime.Now:O}] {msg}\r\n"); } catch { }
        }

        try
        {
                // Log all options and basic file stats
                string ExistsStat(string? p) => string.IsNullOrWhiteSpace(p) ? "<null>" : ($"'{p}' exists={(File.Exists(p) ? "file" : Directory.Exists(p) ? "dir" : "no")} ");
                Log($"Updater start\n  --src={ExistsStat(src)}\n  --dst={ExistsStat(dst)}\n  --exe={ExistsStat(exe)}\n  --parent={parent}\n  --log='{log}'\n  --files={ExistsStat(files)}\n  --dirs={ExistsStat(dirs)}\n  --del={ExistsStat(del)}\n  --strip-prefix='{strip}'\n  --preserve='{preserve}'");
                // Wait parent
                if (parent > 0)
                {
                    try { var proc = Process.GetProcessById(parent); proc.WaitForExit(); Log($"Parent {parent} exited"); } catch { }
                }
                // Ensure dst
                try { Directory.CreateDirectory(dst); } catch { }

                // Detect strip prefix if not provided
                if (string.IsNullOrWhiteSpace(strip))
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
                                if (allHave && Directory.Exists(Path.Combine(src, candidate))) detected = candidate;
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
                        if (!string.IsNullOrWhiteSpace(detected)) strip = detected!;
                    }
                    catch { }
                }
                Log($"effective strip-prefix='{strip}'");

                // Preserve rules: support directory rules (ending with '/'), exact relative paths, filename-only, and simple wildcards '*' and '?'
                var preserveRules = (preserve ?? "config.json")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Replace('\\','/').Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                try { Log($"preserve rules: [{string.Join(", ", preserveRules)}]"); } catch { }

                bool WildcardIsMatch(string text, string pattern)
                {
                    // convert simple wildcard to regex
                    var sb = new StringBuilder();
                    sb.Append('^');
                    foreach (var ch in pattern)
                    {
                        switch (ch)
                        {
                            case '*': sb.Append(".*"); break;
                            case '?': sb.Append('.'); break;
                            case '.': sb.Append("\\."); break;
                            case '\\': sb.Append("\\\\"); break;
                            case '/': sb.Append('/'); break;
                            default: sb.Append(ch); break;
                        }
                    }
                    sb.Append('$');
                    try { return System.Text.RegularExpressions.Regex.IsMatch(text, sb.ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                    catch { return false; }
                }

                bool ShouldPreserve(string rel)
                {
                    // rel must be forward-slash relative path
                    var norm = rel.Replace('\\','/').Trim('/');
                    var leaf = Path.GetFileName(norm);
                    foreach (var rule in preserveRules)
                    {
                        var r = rule;
                        if (r.EndsWith('/'))
                        {
                            // directory prefix match
                            var dir = r.Trim('/');
                            if (!string.IsNullOrEmpty(dir) && norm.StartsWith(dir + '/', StringComparison.OrdinalIgnoreCase)) { Log($"preserve (dir): {norm} by '{rule}'"); return true; }
                            if (string.IsNullOrEmpty(dir)) { Log($"preserve (root dir): {norm} by '{rule}'"); return true; }
                            continue;
                        }
                        if (r.Contains('*') || r.Contains('?'))
                        {
                            if (WildcardIsMatch(norm, r) || WildcardIsMatch(leaf, r)) { Log($"preserve (wildcard): {norm} by '{rule}'"); return true; }
                        }
                        else
                        {
                            // exact relative path or filename
                            if (norm.Equals(r, StringComparison.OrdinalIgnoreCase) || leaf.Equals(r, StringComparison.OrdinalIgnoreCase)) { Log($"preserve (exact): {norm} by '{rule}'"); return true; }
                        }
                    }
                    return false;
                }

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

                // Copy function
                async Task CopyFileAsync(string sourceFile, string destFile)
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
                            using var srcFs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var dstFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.Read);
                            await srcFs.CopyToAsync(dstFs);
                            break;
                        }
                        catch (Exception ex)
                        {
                            attempt++;
                            if (attempt >= maxAttempts) { Log($"copy failed {sourceFile} -> {destFile}: {ex.Message}"); break; }
                            var delay = Math.Min(5000, 200 * (int)Math.Pow(2, Math.Max(0, attempt - 1)));
                            Log($"copy retry {attempt}/{maxAttempts} {sourceFile}: {ex.Message}; {delay}ms");
                            await Task.Delay(delay);
                        }
                    }
                }

                // If file list provided, copy them first (diff), respecting strip-prefix
                if (!string.IsNullOrWhiteSpace(files) && File.Exists(files))
                {
                    foreach (var rel in File.ReadAllLines(files, Encoding.UTF8))
                    {
                        var clean = (rel ?? string.Empty).Replace('\\','/').Trim('/');
                        if (string.IsNullOrWhiteSpace(clean))
                        {
                            continue;
                        }
                        if (ShouldPreserve(clean)) { Log($"skip copy preserve {clean}"); continue; }
                        var srcRel = clean;
                        var dstRel = string.IsNullOrWhiteSpace(strip) ? clean : clean.StartsWith(strip + "/", StringComparison.OrdinalIgnoreCase) ? clean.Substring(strip.Length + 1) : clean;
                        var s = Path.Combine(src, srcRel.Replace('/', Path.DirectorySeparatorChar));
                        var d = Path.Combine(dst, dstRel.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(s))
                        {
                            Log($"diff src missing {srcRel}");
                            continue;
                        }
                        await CopyFileAsync(s, d);
                    }
                }

                // Residual mirror of all SRC files (ensures runtimes/, prereqs/ etc.)
                if (Directory.Exists(src))
                {
                    foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(src, s).Replace('\\','/');
                        if (ShouldPreserve(rel)) { continue; }
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
                        if (ShouldPreserve(clean)) { Log($"skip delete preserve {clean}"); continue; }
                        var delPath = Path.Combine(dst, clean.Replace('/', Path.DirectorySeparatorChar));
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
                        var p = Path.Combine(dst, clean.Replace('/', Path.DirectorySeparatorChar));
                        try { Directory.CreateDirectory(p); } catch { }
                    }
                }

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
                        var leaf = Path.GetFileName(r);
                        if (leaf.Equals("filelist.txt", StringComparison.OrdinalIgnoreCase) ||
                            leaf.Equals("emptydirs.txt", StringComparison.OrdinalIgnoreCase) ||
                            leaf.Equals("deletelist.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        return false;
                    }
                    if (Directory.Exists(src))
                    {
                        foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                        {
                            var rel = Path.GetRelativePath(src, s).Replace('\\','/');
                            if (!IgnoreForHash(rel)) map.Add(rel);
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
                            if (!IgnoreForHash(rel)) map.Add(rel);
                        }
                    }
                    int ok = 0, mm = 0, missS = 0, missD = 0, total = 0;
                    foreach (var key in map)
                    {
                        total++;
                        var relSrc = key;
                        var relDst = string.IsNullOrWhiteSpace(strip) ? key : key.StartsWith(strip + "/", StringComparison.OrdinalIgnoreCase) ? key.Substring(strip.Length + 1) : key;
                        var sp = Path.Combine(src, relSrc.Replace('/', Path.DirectorySeparatorChar));
                        var dp = Path.Combine(dst, relDst.Replace('/', Path.DirectorySeparatorChar));
                        var se = File.Exists(sp);
                        var de = File.Exists(dp);
                        if (!se) { missS++; Log($"hash: SRC missing {relSrc}"); continue; }
                        if (!de)
                        {
                            // Do not flag as missing if it is intentionally preserved from copy
                            if (!ShouldPreserve(relDst)) { missD++; Log($"hash: DST missing {relDst}"); }
                            continue;
                        }
                        var h1 = Sha256Hex(sp); var h2 = Sha256Hex(dp);
                        if (!string.IsNullOrEmpty(h1) && h1.Equals(h2, StringComparison.OrdinalIgnoreCase)) { ok++; Log($"hash ok {relDst} {h2}"); }
                        else { mm++; Log($"hash MISMATCH {relDst} src={h1} dst={h2}"); }
                    }
                    Log($"hash union summary: total={total} ok={ok} mismatch={mm} src_missing={missS} dst_missing={missD}");
                }
                catch (Exception ex) { Log($"hash union compare error: {ex.Message}"); }

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
        }

        return 0;
    }

    private static string Sha256Hex(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var buf = new byte[262144];
            int r;
            while ((r = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, r, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch { return string.Empty; }
    }
}
