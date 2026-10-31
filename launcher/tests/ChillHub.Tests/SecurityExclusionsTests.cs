namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using ChillHub.Core.Sync;
    using ChillHub.Security;
    using Xunit;

    public sealed class SecurityExclusionsTests {
        [Fact]
        public void HelperCannotOpenDuringUpdateHandoff() {
            var name = "Local\\ChillHub.Tests.Security." + Guid.NewGuid().ToString("N");
            using (var update = SecurityUpdateGuard.TryAcquire(name)) {
                Assert.NotNull(update);
                Assert.Null(SecurityUpdateGuard.TryAcquire(name));
            }
            using var next = SecurityUpdateGuard.TryAcquire(name);
            Assert.NotNull(next);
        }

        [Theory]
        [InlineData(225)]
        [InlineData(226)]
        public void RecognizesWindowsCodesThroughWrappers(int code) {
            var failure = new IOException("arbitrary language", unchecked((int)0x80070000) | code);
            Assert.True(SecurityBlock.IsBlocked(new AggregateException(new InvalidOperationException("wrapper", failure))));
            Assert.False(SecurityBlock.IsBlocked(new IOException("Антивирус", unchecked((int)0x80070005))));
        }

        [Theory]
        [InlineData(@"C:\")]
        [InlineData(@"C:\Users\someone")]
        [InlineData(@"C:\Users.")]
        [InlineData(@"C:\Users ")]
        [InlineData(@"C:\games\.. ")]
        [InlineData(@"C:\games\steamapps")]
        [InlineData(@"C:\games\steamapps\common")]
        [InlineData(@"C:\games\*")]
        [InlineData(@"C:\games:stream")]
        [InlineData(@"\\server\game")]
        [InlineData(@"relative\game")]
        public void RejectsBroadOrSpecialPaths(string path) => Assert.Throws<ArgumentException>(() => ExclusionPolicy.Normalize(path));

        [Fact]
        public void PreservesExistingExclusionsAndDoesNotClaimThem() {
            using var dir = new TempDir();
            var provider = new FakeProvider { Covered = true };
            provider.Paths.Add(dir.Root);
            var result = Assert.Single(ExclusionOperation.Run(provider, new[] { dir.Root }, false));
            Assert.Equal(ExclusionStatus.Existing, result.Status);
            Assert.False(result.Added);
            Assert.Equal(0, provider.Adds);
            var journal = new SecurityJournal();
            journal.Record(new[] { result });
            Assert.Empty(journal.AddedPaths);
        }

        [Theory]
        [InlineData(true, ExclusionStatus.Applied)]
        [InlineData(false, ExclusionStatus.Blocked)]
        [InlineData(null, ExclusionStatus.Unknown)]
        public void VerifiesEffectiveSettingAndRetainsOwnership(bool? effective, ExclusionStatus status) {
            using var dir = new TempDir();
            var provider = new FakeProvider { AfterAdd = effective };
            var result = Assert.Single(ExclusionOperation.Run(provider, new[] { dir.Root }, false));
            Assert.Equal(status, result.Status);
            Assert.True(result.Added);
            Assert.Equal(1, provider.Adds);
        }

        [Fact]
        public void PolicyRefusalIsNotSuccessAndDoesNotPreventOtherPaths() {
            using var dir = new TempDir();
            var blocked = Path.Combine(dir.Root, "blocked");
            var accepted = Path.Combine(dir.Root, "accepted");
            var provider = new FakeProvider { Refuse = blocked };
            var results = ExclusionOperation.Run(provider, new[] { blocked, accepted }, false);
            Assert.Equal(ExclusionStatus.Blocked, results[0].Status);
            Assert.False(results[0].Added);
            Assert.Equal(ExclusionStatus.Applied, results[1].Status);
            var journal = new SecurityJournal();
            journal.Record(results);
            Assert.Equal(new[] { accepted }, journal.AddedPaths);
            var file = Path.Combine(dir.Root, "journal.json");
            journal.Save(file);
            Assert.Equal(journal.AddedPaths, SecurityJournal.Read(file).AddedPaths);
            var removed = ExclusionOperation.Run(provider, journal.AddedPaths, true);
            journal.Record(removed);
            Assert.Empty(journal.AddedPaths);
            Assert.Equal(ExclusionStatus.Removed, Assert.Single(removed).Status);
        }

        [Fact]
        public void IgnoredRemovalRetainsOwnership() {
            using var dir = new TempDir();
            var provider = new FakeProvider { IgnoreRemove = true };
            provider.Paths.Add(dir.Root);
            var journal = new SecurityJournal { AddedPaths = new() { dir.Root } };
            var result = Assert.Single(ExclusionOperation.Run(provider, journal.AddedPaths, true));
            journal.Record(new[] { result });
            Assert.Equal(ExclusionStatus.Blocked, result.Status);
            Assert.Single(journal.AddedPaths);
        }

        [Theory]
        [InlineData(225)]
        [InlineData(226)]
        public async Task BlockedDownloadStopsAfterFirstAttempt(int code) {
            using var dir = new TempDir();
            using var cache = new HashCacheScope();
            using var handler = new BlockingHandler(code);
            using var client = new HttpClient(handler);
            var sync = new SimpleSyncService(client);
            var plan = new DiffPlan {
                GameId = cache.GameId,
                LocalRoot = dir.Root,
                TotalDownloadBytes = 4,
                TotalFilesToDownload = 1,
                Downloads = new List<FileTask> { new() { RelativePath = "blocked.dll", Size = 4, Url = "https://example.invalid/file", Sha256 = new string('0', 64) } },
            };
            var error = await Assert.ThrowsAnyAsync<Exception>(() => sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None));
            Assert.True(SecurityBlock.IsBlocked(error));
            Assert.Contains("blocked.dll", SecurityBlock.Describe(error));
            Assert.Equal(1, handler.Requests);
            Assert.False(File.Exists(Path.Combine(dir.Root, "blocked.dll")));
        }

        [Fact]
        public void SelfUpdateRetryReusesOnlyVerifiedFiles() {
            using var dir = new TempDir();
            var bytes = new byte[] { 1, 2, 3, 4 };
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            dir.WriteBytes("ready.dll", bytes);
            dir.WriteBytes("bad.dll", new byte[] { 4, 3, 2, 1 });
            dir.WriteBytes("partial.dll.part", new byte[] { 1, 2 });
            var plan = new DiffPlan {
                LocalRoot = dir.Root,
                Downloads = new[] { "ready.dll", "bad.dll", "partial.dll" }
                    .Select(name => new FileTask { RelativePath = name, Size = 4, Sha256 = hash }).ToList(),
            };
            Core.SelfUpdate.SelfUpdateDownloader.ReuseDownloadedFiles(plan);
            Assert.Equal(new[] { "bad.dll", "partial.dll" }, plan.Downloads.Select(file => file.RelativePath));
            Assert.Equal(8, plan.TotalDownloadBytes);
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(dir.PathTo("partial.dll.part")));
        }

        private sealed class BlockingHandler(int code) : HttpMessageHandler {
            internal int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                Interlocked.Increment(ref this.Requests);
                throw new IOException("blocked", unchecked((int)0x80070000) | code);
            }
        }

        private sealed class FakeProvider : IAntivirusProvider {
            public string Id => "fake";
            internal List<string> Paths = new();
            internal bool? Covered;
            internal bool? AfterAdd = true;
            internal string? Refuse;
            internal bool IgnoreRemove;
            internal int Adds;
            public IReadOnlyList<string> ReadPaths() => this.Paths.ToArray();
            public void Add(string path) {
                if (path == this.Refuse) { throw new UnauthorizedAccessException("policy"); }
                this.Adds++;
                this.Paths.Add(path);
                this.Covered = this.AfterAdd;
            }
            public void Remove(string path) { if (!this.IgnoreRemove) { this.Paths.Remove(path); } }
            public bool? Check(string path) => this.Covered;
        }
    }
}
