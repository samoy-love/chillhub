namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    public class MissingGamesCleanupTests {
        [Fact]
        public void ScanIncludesEmptyAndUnmarkedFoldersButNotKnownGamesOrLooseFiles() {
            using var temp = new TempDir();
            Directory.CreateDirectory(Path.Combine(temp.Root, "known"));
            Directory.CreateDirectory(Path.Combine(temp.Root, "empty"));
            var unmarked = Directory.CreateDirectory(Path.Combine(temp.Root, "unmarked")).FullName;
            File.WriteAllText(Path.Combine(unmarked, "game.exe"), "game");
            File.WriteAllText(Path.Combine(temp.Root, "readme.txt"), "keep");

            var result = MissingGamesCleanup.Scan(temp.Root, new[] { new GameInfo { GameId = "KNOWN" } });

            Assert.Equal(new[] { "empty", "unmarked" }, result.Folders.Select(f => f.Name));
            Assert.All(result.Folders, f => Assert.True(Path.IsPathFullyQualified(f.Path)));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("{\"items\":null}")]
        [InlineData("{\"items\":{}}")]
        [InlineData("{\"items\":[null]}")]
        [InlineData("{\"items\":[{}]}")]
        [InlineData("{\"items\":[{\"gameId\":\"../other\"}]}")]
        public void InvalidCatalogCannotBecomeAnEmptyCatalog(string json) {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<JsonException>(() => MissingGamesCleanup.ParseCatalog(document.RootElement));
        }

        [Fact]
        public void ExplicitEmptyCatalogIsValid() {
            using var document = JsonDocument.Parse("{\"items\":[]}");
            Assert.Empty(MissingGamesCleanup.ParseCatalog(document.RootElement).Items);
        }

        [Fact]
        public async Task FailedRequestAllowsSuccessfulRetryAndOnlyOneScanPerSession() {
            using var temp = new TempDir();
            Directory.CreateDirectory(Path.Combine(temp.Root, "removed"));
            using var handler = new CatalogHandler();
            using var http = new HttpClient(handler);
            var check = new MissingGamesCheck();
            await Assert.ThrowsAsync<HttpRequestException>(() => MissingGamesCleanup.LoadCatalogAsync(http, "https://example.invalid/api/games"));
            var catalog = await MissingGamesCleanup.LoadCatalogAsync(http, "https://example.invalid/api/games");
            Assert.Single((await check.ScanAsync(temp.Root, catalog.Items))!.Folders);
            Assert.Null(await check.ScanAsync(temp.Root, catalog.Items));
            // Следующий процесс не помнит отказ пользователя.
            Assert.Single((await new MissingGamesCheck().ScanAsync(temp.Root, catalog.Items))!.Folders);
        }

        [Fact]
        public async Task FailedScanCanRetryAndEmptyScanCompletesSession() {
            using var temp = new TempDir();
            var check = new MissingGamesCheck();
            await Assert.ThrowsAsync<IOException>(() => check.ScanAsync("relative", Array.Empty<GameInfo>()));
            Assert.Empty((await check.ScanAsync(temp.Root, Array.Empty<GameInfo>()))!.Folders);
            Directory.CreateDirectory(Path.Combine(temp.Root, "new-folder"));
            Assert.Null(await check.ScanAsync(temp.Root, Array.Empty<GameInfo>()));
        }

        [Fact]
        public async Task ConcurrentScansReturnOneResult() {
            using var temp = new TempDir();
            var check = new MissingGamesCheck();
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => check.ScanAsync(temp.Root, Array.Empty<GameInfo>())));
            Assert.Single(results, r => r != null);
        }

        [Fact]
        public async Task AvailabilityTracksLatestCatalogEvenWithoutAnotherScan() {
            using var temp = new TempDir();
            var check = new MissingGamesCheck();
            var catalog = new[] { new GameInfo { GameId = "demo" } };
            await check.ScanAsync(temp.Root, catalog);
            Assert.True(check.IsAvailable("DEMO"));
            Assert.Null(await check.ScanAsync(temp.Root, Array.Empty<GameInfo>()));
            Assert.False(check.IsAvailable("demo"));
            Assert.Null(await check.ScanAsync(temp.Root, catalog));
            Assert.True(check.IsAvailable("demo"));
        }

        [Fact]
        public void DeletesSelectedFolderAndShortcutsButKeepsOtherFolders() {
            using var temp = new TempDir();
            var a = Directory.CreateDirectory(Path.Combine(temp.Root, "a")).FullName;
            var b = Directory.CreateDirectory(Path.Combine(temp.Root, "b")).FullName;
            File.WriteAllText(Path.Combine(a, "save.txt"), "save");
            File.SetAttributes(Path.Combine(a, "save.txt"), FileAttributes.ReadOnly);
            var snapshot = MissingGamesCleanup.Scan(temp.Root, Array.Empty<GameInfo>());
            string? shortcuts = null;

            var result = MissingGamesCleanup.DeleteFolder(snapshot, snapshot.Folders[0], () => temp.Root,
                _ => false, _ => false, _ => false, p => shortcuts = p);

            Assert.Equal(MissingGamesText.Deleted, result);
            Assert.False(Directory.Exists(a));
            Assert.True(Directory.Exists(b));
            Assert.Equal(a, shortcuts);
        }

        [Fact]
        public void BusyRunningAndAvailableGamesArePreserved() {
            using var temp = new TempDir();
            var game = Directory.CreateDirectory(Path.Combine(temp.Root, "game")).FullName;
            var snapshot = MissingGamesCleanup.Scan(temp.Root, Array.Empty<GameInfo>());
            var folder = snapshot.Folders.Single();
            Assert.Equal(MissingGamesText.Busy, MissingGamesCleanup.DeleteFolder(snapshot, folder, () => temp.Root, _ => true, _ => false, _ => false));
            Assert.Equal(MissingGamesText.Busy, MissingGamesCleanup.DeleteFolder(snapshot, folder, () => temp.Root, _ => false, _ => false, _ => true));
            Assert.Equal(MissingGamesText.Available, MissingGamesCleanup.DeleteFolder(snapshot, folder, () => temp.Root, _ => false, _ => true, _ => false));
            Assert.True(Directory.Exists(game));
        }

        [Fact]
        public void ChangedRootRootItselfAndOutsidePathsAreNeverDeleted() {
            using var temp = new TempDir();
            using var outside = new TempDir();
            Directory.CreateDirectory(Path.Combine(temp.Root, "game"));
            var snapshot = MissingGamesCleanup.Scan(temp.Root, Array.Empty<GameInfo>());
            Assert.Equal(MissingGamesText.Changed, MissingGamesCleanup.DeleteFolder(snapshot, snapshot.Folders[0], () => outside.Root, _ => false, _ => false));
            foreach (var path in new[] { temp.Root, outside.Root, Path.Combine(temp.Root, "..", Path.GetFileName(outside.Root)) }) {
                Assert.Equal(MissingGamesText.Changed, MissingGamesCleanup.DeleteFolder(snapshot,
                    new MissingGameFolder(Path.GetFileName(path), path), () => temp.Root, _ => false, _ => false));
            }

            Assert.True(Directory.Exists(temp.Root));
            Assert.True(Directory.Exists(outside.Root));
        }

        [Fact]
        public void LockedFileIsReportedAndDoesNotPreventOtherFilesFromBeingRemoved() {
            using var temp = new TempDir();
            var game = Directory.CreateDirectory(Path.Combine(temp.Root, "game")).FullName;
            var blocked = Path.Combine(game, "locked.bin");
            File.WriteAllText(blocked, "busy");
            File.WriteAllText(Path.Combine(game, "free.bin"), "free");
            var snapshot = MissingGamesCleanup.Scan(temp.Root, Array.Empty<GameInfo>());
            var shortcutsRemoved = false;
            using (new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None)) {
                Assert.Equal(MissingGamesText.Failed, MissingGamesCleanup.DeleteFolder(snapshot, snapshot.Folders[0],
                    () => temp.Root, _ => false, _ => false, _ => false, _ => shortcutsRemoved = true));
            }

            Assert.True(File.Exists(blocked));
            Assert.False(File.Exists(Path.Combine(game, "free.bin")));
            Assert.False(shortcutsRemoved);
        }

        [Fact]
        public void RealProcessInsideFolderPreventsDeletion() {
            using var temp = new TempDir();
            var game = Directory.CreateDirectory(Path.Combine(temp.Root, "game")).FullName;
            var executable = Path.Combine(game, "ping.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), executable);
            var snapshot = MissingGamesCleanup.Scan(temp.Root, Array.Empty<GameInfo>());
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = executable,
                Arguments = "-t 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            try {
                Assert.False(process.HasExited);
                Assert.Equal(MissingGamesText.Busy, MissingGamesCleanup.DeleteFolder(snapshot, snapshot.Folders[0],
                    () => temp.Root, _ => false, _ => false));
                Assert.True(File.Exists(executable));
            }
            finally {
                if (!process.HasExited) {
                    process.Kill();
                }

                process.WaitForExit();
            }
        }

        [Fact]
        public void RootReplacedByJunctionAfterScanCannotDeleteExternalFiles() {
            using var temp = new TempDir();
            using var outside = new TempDir();
            var root = Directory.CreateDirectory(Path.Combine(temp.Root, "games")).FullName;
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var external = Directory.CreateDirectory(Path.Combine(outside.Root, "game")).FullName;
            var save = Path.Combine(external, "save.txt");
            File.WriteAllText(save, "keep");
            var snapshot = MissingGamesCleanup.Scan(root, Array.Empty<GameInfo>());
            Directory.Delete(game);
            Directory.Delete(root);
            CreateJunction(root, outside.Root);
            try {
                Assert.Equal(MissingGamesText.Failed, MissingGamesCleanup.DeleteFolder(snapshot, snapshot.Folders[0],
                    () => root, _ => false, _ => false, _ => false));
                Assert.Equal("keep", File.ReadAllText(save));
            }
            finally {
                Directory.Delete(root);
            }
        }

        [Fact]
        public void RootCannotBeSwappedWhileDeletionChecksRunningProcesses() {
            using var temp = new TempDir();
            using var outside = new TempDir();
            var root = Directory.CreateDirectory(Path.Combine(temp.Root, "games")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "game"));
            var save = outside.WriteFile("game/save.txt", "keep");
            var snapshot = MissingGamesCleanup.Scan(root, Array.Empty<GameInfo>());
            var replaced = false;
            var checkedProcesses = false;
            try {
                var result = MissingGamesCleanup.DeleteFolder(snapshot, snapshot.Folders[0], () => root, _ => false, _ => false, _ => {
                    checkedProcesses = true;
                    try {
                        Directory.Move(root, Path.Combine(temp.Root, "previous"));
                        CreateJunction(root, outside.Root);
                        replaced = true;
                    }
                    catch (IOException) {
                        // Удаление удерживает родительский каталог до конца обхода.
                    }

                    return false;
                }, _ => { });
                Assert.True(File.Exists(save), "подмена корня не должна удалять внешние файлы");
                Assert.True(checkedProcesses);
                Assert.Equal(MissingGamesText.Deleted, result);
                Assert.False(replaced);
            }
            finally {
                if (replaced) {
                    Directory.Delete(root);
                }
            }
        }

        [Fact]
        public void TraversedEntryCannotBeRenamedUntilItsHandleIsReleased() {
            using var temp = new TempDir();
            var folder = Directory.CreateDirectory(Path.Combine(temp.Root, "nested")).FullName;
            var moved = Path.Combine(temp.Root, "moved");
            using (var held = DeletionPathGuard.HoldEntry(folder)) {
                Assert.NotNull(held);
                Assert.Throws<IOException>(() => Directory.Move(folder, moved));
                // Другим играм не запрещена работа с файлами внутри общего корня.
                File.WriteAllText(Path.Combine(folder, "new.txt"), "new");
            }

            Directory.Move(folder, moved);
            Assert.Equal("new", File.ReadAllText(Path.Combine(moved, "new.txt")));
        }

        [Fact]
        public void DeletionSupportsPathsLongerThanMaxPath() {
            using var temp = new TempDir();
            var game = Directory.CreateDirectory(Path.Combine(temp.Root, "game")).FullName;
            var nested = Path.Combine(game, new string('a', 110), new string('b', 110));
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "save.txt"), "save");
            Assert.True(nested.Length > 260);
            var snapshot = MissingGamesCleanup.Scan(temp.Root, Array.Empty<GameInfo>());
            Assert.Equal(MissingGamesText.Deleted, MissingGamesCleanup.DeleteFolder(snapshot, snapshot.Folders[0],
                () => temp.Root, _ => false, _ => false, _ => false, _ => { }));
            Assert.False(Directory.Exists(game));
        }

        [Fact]
        public void DirectoryLinksAreRemovedWithoutTouchingTheirTargets() {
            using var temp = new TempDir();
            using var outside = new TempDir();
            File.WriteAllText(Path.Combine(outside.Root, "save.txt"), "keep");
            var game = Directory.CreateDirectory(Path.Combine(temp.Root, "game")).FullName;
            // Junction не требует прав администратора или включённого Developer Mode.
            CreateJunction(Path.Combine(game, "linked"), outside.Root);
            CreateJunction(Path.Combine(temp.Root, "linked-game"), outside.Root);
            var snapshot = MissingGamesCleanup.Scan(temp.Root, Array.Empty<GameInfo>());
            foreach (var folder in snapshot.Folders) {
                Assert.Equal(MissingGamesText.Deleted, MissingGamesCleanup.DeleteFolder(snapshot, folder,
                    () => temp.Root, _ => false, _ => false, _ => false, _ => { }));
            }

            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside.Root, "save.txt")));
            Assert.Empty(Directory.GetDirectories(temp.Root));
        }

        private static void CreateJunction(string link, string target) {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        private sealed class CatalogHandler : HttpMessageHandler {
            private int calls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                this.calls++;
                return Task.FromResult(new HttpResponseMessage(this.calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) {
                    Content = new StringContent("{\"items\":[]}"),
                });
            }
        }
    }
}
