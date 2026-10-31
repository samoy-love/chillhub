namespace ChillHub.Security {
    public static class ExclusionPolicy {
        public const string Warning = "Антивирус перестанет проверять файлы в выбранных папках, включая новые игры и моды. Исключение не подтверждает безопасность файлов.";

        public static string Normalize(string value) {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.IndexOfAny(new[] { '*', '?', '\r', '\n', '\0', '%', '"', '<', '>', '|' }) >= 0
                || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)) {
                throw new ArgumentException("Нужен полный путь к отдельной локальной папке без масок.");
            }
            var inputSegments = value.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (inputSegments.Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))) {
                throw new ArgumentException("В конце имени папки не должно быть точки или пробела.");
            }
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var root = Path.GetPathRoot(path)!;
            if (string.Equals(path, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
                || path[root.Length..].Contains(':')) {
                throw new ArgumentException("Нельзя исключать весь диск или специальный путь.");
            }

            var forbidden = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.GetTempPath(),
                Path.Combine(root, "Users"),
            };
            foreach (var entry in forbidden.Where(p => !string.IsNullOrEmpty(p))) {
                if (IsWithin(entry, path)) { throw new ArgumentException("Эта папка слишком широка для исключения. Выберите папку Chill Hub или отдельной игры."); }
            }
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows) && IsWithin(path, windows)) { throw new ArgumentException("Нельзя исключать системные файлы Windows."); }

            var segments = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if ((segments.Length <= 2 && segments[0].Equals("Users", StringComparison.OrdinalIgnoreCase))
                || Path.GetFileName(path).Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                || (Path.GetFileName(path).Equals("common", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "steamapps", StringComparison.OrdinalIgnoreCase))
                || Directory.Exists(Path.Combine(path, "steamapps"))) {
                throw new ArgumentException("Выберите отдельную папку игры, а не профиль пользователя или библиотеку Steam.");
            }

            for (var node = new DirectoryInfo(path); node != null; node = node.Parent) {
                if ((node.Exists || File.Exists(node.FullName)) && (File.GetAttributes(node.FullName) & FileAttributes.ReparsePoint) != 0) {
                    throw new ArgumentException("Путь содержит ссылку на другую папку. Настройте исключение вручную.");
                }
            }
            if (File.Exists(path)) { throw new ArgumentException("Ожидалась папка, а не файл."); }
            return path;
        }

        public static bool IsWithin(string path, string parent) {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            return full.Equals(basePath, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static string[] Validate(IEnumerable<string> paths) {
            var input = paths.ToArray();
            if (input.Length is < 1 or > 32) { throw new ArgumentException("Выберите от одной до 32 папок."); }
            return input.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public enum ExclusionStatus { Existing, Applied, Removed, Manual, Cancelled, Blocked, Unknown, Failed }
    public sealed record ExclusionResult(string Path, ExclusionStatus Status, string Detail, bool Added = false);

    public interface IAntivirusProvider {
        string Id { get; }
        IReadOnlyList<string> ReadPaths();
        void Add(string path);
        void Remove(string path);
        bool? Check(string path);
    }

    public static class ExclusionOperation {
        public static IReadOnlyList<ExclusionResult> Run(IAntivirusProvider provider, IEnumerable<string> paths, bool remove) {
            var normalized = ExclusionPolicy.Validate(paths);
            var results = new List<ExclusionResult>();
            foreach (var path in normalized) {
                bool added = false;
                try {
                    // Revalidate at the privileged boundary immediately before touching settings.
                    ExclusionPolicy.Normalize(path);
                    var exists = provider.ReadPaths().Any(p => string.Equals(Path.TrimEndingDirectorySeparator(p), path, StringComparison.OrdinalIgnoreCase));
                    if (remove) {
                        if (exists) { provider.Remove(path); }
                        var remains = provider.ReadPaths().Any(p => string.Equals(Path.TrimEndingDirectorySeparator(p), path, StringComparison.OrdinalIgnoreCase));
                        results.Add(new(path, remains ? ExclusionStatus.Blocked : ExclusionStatus.Removed,
                            remains ? "Удаление не применилось: проверьте политику антивируса." : "Запись удалена. Другие исключения не изменены."));
                        continue;
                    }
                    if (provider.Check(path) == true) {
                        results.Add(new(path, ExclusionStatus.Existing, "Исключение уже действует."));
                        continue;
                    }
                    if (!exists) { provider.Add(path); added = true; }
                    var registered = provider.ReadPaths().Any(p => string.Equals(Path.TrimEndingDirectorySeparator(p), path, StringComparison.OrdinalIgnoreCase));
                    // Keep ownership even if effective verification is unavailable, so removal remains possible.
                    var effective = provider.Check(path);
                    results.Add(new(path, effective == true ? ExclusionStatus.Applied : effective == false ? ExclusionStatus.Blocked : ExclusionStatus.Unknown,
                        effective == true ? "Применено и проверено." : effective == false ? "Исключение не действует. Возможно, изменение запрещено политикой." : "Не удалось проверить применение. Проверьте список в безопасности Windows.", added && registered));
                }
                catch (Exception ex) {
                    results.Add(new(path, ex is UnauthorizedAccessException ? ExclusionStatus.Blocked : ExclusionStatus.Failed,
                        "Не удалось изменить исключение: " + ex.Message, added));
                }
            }
            return results;
        }
    }
}
