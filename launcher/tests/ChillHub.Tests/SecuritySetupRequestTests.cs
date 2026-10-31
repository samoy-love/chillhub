namespace ChillHub.Tests {
    using System;
    using System.IO;
    using ChillHub.Security;
    using Xunit;

    public sealed class SecuritySetupRequestTests {
        [Fact]
        public void InstallerAndFirstLaunchOfferAllThreeRoots() {
            var request = SecuritySetupRequest.Parse(new[] { "--offer", "--games", @"D:\Games\ChillHub", "--updates", @"C:\Temp\ChillHub\SelfUpdate" }, @"C:\Apps\ChillHub", @"C:\unused");
            Assert.True(request.OfferOnce);
            Assert.False(request.Remove);
            Assert.Equal(new[] { @"C:\Apps\ChillHub", @"D:\Games\ChillHub", @"C:\Temp\ChillHub\SelfUpdate" }, request.Paths);
        }

        [Fact]
        public void ErrorRecoveryKeepsUpdateRootAlongsideExternalGame() {
            var request = SecuritySetupRequest.Parse(new[] { "--extra", @"D:\Steam\steamapps\common\Game" }, @"C:\Apps\ChillHub", @"C:\Temp\ChillHub\SelfUpdate");
            Assert.False(request.OfferOnce);
            Assert.Contains(@"D:\Steam\steamapps\common\Game", request.Paths);
            Assert.Contains(@"C:\Temp\ChillHub\SelfUpdate", request.Paths);
        }

        [Theory]
        [InlineData("--games")]
        [InlineData("--updates")]
        [InlineData("--extra")]
        [InlineData("--unexpected")]
        public void IncompleteRequestDoesNotSilentlyDropAFolder(string argument) {
            Assert.Throws<ArgumentException>(() => SecuritySetupRequest.Parse(new[] { argument }, @"C:\Apps\ChillHub", @"C:\Temp\ChillHub"));
        }

        [Fact]
        public void AutomaticOfferCannotRemoveExclusions() {
            Assert.Throws<ArgumentException>(() => SecuritySetupRequest.Parse(new[] { "--offer", "--remove" }, @"C:\Apps\ChillHub", @"C:\Temp\ChillHub"));
        }

        [Fact]
        public void InstallerChoiceSurvivesRereadingAndLaterOperationResults() {
            var root = Path.Combine(Path.GetTempPath(), "ChillHub-onboarding-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(root, "security-exclusions.json");
            try {
                Assert.False(SecurityJournal.Read(file).IntroductionShown);
                SecurityJournal.Update(file, journal => journal.IntroductionShown = true);
                // Dismissal is remembered without claiming any successful exclusions.
                var afterInstall = SecurityJournal.Read(file);
                Assert.True(afterInstall.IntroductionShown);
                Assert.Empty(afterInstall.AddedPaths);
                SecurityJournal.Update(file, journal => journal.Record(new[] { new ExclusionResult(root, ExclusionStatus.Applied, "ok", true) }));
                Assert.True(SecurityJournal.Read(file).IntroductionShown);
                SecurityJournal.Update(file, journal => journal.Record(new[] { new ExclusionResult(root, ExclusionStatus.Removed, "ok") }));
                Assert.True(SecurityJournal.Read(file).IntroductionShown);
                Assert.Empty(SecurityJournal.Read(file).AddedPaths);
            }
            finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
        }

        [Fact]
        public void ExistingUsersReceiveTheOfferWithoutLosingOwnedExclusions() {
            var root = Path.Combine(Path.GetTempPath(), "ChillHub-onboarding-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(root, "security-exclusions.json");
            Directory.CreateDirectory(root);
            try {
                File.WriteAllText(file, "{\"AddedPaths\":[\"C:\\\\Games\\\\ChillHub\"]}");
                Assert.False(SecurityJournal.Read(file).IntroductionShown);
                SecurityJournal.Update(file, journal => journal.IntroductionShown = true);
                Assert.True(SecurityJournal.Read(file).IntroductionShown);
                Assert.Equal(@"C:\Games\ChillHub", Assert.Single(SecurityJournal.Read(file).AddedPaths));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
