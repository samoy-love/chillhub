namespace ChillHub.Security {
    using System.Text.Json;

    public sealed record ManualExclusion(string ProductId, string Path);

    public sealed class SecurityJournal {
        public List<string> AddedPaths { get; set; } = new();
        public List<string> OfferedSteamPaths { get; set; } = new();
        public List<ManualExclusion> ManualPaths { get; set; } = new();

        public static SecurityJournal Update(string path, Action<SecurityJournal> change) {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
            using var mutex = new Mutex(false, "Local\\ChillHub.SecurityJournal." + key);
            bool entered;
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) { throw new IOException("Журнал исключений занят. Повторите попытку."); }
            try {
                var journal = Read(path);
                change(journal);
                journal.Save(path);
                return journal;
            }
            finally { mutex.ReleaseMutex(); }
        }

        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "security-exclusions.json");

        public static SecurityJournal Read(string path) => File.Exists(path)
            ? JsonSerializer.Deserialize<SecurityJournal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Журнал исключений пуст.")
            : new SecurityJournal();

        public void Record(IEnumerable<ExclusionResult> results) {
            foreach (var result in results) {
                if (result.Added && !this.AddedPaths.Contains(result.Path, StringComparer.OrdinalIgnoreCase)) { this.AddedPaths.Add(result.Path); }
                if (result.Status == ExclusionStatus.Removed) { this.AddedPaths.RemoveAll(p => p.Equals(result.Path, StringComparison.OrdinalIgnoreCase)); }
            }
        }

        public void Save(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
    }
}
