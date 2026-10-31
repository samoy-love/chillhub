namespace ChillHub.SecurityHelper {
    using System.IO;

    internal static class HelperDiagnostics {
        // Technical paths and provider errors belong in a local log, never in the UI.
        internal static void Record(string message) {
            try {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "security-helper.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch (Exception) { /* Diagnostics must not interrupt a confirmed operation. */ }
        }
    }
}
