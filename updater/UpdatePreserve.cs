using System.Text;
using System.Text.RegularExpressions;

namespace ChillHub.Update;

/// <summary>
/// Single source of truth for the "preserve" rules shared by the launcher (UpdateWindow)
/// and the native updater (Program).
///
/// A preserved file is user/machine state: the updater never overwrites or deletes it, and the
/// launcher must never treat it as a self-update trigger (otherwise the launcher and the updater
/// disagree forever and the update loops).
///
/// This type lives in the updater assembly because the launcher project already has a
/// ProjectReference to it; the dependency cannot go the other way.
/// </summary>
public sealed class PreserveMatcher
{
    /// <summary>
    /// Default rules. Keep in sync with nothing else — this IS the definition.
    ///
    /// B6. Uninstall.exe создаёт NSIS в момент установки, у каждого пользователя он свой.
    /// Пока он числился в манифесте с фиксированным хешем, у свежеустановленного
    /// пользователя байты не совпадали никогда: проверка целостности всегда видела
    /// расхождение и предлагала обновление вечно — та самая петля самообновления.
    /// Это артефакт времени установки, а не файл пакета, поэтому он в preserve
    /// (те же правила вносят сервер и установщик).
    ///
    /// launcher.update-status — исход последнего обновления, который пишет апдейтер
    /// (A12). Такое же машинное состояние, как launcher.version: в пакет не входит,
    /// перезаписывать и удалять его по манифесту нельзя.
    /// </summary>
    public static readonly string[] DefaultRules =
    {
        "config.json",
        "launcher.version",
        "launcher.update-status",
        "Uninstall.exe",
    };

    /// <summary>Value to pass to the updater's --preserve option.</summary>
    public static string DefaultRulesArg => string.Join(",", DefaultRules);

    /// <summary>
    /// Files produced by the update machinery itself. They are never part of the launcher payload
    /// and must be scrubbed from the installation directory if an older buggy updater copied them there.
    /// </summary>
    public static readonly string[] UpdaterArtifactFiles =
    {
        "filelist.txt",
        "emptydirs.txt",
        "deletelist.txt",
        "apply-update.log",
        "apply-update.cmd",
    };

    /// <summary>Directory (relative to the installation root) an older updater mirrored into place.</summary>
    public const string UpdaterArtifactDir = "updater";

    private readonly List<string> rules;

    public PreserveMatcher(string? csv = null)
    {
        var source = string.IsNullOrWhiteSpace(csv) ? DefaultRulesArg : csv!;
        this.rules = source
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Replace('\\', '/').Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> Rules => this.rules;

    /// <summary>
    /// Returns true when the relative path must not be written/deleted by the updater and must not
    /// be considered a mismatch by the launcher's integrity check.
    /// Supports directory rules ("logs/"), exact relative paths and '*'/'?' wildcards.
    ///
    /// A11. Сравнивается ТОЧНЫЙ путь относительно корня установки — правило "config.json"
    /// защищает только "config.json" в корне и НЕ трогает "data/config.json".
    /// Раньше клиент дополнительно сравнивал имя файла в любом подкаталоге, а сервер —
    /// только точное совпадение верхнего уровня. Из-за расхождения "data/config.json"
    /// сервер публиковал, а клиент молча пропускал: файл никогда не обновлялся, и
    /// проверка целостности расходилась вечно. Правило должно быть ОДНО на обе стороны.
    /// </summary>
    public bool ShouldPreserve(string? relativePath, Action<string>? log = null)
    {
        var norm = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (norm.Length == 0)
        {
            return false;
        }

        foreach (var rule in this.rules)
        {
            if (rule.EndsWith('/'))
            {
                var dir = rule.Trim('/');
                if (dir.Length == 0)
                {
                    log?.Invoke($"preserve (root dir): {norm} by '{rule}'");
                    return true;
                }

                if (norm.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"preserve (dir): {norm} by '{rule}'");
                    return true;
                }

                continue;
            }

            if (rule.Contains('*') || rule.Contains('?'))
            {
                if (WildcardIsMatch(norm, rule))
                {
                    log?.Invoke($"preserve (wildcard): {norm} by '{rule}'");
                    return true;
                }

                continue;
            }

            if (norm.Equals(rule, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"preserve (exact): {norm} by '{rule}'");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the relative path is an artifact of the update machinery itself
    /// (<see cref="UpdaterArtifactFiles"/> or anything under <see cref="UpdaterArtifactDir"/>).
    /// Such paths must never be mirrored into — and must be scrubbed from — the installation directory.
    /// </summary>
    public static bool IsUpdaterArtifact(string? relativePath)
    {
        var norm = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (norm.Length == 0)
        {
            return false;
        }

        if (norm.StartsWith(UpdaterArtifactDir + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A11. Только верхний уровень, как и в preserve: имя файла в произвольном
        // подкаталоге (например "data/filelist.txt") — это обычный файл пакета,
        // и клиент не имеет права молча его пропускать, раз сервер его публикует.
        foreach (var name in UpdaterArtifactFiles)
        {
            if (norm.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardIsMatch(string text, string pattern)
    {
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
                default: sb.Append(Regex.Escape(ch.ToString())); break;
            }
        }

        sb.Append('$');
        try
        {
            return Regex.IsMatch(text, sb.ToString(), RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
