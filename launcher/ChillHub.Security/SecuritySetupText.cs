namespace ChillHub.Security {
    public sealed record SecuritySetupResult(bool Complete, string Title, string Message);

    public static class SecuritySetupText {
        public const string RemoveTitle = "Удаление исключений";
        public const string RemoveIntro = "Верните проверку файлов антивирусом.";
        public const string CopyFoldersHint = "Добавьте каждую папку: скопируйте и вставьте в антивирус.";
        public const string CopyHint = "Скопировано. Вставьте в исключения антивируса.";
        public const string CopyFailed = "Не удалось скопировать";
        public const string RemoveWarning = "Удалятся только исключения, добавленные Chill Hub.";
        public const string DetectionFailed = "Выберите ваш антивирус";
        public const string NothingToRemove = "Удалять нечего";
        public const string GuideFailed = "Не удалось открыть инструкцию";
        public const string Waiting = "Подтвердите в Windows";
        public const string CancelledTitle = "Настройка отменена";
        public const string IncompleteTitle = "Настройка не завершена";
        public const string Interrupted = "Настройка прервалась. Часть папок могла добавиться — можно повторить.";
        public const string RememberFailed = "Не удалось запомнить выбор. Окно может появиться снова.";
        public const string PrepareFailed = "Некоторые папки недоступны. Исключения не менялись — попробуйте ручную настройку.";

        public const string Title = "Настройка антивируса";
        public const string Intro = "Добавьте файлы Chill Hub в исключения.";
        public const string ManualHint = "Добавьте папки по инструкции. Антивирус перестанет их проверять.";
        public const string Pending = "Нажмите «Да», затем подтвердите настройку.";
        public const string Cancelled = "Настройки не изменены. Можно повторить.";
        public const string Retry = "Вернитесь в лаунчер. При ошибке повторите загрузку.";

        public const string Warning = "Антивирус перестанет проверять эти папки, включая новые игры и моды.";
        public const string FilesOnlyHint = "McAfee позволяет добавлять только отдельные файлы. Откройте инструкцию.";
        public const string UnknownHint = "В антивирусе откройте «Исключения» и добавьте папки. Он перестанет их проверять.";
        public const string TryAgain = "Попробуйте ещё раз.";
        public const string OpenFailed = "Не удалось открыть настройку антивируса. Перезапустите Chill Hub.";

        public static string FolderName(string path, string? install, string? updates, IEnumerable<string> games) {
            static bool Same(string path, string? other) => !string.IsNullOrWhiteSpace(other)
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)), StringComparison.OrdinalIgnoreCase);
            if (Same(path, install)) { return "Лаунчер"; }
            if (Same(path, updates)) { return "Обновления"; }
            return games.Any(game => Same(path, game)) ? "Игры" : "Другие файлы";
        }

        public static string ResultHint(ExclusionStatus status) => status switch {
            ExclusionStatus.Blocked => "антивирус не применил настройку",
            ExclusionStatus.Unknown => "не удалось проверить",
            ExclusionStatus.Manual => "нужна ручная настройка",
            _ => "не удалось настроить",
        };

        public static SecuritySetupResult Summarize(IReadOnlyCollection<ExclusionResult> results, int expected, bool removing) {
            var allReported = expected > 0 && results.Count == expected
                && results.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == expected;
            if (allReported && results.All(r => r.Status == ExclusionStatus.Cancelled)) {
                return new(false, "Настройка отменена", Cancelled);
            }
            var complete = allReported
                && results.All(r => removing ? r.Status == ExclusionStatus.Removed : r.Status is ExclusionStatus.Applied or ExclusionStatus.Existing);
            if (complete) {
                return removing
                    ? new(true, "Исключения удалены", "Файлы снова проверяются, если нет других исключений.")
                    : new(true, "Всё настроено", Retry);
            }
            return new(false, "Настройка не завершена", "Не все папки удалось настроить. Повторите попытку или настройте вручную.");
        }
    }
}
