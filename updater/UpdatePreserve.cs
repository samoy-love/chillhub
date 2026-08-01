// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.

namespace ChillHub.Update;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Единый источник правды о том, какие файлы установки НЕ трогает апдейтер.
/// Используется и апдейтером (что не перезаписывать / не удалять),
/// и лаунчером (что не учитывать при сверке хешей манифеста и что передавать в --preserve).
/// Рассинхрон этих двух списков — причина бесконечного цикла самообновления.
/// </summary>
public sealed class PreserveMatcher
{
    /// <summary>
    /// Правила по умолчанию в том виде, в котором они передаются в апдейтер через --preserve.
    /// ВАЖНО: эти же файлы обязаны отсутствовать в манифесте лаунчера
    /// (см. тест updater/tests — ManifestPreserveCheck).
    /// </summary>
    public const string DefaultRulesCsv = "config.json,launcher.version";

    /// <summary>
    /// Служебные файлы апдейтера. Они никогда не должны попадать в папку установки
    /// и подлежат разовой очистке на уже «засорённых» инсталляциях.
    /// </summary>
    public static readonly IReadOnlyList<string> UpdaterArtifactFiles = new[]
    {
        "filelist.txt",
        "emptydirs.txt",
        "deletelist.txt",
        "apply-update.log",
        "apply-update.cmd",
    };

    /// <summary>
    /// Служебный подкаталог с копией апдейтера (тоже не должен оказаться в папке установки).
    /// </summary>
    public const string UpdaterArtifactDir = "updater";

    private readonly List<string> rules;

    public PreserveMatcher(IEnumerable<string> rules)
    {
        this.rules = (rules ?? Enumerable.Empty<string>())
            .Select(s => (s ?? string.Empty).Replace('\\', '/').Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Матчер с правилами по умолчанию.</summary>
    public static PreserveMatcher Default => Parse(DefaultRulesCsv);

    /// <summary>Активные правила (нормализованные).</summary>
    public IReadOnlyList<string> Rules => this.rules;

    /// <summary>Разбирает CSV-строку правил (формат аргумента --preserve).</summary>
    public static PreserveMatcher Parse(string? csv)
    {
        var src = string.IsNullOrWhiteSpace(csv) ? DefaultRulesCsv : csv!;
        return new PreserveMatcher(src.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Правила в виде CSV — ровно то, что нужно передать в --preserve.</summary>
    public string ToCsv() => string.Join(",", this.rules);

    /// <summary>
    /// Проверяет, попадает ли относительный путь под правила preserve.
    /// Поддерживаются: каталоги (правило оканчивается на '/'), точный относительный путь,
    /// только имя файла, а также простые маски '*' и '?'.
    /// </summary>
    public bool ShouldPreserve(string? rel) => this.ShouldPreserve(rel, out _);

    /// <summary>То же, но дополнительно возвращает сработавшее правило (для логов).</summary>
    public bool ShouldPreserve(string? rel, out string? matchedRule)
    {
        matchedRule = null;
        var norm = (rel ?? string.Empty).Replace('\\', '/').Trim('/');
        if (norm.Length == 0)
        {
            return false;
        }

        var leaf = norm.Split('/').Last();
        foreach (var rule in this.rules)
        {
            if (rule.EndsWith('/'))
            {
                var dir = rule.Trim('/');
                if (dir.Length == 0)
                {
                    matchedRule = rule;
                    return true;
                }

                if (norm.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                {
                    matchedRule = rule;
                    return true;
                }

                continue;
            }

            if (rule.Contains('*', StringComparison.Ordinal) || rule.Contains('?', StringComparison.Ordinal))
            {
                if (WildcardIsMatch(norm, rule) || WildcardIsMatch(leaf, rule))
                {
                    matchedRule = rule;
                    return true;
                }

                continue;
            }

            if (norm.Equals(rule, StringComparison.OrdinalIgnoreCase) || leaf.Equals(rule, StringComparison.OrdinalIgnoreCase))
            {
                matchedRule = rule;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Служебный ли это файл/каталог апдейтера (мусор от прошлых версий в папке установки).
    /// </summary>
    public static bool IsUpdaterArtifact(string? rel)
    {
        var norm = (rel ?? string.Empty).Replace('\\', '/').Trim('/');
        if (norm.Length == 0)
        {
            return false;
        }

        if (norm.StartsWith(UpdaterArtifactDir + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leaf = norm.Split('/').Last();
        foreach (var name in UpdaterArtifactFiles)
        {
            if (leaf.Equals(name, StringComparison.OrdinalIgnoreCase))
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
