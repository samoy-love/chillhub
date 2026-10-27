// <copyright file="SelfUpdateTimeoutTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Sync;

    using Xunit;

    public class SelfUpdateTimeoutTests {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ЗависшийЗапросОтменяетсяИПроверкуМожноПовторить(bool hangManifest) {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            using var handler = new HangingHandler(hangManifest);
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var checker = new SelfUpdateChecker(http, new SimpleSyncService(http),
                () => "https://example.test", stand.Paths, stand.Attempts);

            var result = await checker.CheckAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(handler.Cancelled);
            Assert.Equal(SelfUpdateState.CheckFailed, result.State);
            Assert.Contains("Не удалось проверить", SelfUpdatePrecheck.ManualStatusText(result.State));
            Assert.Equal(0, stand.Attempts.Get("1.2.4"));

            handler.Recovered = true;
            var retry = await checker.CheckAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SelfUpdateState.UpToDate, retry.State);
        }

        private sealed class HangingHandler(bool hangManifest) : HttpMessageHandler {
            internal bool Recovered { get; set; }

            internal bool Cancelled { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                if (!this.Recovered && (!hangManifest || !request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal))) {
                    try {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException) {
                        this.Cancelled = true;
                        throw;
                    }
                }

                var version = this.Recovered ? "1.2.3" : "1.2.4";
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{\"version\":\"" + version + "\"}"),
                };
            }
        }
    }
}
