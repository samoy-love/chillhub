namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ChillHub.Security;
    using Xunit;

    public sealed class ExclusionVerificationTests {
        [Fact]
        public void DelayedDefenderRefreshIsNotReportedAsPolicyRejection() {
            var checks = new Queue<bool?>(new bool?[] { false, false, true });
            int waits = 0;
            Assert.True(ExclusionVerification.WaitForApplied(() => checks.Dequeue(), _ => waits++));
            Assert.Equal(2, waits);
            Assert.Empty(checks);
        }

        [Fact]
        public void PermanentRejectionStopsAfterFiveSecondsOfDelays() {
            var waited = TimeSpan.Zero;
            Assert.False(ExclusionVerification.WaitForApplied(() => false, delay => waited += delay));
            Assert.Equal(TimeSpan.FromSeconds(5), waited);
        }

        [Fact]
        public void UnavailableVerificationIsNeverPromotedToSuccess() {
            Assert.Null(ExclusionVerification.WaitForApplied(() => null, _ => throw new Exception("must not retry a failed check")));
        }

        [Fact]
        public void FirstInstallPreparesMissingRootsForDefenderVerification() {
            var root = Path.Combine(Path.GetTempPath(), "ChillHub-prepare-" + Guid.NewGuid().ToString("N"));
            var paths = new[] { Path.Combine(root, "games"), Path.Combine(root, "updates") };
            try {
                ExclusionPolicy.PrepareFolders(paths);
                Assert.All(paths, path => Assert.True(Directory.Exists(path)));
                File.WriteAllText(Path.Combine(paths[0], "keep.txt"), "keep");
                ExclusionPolicy.PrepareFolders(paths);
                Assert.Equal("keep", File.ReadAllText(Path.Combine(paths[0], "keep.txt")));
            }
            finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
        }

        [Fact]
        public void InvalidBatchCreatesNoFolders() {
            var root = Path.Combine(Path.GetTempPath(), "ChillHub-prepare-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<ArgumentException>(() => ExclusionPolicy.PrepareFolders(new[] { root, @"C:\" }));
            Assert.False(Directory.Exists(root));
        }
    }
}
