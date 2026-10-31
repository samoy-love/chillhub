namespace ChillHub.Security {
    using System.Diagnostics;
    using System.Management;

    public sealed class DefenderProvider : IAntivirusProvider {
        public string Id => "defender";

        public IReadOnlyList<string> ReadPaths() {
            using var search = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT ExclusionPath FROM MSFT_MpPreference");
            using var items = search.Get();
            foreach (ManagementObject item in items) {
                using (item) { return item["ExclusionPath"] as string[] ?? Array.Empty<string>(); }
            }
            throw new InvalidOperationException("Настройки Defender недоступны.");
        }

        public void Add(string path) => Change("Add", path);
        public void Remove(string path) => Change("Remove", path);

        private static void Change(string method, string path) {
            using var preferences = new ManagementClass(@"root\Microsoft\Windows\Defender:MSFT_MpPreference");
            using var args = preferences.GetMethodParameters(method);
            args["ExclusionPath"] = new[] { ExclusionPolicy.Normalize(path) };
            using var result = preferences.InvokeMethod(method, args, null);
            if (result == null || Convert.ToUInt32(result["ReturnValue"]) != 0) {
                throw new InvalidOperationException("Defender отклонил изменение. Проверьте права и политику защиты.");
            }
        }

        public bool? Check(string path) {
            var executable = FindCommand();
            if (executable == null) { return null; }
            var start = new ProcessStartInfo(executable) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-CheckExclusion");
            start.ArgumentList.Add("-Path");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process == null) { return null; }
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(); return null; }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            return process.ExitCode switch { 0 => true, 1 => false, _ => null };
        }

        private static string? FindCommand() {
            var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
            if (Directory.Exists(platform)) {
                var file = Directory.GetDirectories(platform)
                    .Select(p => (Path: Path.Combine(p, "MpCmdRun.exe"), Version: Version.TryParse(Path.GetFileName(p).Split('-')[0], out var v) ? v : new Version()))
                    .OrderByDescending(p => p.Version).Select(p => p.Path).FirstOrDefault(File.Exists);
                if (file != null) { return file; }
            }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
            return File.Exists(fallback) ? fallback : null;
        }
    }
}
