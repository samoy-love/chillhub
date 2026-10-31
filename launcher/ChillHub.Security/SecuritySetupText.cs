namespace ChillHub.Security {
    public sealed record SecuritySetupResult(bool Complete, string Title, string Message);

    public static class SecuritySetupText {
        public const string Detecting = "Определяем антивирус…";
        public const string RemoveTitle = "Удаление исключений";
        public const string RemoveIntro = "Перед удалением лаунчера можно убрать исключения, которые добавил помощник.";
        public const string FolderHint = "Снимите галочки с папок, которые не хотите менять.";
        public const string CopyFoldersHint = "Скопируйте нужные пути в настройки антивируса.";
        public const string CopyTitle = "Путь скопирован";
        public const string CopyHint = "Вставьте его в настройках антивируса.";
        public const string CopyFailed = "Не удалось скопировать путь";
        public const string RemoveWarning = "Удаление исключений не отменяет другие правила антивируса.";
        public const string DetectionFailed = "Антивирус не определён — выберите его в списке.";
        public const string BusyTitle = "Настройка выполняется";
        public const string BusyClose = "Дождитесь результата, затем закройте окно.";
        public const string RemoveHint = "Будут удалены только выбранные записи Microsoft Defender, добавленные помощником.";
        public const string NothingToRemove = "Удалять нечего";
        public const string NothingToRemoveHint = "Нет записей, добавленных помощником. Можно закрыть окно.";
        public const string GuideOpened = "Инструкция открыта в браузере";
        public const string GuideOpenedHint = "Добавьте нужные папки в настройках антивируса. Помощник не проверяет ручную настройку. ";
        public const string GuideFailed = "Не удалось открыть инструкцию";
        public const string SelectFolders = "Выберите папки";
        public const string SelectOwned = "Удалить можно только записи, добавленные помощником.";
        public const string Waiting = "Ожидаем подтверждение";
        public const string CancelledTitle = "Настройка отменена";
        public const string IncompleteTitle = "Настройка не завершена";
        public const string Interrupted = "Часть изменений могла примениться. Проверьте список исключений в безопасности Windows или повторите попытку.";

        public const string Title = "Настройка защиты";
        public const string Intro = "Если антивирус мешает загрузке, добавьте папки Chill Hub в исключения.";
        public const string DefenderHint = "Помощник добавит выбранные папки в Microsoft Defender. Windows запросит разрешение администратора.";
        public const string ManualHint = "Откройте инструкцию и добавьте папки ниже в настройках антивируса. Затем вернитесь в лаунчер и повторите загрузку.";
        public const string Pending = "Подтвердите запрос Windows, затем список папок. Дождитесь результата в этом окне.";
        public const string Cancelled = "Разрешение отменено. Настройки не изменены. Можно попробовать снова или закрыть окно.";
        public const string Retry = "Закройте это окно и повторите загрузку в лаунчере.";

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
                    ? new(true, "Исключения удалены", "Записи, добавленные помощником, удалены. Другие исключения не изменены. Можно закрыть окно.")
                    : new(true, "Исключения настроены", "Microsoft Defender подтвердил настройку для всех выбранных папок. " + Retry);
            }
            return new(false, "Настройка не завершена", "Не все изменения удалось подтвердить. Проверьте результат для каждой папки ниже. Можно повторить попытку или настроить антивирус вручную.");
        }
    }
}
