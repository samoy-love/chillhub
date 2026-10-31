namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed record MissingGameFolder(string Name, string Path);

    internal sealed record MissingGamesSnapshot(string Root, IReadOnlyList<MissingGameFolder> Folders);

    /// <summary>Один успешный обход за жизнь главного окна; ошибки разрешают повтор.</summary>
    internal sealed class MissingGamesCheck {
        private int state;
        private HashSet<string> available = new(StringComparer.OrdinalIgnoreCase);

        internal bool IsAvailable(string gameId) => Volatile.Read(ref this.available).Contains(gameId);

        internal async Task<MissingGamesSnapshot?> ScanAsync(string root, IReadOnlyList<GameInfo> games) {
            // Обновляем защиту даже после обхода: пока диалог отложен, каталог мог измениться.
            MissingGamesCleanup.ValidateGames(games);
            Volatile.Write(ref this.available, new HashSet<string>(games.Select(g => g.GameId), StringComparer.OrdinalIgnoreCase));
            if (Interlocked.CompareExchange(ref this.state, 1, 0) != 0) {
                return null;
            }

            try {
                var snapshot = await Task.Run(() => MissingGamesCleanup.Scan(root, games));
                Interlocked.Exchange(ref this.state, 2);
                return snapshot;
            }
            catch {
                Interlocked.Exchange(ref this.state, 0);
                throw;
            }
        }
    }

    internal static class MissingGamesCleanup {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>Отсутствующее items не равнозначно пустому каталогу.</summary>
        internal static async Task<GamesResponse> LoadCatalogAsync(HttpClient http, string url) {
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            return ParseCatalog(json.RootElement);
        }

        internal static GamesResponse ParseCatalog(JsonElement json) {
            if (json.ValueKind != JsonValueKind.Object
                || !json.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array) {
                throw new JsonException("Catalog must contain an items array.");
            }

            var response = json.Deserialize<GamesResponse>(JsonOptions)!;
            ValidateGames(response.Items);
            return response;
        }

        internal static MissingGamesSnapshot Scan(string root, IReadOnlyList<GameInfo> games) {
            ValidateGames(games);
            root = NormalizeRoot(root);
            ValidateRoot(root);
            var known = new HashSet<string>(games.Select(g => g.GameId), StringComparer.OrdinalIgnoreCase);
            var folders = new List<MissingGameFolder>();
            // Directory.Exists скрывает отказ в доступе. Такой отказ не должен завершать проверку.
            try {
                foreach (var path in Directory.EnumerateDirectories(root)) {
                    var name = System.IO.Path.GetFileName(path);
                    if (!known.Contains(name)) {
                        folders.Add(new MissingGameFolder(name, System.IO.Path.GetFullPath(path)));
                    }
                }
            }
            catch (DirectoryNotFoundException) {
                // Ещё не созданная папка игр — корректный пустой результат.
            }

            return new MissingGamesSnapshot(root, folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
        }

        internal static string DeleteFolder(
            MissingGamesSnapshot snapshot,
            MissingGameFolder folder,
            Func<string> currentRoot,
            Func<string, bool> busy,
            Func<string, bool> available,
            Func<string, bool>? running = null,
            Action<string>? removeShortcuts = null) {
            try {
                var root = NormalizeRoot(snapshot.Root);
                var path = System.IO.Path.GetFullPath(folder.Path);
                if (!string.Equals(root, NormalizeRoot(currentRoot()), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(System.IO.Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(System.IO.Path.GetFileName(path), folder.Name, StringComparison.OrdinalIgnoreCase)) {
                    return MissingGamesText.Changed;
                }

                using var guard = DeletionPathGuard.HoldRoot(root);
                if (available(folder.Name)) {
                    return MissingGamesText.Available;
                }

                if (busy(folder.Name) || (running ?? HasRunningProcess)(path)) {
                    return MissingGamesText.Busy;
                }

                DeleteNode(path);
                if (Exists(path)) {
                    return MissingGamesText.Failed;
                }

                if (removeShortcuts != null) {
                    removeShortcuts(path);
                }
                else {
                    GameLocalState.TryRemoveDesktopShortcuts(path);
                }

                return MissingGamesText.Deleted;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"MissingGamesCleanup.DeleteFolder: {ex.Message}");
                return MissingGamesText.Failed;
            }
        }

        internal static void ValidateGames(IReadOnlyList<GameInfo> games) {
            if (games == null || games.Any(g => g == null || string.IsNullOrWhiteSpace(g.GameId)
                || g.GameId is "." or ".." || g.GameId.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)) {
                throw new JsonException("Catalog contains an invalid game id.");
            }
        }

        private static string NormalizeRoot(string root) {
            if (string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathFullyQualified(root)) {
                throw new IOException("Games directory must be absolute.");
            }

            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        }

        private static void ValidateRoot(string root) {
            // Подмена самого корня или его родителей ссылкой не должна увести удаление наружу.
            for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent) {
                if (Exists(dir.FullName) && (File.GetAttributes(dir.FullName) & FileAttributes.ReparsePoint) != 0) {
                    throw new IOException("Games directory has a linked ancestor.");
                }
            }
        }

        private static bool Exists(string path) {
            try {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) {
                return false;
            }
            catch (DirectoryNotFoundException) {
                return false;
            }
        }

        private static void DeleteNode(string path) {
            using var handle = DeletionPathGuard.HoldEntry(path);
            if (handle == null) {
                return;
            }

            var attributes = File.GetAttributes(path);
            var directory = (attributes & FileAttributes.Directory) != 0;
            // Никакого рекурсивного Directory.Delete: у ссылок удаляется только сама запись.
            if ((attributes & FileAttributes.ReparsePoint) != 0) {
                handle.Dispose();
                if (directory) {
                    Directory.Delete(path, false);
                }
                else {
                    File.Delete(path);
                }

                return;
            }

            if (directory) {
                foreach (var child in Directory.GetFileSystemEntries(path)) {
                    try {
                        DeleteNode(child);
                    }
                    catch (Exception ex) {
                        Logging.Logger.Warn($"MissingGamesCleanup: {ex.Message}");
                    }
                }

                // Родители остаются закреплены. После освобождения самой папки
                // удаляем только запись, без повторного рекурсивного обхода.
                handle.Dispose();
                Directory.Delete(path, false);
            }
            else {
                if ((attributes & FileAttributes.ReadOnly) != 0) {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }

                handle.Dispose();
                File.Delete(path);
            }
        }

        private static bool HasRunningProcess(string folder) {
            foreach (var process in Process.GetProcesses()) {
                using (process) {
                    try {
                        if (Game.GameProcessFinder.BelongsTo(process.MainModule?.FileName, folder)) {
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) {
                        // Чужой защищённый или уже завершившийся процесс не даёт прочесть путь.
                    }
                }
            }

            return false;
        }
    }
}
