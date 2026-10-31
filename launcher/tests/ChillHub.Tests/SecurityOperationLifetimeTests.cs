namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using ChillHub.Security;
    using Xunit;

    public sealed class SecurityOperationLifetimeTests {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SuccessAndFailureWaitForTheChildBeforeReleasingTheCaller(bool fail) {
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool disconnected = false;
            var failure = new IOException("journal unavailable");
            var operation = SecurityOperationLifetime.RunAsync(
                () => fail ? Task.FromException<int>(failure) : Task.FromResult(7),
                () => disconnected = true,
                () => { Assert.True(disconnected); waiting.SetResult(true); return exited.Task; });
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            exited.SetResult(true);
            if (fail) { Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => operation)); }
            else { Assert.Equal(7, await operation); }
        }

        [Fact]
        public async Task DisconnectFailureStillWaitsForTheChild() {
            bool waited = false;
            await Assert.ThrowsAsync<IOException>(() => SecurityOperationLifetime.RunAsync(
                () => Task.FromResult(1),
                () => throw new IOException("disconnect failed"),
                () => { waited = true; return Task.CompletedTask; }));
            Assert.True(waited);
        }
    }
}
