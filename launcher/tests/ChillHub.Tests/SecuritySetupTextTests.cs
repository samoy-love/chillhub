namespace ChillHub.Tests {
    using System;
    using ChillHub.Security;
    using Xunit;

    public sealed class SecuritySetupTextTests {
        [Fact]
        public void VerifiedFoldersFinishWithAnExplicitRetryInstruction() {
            var result = SecuritySetupText.Summarize(new[] {
                new ExclusionResult("game", ExclusionStatus.Applied, ""),
                new ExclusionResult("launcher", ExclusionStatus.Existing, ""),
            }, 2, false);
            Assert.True(result.Complete);
            Assert.Contains("повторите загрузку", result.Message);
        }

        [Theory]
        [InlineData(ExclusionStatus.Unknown)]
        [InlineData(ExclusionStatus.Blocked)]
        [InlineData(ExclusionStatus.Failed)]
        [InlineData(ExclusionStatus.Manual)]
        [InlineData(ExclusionStatus.Cancelled)]
        [InlineData(ExclusionStatus.Removed)]
        public void PartialOrUnverifiedResultsKeepTheRetryFlow(ExclusionStatus status) {
            var result = SecuritySetupText.Summarize(new[] {
                new ExclusionResult("game", ExclusionStatus.Applied, ""),
                new ExclusionResult("launcher", status, ""),
            }, 2, false);
            Assert.False(result.Complete);
            Assert.Equal("Настройка не завершена", result.Title);
        }

        [Fact]
        public void EmptyMissingAndDuplicateRepliesCannotFinish() {
            Assert.False(SecuritySetupText.Summarize(Array.Empty<ExclusionResult>(), 0, false).Complete);
            var applied = new ExclusionResult("game", ExclusionStatus.Applied, "");
            Assert.False(SecuritySetupText.Summarize(new[] { applied }, 2, false).Complete);
            Assert.False(SecuritySetupText.Summarize(new[] { applied, applied with { Path = "GAME" } }, 2, false).Complete);
        }

        [Fact]
        public void CancellationDoesNotClaimSetupOrAskToRetryDownload() {
            var result = SecuritySetupText.Summarize(new[] { new ExclusionResult("game", ExclusionStatus.Cancelled, "") }, 1, false);
            Assert.False(result.Complete);
            Assert.Equal("Настройка отменена", result.Title);
            Assert.Contains("Настройки не изменены", result.Message);
            Assert.DoesNotContain("повторите загрузку", result.Message);
            var incomplete = SecuritySetupText.Summarize(new[] { new ExclusionResult("game", ExclusionStatus.Cancelled, "") }, 2, false);
            Assert.DoesNotContain("Настройки не изменены", incomplete.Message);
        }

        [Fact]
        public void RemovalRequiresConfirmedRemovalForEveryFolder() {
            Assert.True(SecuritySetupText.Summarize(new[] { new ExclusionResult("game", ExclusionStatus.Removed, "") }, 1, true).Complete);
            Assert.False(SecuritySetupText.Summarize(new[] { new ExclusionResult("game", ExclusionStatus.Applied, "") }, 1, true).Complete);
        }
    }
}
