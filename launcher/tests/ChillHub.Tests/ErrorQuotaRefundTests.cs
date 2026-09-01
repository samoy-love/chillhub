// <copyright file="ErrorQuotaRefundTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Глобальная квота автоотчётов считает отчёты, ПРИНЯТЫЕ сервером, а не попытки их
    /// отправить.
    /// <para>
    /// Разница видна на машине без сети: квота — три отчёта за три минуты, очереди у
    /// автоотчётов нет. Если её тратят неудачные попытки, то три падения подряд
    /// выжигают окно целиком, и первый же отчёт, который дошёл бы, пользователь видит
    /// заглушённым — вместе с причиной, из-за которой он и обратился в поддержку.
    /// </para>
    /// </summary>
    public class ErrorQuotaRefundTests {
        /// <summary>Отчёт, не доехавший до сервера, слот квоты не тратит.</summary>
        [Fact]
        public async Task НеудачнаяОтправкаНеТратитКвоту() {
            using var scope = new QuotaSpy(_ => throw new HttpRequestException("сеть недоступна"));

            for (var i = 0; i < 3; i++) {
                await ErrorReporter.ReportForTestsAsync(new InvalidOperationException($"падение {i}"), "Тест", false);
            }

            Assert.Equal(3, scope.Attempts);
            Assert.Equal(0, scope.GlobalQuotaCount);
        }

        /// <summary>Отказ сервера — тоже не доставка: слот возвращается и по коду ответа.</summary>
        [Fact]
        public async Task ОтказСервераНеТратитКвоту() {
            using var scope = new QuotaSpy(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест", false);

            Assert.Equal(1, scope.Attempts);
            Assert.Equal(0, scope.GlobalQuotaCount);
        }

        /// <summary>
        /// Принятый отчёт слот тратит — иначе возврат отменил бы саму квоту, а она
        /// последняя защита от лавины отчётов при падении в цикле перезапусков.
        /// </summary>
        [Fact]
        public async Task ПринятыйОтчётТратитКвоту() {
            using var scope = new QuotaSpy(_ => new HttpResponseMessage(HttpStatusCode.OK));

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест", false);

            Assert.Equal(1, scope.GlobalQuotaCount);
        }

        /// <summary>
        /// Стенд: подставной транспорт вместо сети, обнулённый файл квоты в %APPDATA% и
        /// возврат всего тронутого на место. Путь к файлу квоты задан внутри
        /// продакшн-кода и наружу не выведен, поэтому тест правит настоящий файл.
        /// </summary>
        private sealed class QuotaSpy : IDisposable {
            private readonly string? savedQuota;
            private readonly string? savedEnv;
            private readonly string? savedApiBaseUrl;
            private readonly IDisposable httpSeam;
            private readonly HttpClient client;

            internal QuotaSpy(Func<HttpRequestMessage, HttpResponseMessage> reply) {
                this.savedQuota = File.Exists(QuotaPath) ? File.ReadAllText(QuotaPath, Encoding.UTF8) : null;
                this.savedEnv = Environment.GetEnvironmentVariable(ErrorReporter.EnvVar);
                this.savedApiBaseUrl = ConfigService.Current.ApiBaseUrl;

                // Общий рубильник тестового прогона глушит отправку целиком, а здесь
                // проверяется именно она.
                Environment.SetEnvironmentVariable(ErrorReporter.EnvVar, null);
                ConfigService.Current.ApiBaseUrl = "https://example.test";

                ErrorReporter.ResetThrottleForTests();
                this.WriteQuota(count: 0, windowStart: DateTime.UtcNow);

                this.client = new HttpClient(new SpyHandler(this, reply));
                this.httpSeam = ErrorReporter.OverrideHttpForTests(this.client);
            }

            /// <summary>Сколько запросов ушло в подставной транспорт.</summary>
            internal int Attempts { get; private set; }

            /// <summary>Сколько слотов квоты списано на текущий момент.</summary>
            internal int GlobalQuotaCount {
                get {
                    if (!File.Exists(QuotaPath)) {
                        return -1;
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(QuotaPath, Encoding.UTF8));
                    return doc.RootElement.GetProperty("Count").GetInt32();
                }
            }

            private static string QuotaDir => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");

            private static string QuotaPath => Path.Combine(QuotaDir, "report_rl.json");

            public void Dispose() {
                this.httpSeam.Dispose();
                this.client.Dispose();
                ErrorReporter.ResetThrottleForTests();

                Environment.SetEnvironmentVariable(ErrorReporter.EnvVar, this.savedEnv);
                ConfigService.Current.ApiBaseUrl = this.savedApiBaseUrl!;

                try {
                    if (this.savedQuota == null) {
                        if (File.Exists(QuotaPath)) {
                            File.Delete(QuotaPath);
                        }
                    }
                    else {
                        File.WriteAllText(QuotaPath, this.savedQuota, Encoding.UTF8);
                    }
                }
                catch {
                    // Вернуть файл не удалось — прогон из-за этого валить не нужно.
                }
            }

            private void WriteQuota(int count, DateTime windowStart) {
                Directory.CreateDirectory(QuotaDir);
                File.WriteAllText(
                    QuotaPath,
                    JsonSerializer.Serialize(new { Count = count, WindowStartUtc = windowStart }),
                    Encoding.UTF8);
            }

            private sealed class SpyHandler : HttpMessageHandler {
                private readonly QuotaSpy owner;
                private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;

                internal SpyHandler(QuotaSpy owner, Func<HttpRequestMessage, HttpResponseMessage> reply) {
                    this.owner = owner;
                    this.reply = reply;
                }

                protected override Task<HttpResponseMessage> SendAsync(
                    HttpRequestMessage request, CancellationToken cancellationToken) {
                    this.owner.Attempts++;
                    return Task.FromResult(this.reply(request));
                }
            }
        }
    }
}
