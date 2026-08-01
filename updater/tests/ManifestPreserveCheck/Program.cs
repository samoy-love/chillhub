// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
//
// A11. Тест-предохранитель против регрессии «бесконечный цикл самообновления».
//
// Класс регрессии: файл попадает ОДНОВРЕМЕННО в манифест лаунчера и под preserve-правила.
// Апдейтер такой файл не перезаписывает (preserve), а лаунчер сверяет его хеш с манифестом —
// расхождение неустранимо, обновление предлагается вечно.
//
// Запуск:
//   dotnet run --project updater/tests/ManifestPreserveCheck
//   dotnet run --project updater/tests/ManifestPreserveCheck -- <файл-или-каталог-манифестов> [...]
//
// Код возврата: 0 — всё чисто, 1 — найдено пересечение (или сломан сам детектор).
using System.Text.Json;

using ChillHub.Update;

internal static class Program
{
    private const string GoodManifest = """
    {
      "version": "1.1.7",
      "files": [
        { "path": "ChillHub.exe", "size": 100, "sha256": "aa" },
        { "path": "runtimes/win-x64/native/blake3_dotnet.dll", "size": 200, "sha256": "bb" }
      ],
      "emptyDirs": []
    }
    """;

    private const string BadManifest = """
    {
      "version": "1.1.7",
      "files": [
        { "path": "ChillHub.exe", "size": 100, "sha256": "aa" },
        { "path": "config.json", "size": 45, "sha256": "cc" },
        { "path": "launcher.version", "size": 8, "sha256": "5d37ad10" }
      ],
      "emptyDirs": []
    }
    """;

    public static int Main(string[] args)
    {
        // Без этого русские сообщения превращаются в мусор в консоли Windows и в логах CI.
        try {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch {
            // Кодировку выставить не всегда возможно (перенаправленный вывод) — это не повод падать.
        }

        var failures = 0;

        // 1) Самопроверка детектора: «плохой» манифест обязан падать, «хороший» — проходить.
        var badHits = Violations(ParsePaths(BadManifest));
        if (badHits.Count == 0)
        {
            Console.Error.WriteLine("SELF-TEST FAILED: детектор не увидел config.json/launcher.version в манифесте.");
            failures++;
        }
        else
        {
            Console.WriteLine($"self-test ok: bad manifest -> {badHits.Count} violation(s): {string.Join(", ", badHits)}");
        }

        var goodHits = Violations(ParsePaths(GoodManifest));
        if (goodHits.Count != 0)
        {
            Console.Error.WriteLine($"SELF-TEST FAILED: ложное срабатывание на «хорошем» манифесте: {string.Join(", ", goodHits)}");
            failures++;
        }
        else
        {
            Console.WriteLine("self-test ok: good manifest -> 0 violations");
        }

        // 2) Реальные манифесты репозитория (если есть).
        var targets = args.Length > 0 ? args : DefaultTargets();
        var checkedFiles = 0;
        foreach (var target in targets)
        {
            foreach (var file in ExpandTarget(target))
            {
                checkedFiles++;
                List<string> hits;
                try
                {
                    hits = Violations(ParsePaths(File.ReadAllText(file)));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FAILED to read manifest '{file}': {ex.Message}");
                    failures++;
                    continue;
                }

                if (hits.Count > 0)
                {
                    Console.Error.WriteLine($"VIOLATION in '{file}': пути попадают и в манифест, и под preserve: {string.Join(", ", hits)}");
                    Console.Error.WriteLine("  => апдейтер их не перезапишет, лаунчер будет обновляться вечно.");
                    Console.Error.WriteLine("  => исключите их из пакета (scripts/installer.nsi, build-installer.ps1 -PackageZip) и пересоберите манифест.");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"ok: {file}");
                }
            }
        }

        if (checkedFiles == 0)
        {
            // Важно не выдавать это за успешную проверку: реальные манифесты не смотрели вообще.
            // Штатная ситуация для CI: content/** в .gitignore, в чекауте его просто нет.
            Console.WriteLine("ВНИМАНИЕ: реальные манифесты НЕ проверены — отработал только самотест детектора.");
            Console.WriteLine("  Каталог content/manifests/launcher не найден вверх по дереву ни от одного из корней:");
            foreach (var root in ProbedRoots)
            {
                Console.WriteLine($"    {root}");
            }

            Console.WriteLine("  Укажите путь явно: аргументом командной строки либо переменной CHILLHUB_MANIFESTS_DIR.");
        }

        Console.WriteLine(failures == 0
            ? $"PASS (preserve rules: {PreserveMatcher.DefaultRulesArg})"
            : $"FAIL ({failures} problem(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Пути манифеста, попадающие под preserve-правила или являющиеся мусором апдейтера.</summary>
    private static List<string> Violations(IEnumerable<string> paths)
    {
        var matcher = new PreserveMatcher();
        var hits = new List<string>();
        foreach (var p in paths)
        {
            if (matcher.ShouldPreserve(p) || PreserveMatcher.IsUpdaterArtifact(p))
            {
                hits.Add(p);
            }
        }

        return hits;
    }

    private static List<string> ParsePaths(string json)
    {
        var result = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var f in files.EnumerateArray())
        {
            if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var v = p.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    result.Add(v!);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ExpandTarget(string target)
    {
        if (Directory.Exists(target))
        {
            return Directory.EnumerateFiles(target, "*.json", SearchOption.TopDirectoryOnly)
                .Where(p => !Path.GetFileName(p).Equals("latest.json", StringComparison.OrdinalIgnoreCase));
        }

        return File.Exists(target) ? new[] { target } : Array.Empty<string>();
    }

    /// <summary>Каталоги, от которых искали манифесты (для внятной диагностики).</summary>
    private static readonly List<string> ProbedRoots = new();

    /// <summary>
    /// Ищет content/manifests/launcher вверх по дереву от нескольких стартовых точек.
    ///
    /// Одного рабочего каталога мало: под `dotnet run` он задаётся вызывающим, у собранного
    /// .exe отличается ещё сильнее, а в git-worktree каталог content/** (он в .gitignore)
    /// лежит только в основном рабочем каталоге. Поиск только по CWD молча ничего не находил —
    /// и тест «проходил», не проверив ни одного реального манифеста.
    /// </summary>
    private static string[] DefaultTargets()
    {
        // Явное указание побеждает любой автопоиск (удобно для CI и ручных прогонов).
        var fromEnv = Environment.GetEnvironmentVariable("CHILLHUB_MANIFESTS_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            ProbedRoots.Add($"CHILLHUB_MANIFESTS_DIR={fromEnv}");
            if (Directory.Exists(fromEnv) || File.Exists(fromEnv))
            {
                return new[] { fromEnv };
            }
        }

        foreach (var start in SearchRoots())
        {
            ProbedRoots.Add(start);
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "content", "manifests", "launcher");
                if (Directory.Exists(candidate))
                {
                    Console.WriteLine($"манифесты: {candidate}");
                    return new[] { candidate };
                }

                dir = dir.Parent;
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>Стартовые точки поиска, без повторов и без несуществующих путей.</summary>
    private static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string?>
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,

            // У single-file публикации BaseDirectory — каталог распаковки, а не каталог программы.
            SafeDirectoryOf(Environment.ProcessPath),
        };

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(c!);

                // Снимаем хвостовой разделитель: у AppContext.BaseDirectory он есть, у CWD нет,
                // и без этого один и тот же каталог считается двумя разными корнями.
                var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (trimmed.Length > 0 && !trimmed.EndsWith(':'))
                {
                    full = trimmed;
                }
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(full) && seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static string? SafeDirectoryOf(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }
}
