namespace ChillHub.Security {
    /// <summary>A Windows antivirus denial, distinct from disk and network failures.</summary>
    public sealed class SecurityBlockedException : IOException {
        public SecurityBlockedException(string filePath, Exception inner)
            : base(SecurityBlock.Message(filePath), inner) {
            this.FilePath = filePath;
        }

        public string FilePath { get; }
    }

    public static class SecurityBlock {
        public const string MetricCode = "sync_security_blocked";

        public static bool IsBlocked(Exception? error) {
            if (error == null) { return false; }
            if (error is SecurityBlockedException) { return true; }
            if (error.HResult == unchecked((int)0x800700E1) || error.HResult == unchecked((int)0x800700E2)) { return true; }
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsBlocked)) { return true; }
            return IsBlocked(error.InnerException);
        }

        public static string Message(string? path = null) => string.IsNullOrEmpty(path)
            ? "Антивирус заблокировал файл. Настройте исключения и повторите загрузку."
            : $"Антивирус заблокировал файл «{Path.GetFileName(path)}». Настройте исключения и повторите загрузку.";

        public static string Describe(Exception error) {
            for (Exception? current = error; current != null; current = current.InnerException) {
                if (current is SecurityBlockedException blocked) { return Message(blocked.FilePath); }
            }
            return Message();
        }
    }
}
