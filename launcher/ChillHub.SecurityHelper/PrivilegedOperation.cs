namespace ChillHub.SecurityHelper {
    using System.Diagnostics;
    using System.IO;
    using System.IO.Pipes;
    using System.Runtime.InteropServices;
    using System.Security.AccessControl;
    using System.Security.Cryptography;
    using System.Security.Principal;
    using System.Text.Json;
    using System.Windows;
    using ChillHub.Security;
    using Microsoft.Win32.SafeHandles;

    internal sealed record OperationRequest(string Token, string[] Paths, bool Remove);

    internal static class PrivilegedOperation {
        internal static async Task<ExclusionResult[]> RunAsync(string[] paths, bool remove, Action<ExclusionResult> record) {
            var name = "ChillHub.Security." + Guid.NewGuid().ToString("N");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--elevated"); start.ArgumentList.Add(name); start.ArgumentList.Add(token);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Помощник не запущен.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != process.Id) { throw new UnauthorizedAccessException("Подключился другой процесс."); }
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new OperationRequest(token, paths, remove)));
            var results = new List<ExclusionResult>();
            while (true) {
                var line = await ReadBoundedLineAsync(reader, CancellationToken.None);
                if (line == "done") { break; }
                var result = JsonSerializer.Deserialize<ExclusionResult>(line) ?? throw new InvalidDataException("Пустой ответ помощника.");
                if (!paths.Contains(result.Path, StringComparer.OrdinalIgnoreCase)) { throw new InvalidDataException("Помощник вернул неизвестный путь."); }
                results.Add(result);
                record(result); // Persist each completed path, including partial success before a crash.
            }
            return results.ToArray();
        }

        internal static void Serve(string name, string token) {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { throw new UnauthorizedAccessException("Нужны права администратора."); }
            const string prefix = "ChillHub.Security.";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) || token.Length != 64) { throw new ArgumentException("Неверный запрос."); }
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(15000);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = ReadBoundedLineAsync(reader, timeout.Token).GetAwaiter().GetResult();
            var request = JsonSerializer.Deserialize<OperationRequest>(line) ?? throw new InvalidDataException("Пустой запрос.");
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(request.Token), System.Text.Encoding.UTF8.GetBytes(token))) { throw new UnauthorizedAccessException("Запрос не совпал."); }
            var paths = ExclusionPolicy.Validate(request.Paths);
            // Review inside the privileged process prevents a changed request being applied silently.
            var confirmed = HelperAppearance.Confirm(paths, request.Remove);
            foreach (var path in paths) {
                var result = confirmed
                    ? ExclusionOperation.Run(new DefenderProvider(), new[] { path }, request.Remove).Single()
                    : new ExclusionResult(path, ExclusionStatus.Cancelled, "Изменение отменено.");
                writer.WriteLine(JsonSerializer.Serialize(result));
            }
            writer.WriteLine("done");
        }

        private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct) {
            var line = new System.Text.StringBuilder();
            var character = new char[1];
            while (await reader.ReadAsync(character.AsMemory(), ct) != 0) {
                if (character[0] == '\n') { return line.ToString().TrimEnd('\r'); }
                if (line.Length >= 65536) { throw new InvalidDataException("Слишком большой запрос помощника."); }
                line.Append(character[0]);
            }
            throw new EndOfStreamException("Помощник закрыл соединение. Проверьте сохранённые результаты.");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    }
}
