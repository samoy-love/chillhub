// <copyright file="MetricsAndPathFormatTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Metrics;

    using Xunit;

    /// <summary>
    /// Отправка обезличенной статистики и показ путей пользователю.
    /// <para>
    /// Статистика — единственное, что лаунчер шлёт на сервер без прямой просьбы
    /// пользователя, поэтому отключение обязано работать безоговорочно.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class MetricsAndPathFormatTests {
        /// <summary>
        /// Сам прогон тестов статистику не шлёт.
        /// <para>
        /// Стеречь это приходится тестом, потому что нарушение не видно ниоткуда:
        /// отправка «выстрелил и забыл», ошибки сети проглочены, в логе ни строки.
        /// Пока рубильника здесь не было, тесты сотнями слали game_session и ошибки
        /// на боевой сервер, и панель показывала их как игроков.
        /// </para>
        /// </summary>
        [Fact]
        public void ПрогонТестовСтатистикуНеШлёт() {
            Assert.False(MetricsService.Enabled);
        }

        /// <summary>Тумблер в настройках выключает отправку.</summary>
        [Fact]
        public void ВыключенныйТумблерЗапрещаетОтправку() {
            using var scope = new MetricsScope(env: null, sendUsageMetrics: false);

            Assert.False(MetricsService.Enabled);
        }

        /// <summary>При включённом тумблере и без запретов в окружении отправка разрешена.</summary>
        [Fact]
        public void ВключённыйТумблерРазрешаетОтправку() {
            using var scope = new MetricsScope(env: null, sendUsageMetrics: true);

            Assert.True(MetricsService.Enabled);
        }

        /// <summary>
        /// CHILLHUB_METRICS=0 перекрывает настройку: отладочные и автоматические запуски
        /// не должны пачкать статистику, даже если конфиг остался пользовательским.
        /// </summary>
        [Fact]
        public void ПеременнаяОкруженияПерекрываетНастройку() {
            using var scope = new MetricsScope(env: "0", sendUsageMetrics: true);

            Assert.False(MetricsService.Enabled);
        }

        /// <summary>Выключает именно «0», а не любое значение: иначе «1» тоже гасило бы отправку.</summary>
        [Theory]
        [InlineData("1")]
        [InlineData("")]
        [InlineData("false")]
        [InlineData("00")]
        public void ВыключаетТолькоНоль(string value) {
            using var scope = new MetricsScope(env: value, sendUsageMetrics: true);

            Assert.True(MetricsService.Enabled);
        }

        /// <summary>Пробелы вокруг значения не мешают выключить отправку.</summary>
        [Theory]
        [InlineData(" 0")]
        [InlineData("0 ")]
        [InlineData("  0  ")]
        public void ПробелыВокругНуляНеМешают(string value) {
            using var scope = new MetricsScope(env: value, sendUsageMetrics: true);

            Assert.False(MetricsService.Enabled);
        }

        /// <summary>
        /// Идентификатор установки постоянен в пределах процесса. Новый GUID на каждый
        /// вызов раздул бы счётчик уникальных установок до числа событий.
        /// </summary>
        [Fact]
        public void ИдентификаторУстановкиПостоянен() {
            Assert.Equal(MetricsService.InstallId, MetricsService.InstallId);
        }

        /// <summary>
        /// Идентификатор — либо пустая строка (сохранить не удалось), либо GUID без дефисов.
        /// Ничего производного от имени пользователя или машины в нём быть не может.
        /// </summary>
        [Fact]
        public void ИдентификаторУстановкиОбезличен() {
            var id = MetricsService.InstallId;
            if (id.Length == 0) {
                return;
            }

            Assert.Equal(32, id.Length);
            Assert.DoesNotContain('-', id);
            Assert.True(Guid.TryParseExact(id, "N", out _), $"'{id}' не похож на GUID");
            Assert.DoesNotContain(Environment.UserName, id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, id, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// CHILLHUB_METRICS=test отправку не глушит: прогон обязан проверять и приём
        /// событий тоже, иначе сломавшийся эндпоинт заметит только пользователь.
        /// </summary>
        [Fact]
        public void СлужебныйПрогонПродолжаетОтправлять() {
            using var scope = new MetricsScope(env: "test", sendUsageMetrics: true);

            Assert.True(MetricsService.Enabled);
            Assert.True(MetricsService.Synthetic);
        }

        /// <summary>
        /// Служебный прогон помечает СВОЙ ЖЕ идентификатор: по нему сервер и отличает
        /// автотест от игрока, потому что больше отличить их нечем — эндпоинт
        /// публичный, а версия сборки у своей сборки бывает какой угодно.
        /// </summary>
        [Fact]
        public void СлужебныйПрогонПомечаетИдентификатор() {
            using var scope = new MetricsScope(env: "test", sendUsageMetrics: true);

            Assert.StartsWith(MetricsService.TestInstallIdPrefix, MetricsService.ReportedInstallId, StringComparison.Ordinal);
            Assert.EndsWith(MetricsService.InstallId, MetricsService.ReportedInstallId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Обычный запуск ничего не помечает: попади префикс к игроку — его перестала
        /// бы считать вся статистика разом.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("1")]
        [InlineData("testing")]
        public void ОбычныйЗапускИдентификаторНеМетит(string? env) {
            using var scope = new MetricsScope(env: env, sendUsageMetrics: true);

            Assert.False(MetricsService.Synthetic);
            Assert.Equal(MetricsService.InstallId, MetricsService.ReportedInstallId);
        }

        /// <summary>Запрет останавливает все события; без запрета те же вызовы доходят до транспорта.</summary>
        [Theory]
        [InlineData("0", false)]
        [InlineData("1", true)]
        public async Task ОтправкаСоблюдаетПеременнуюОкружения(string env, bool shouldSend) {
            using var config = new ConfigDirsScope();
            ConfigService.Current.ApiBaseUrl = "https://metrics.example.test/api";
            using var scope = new MetricsScope(env, sendUsageMetrics: true);
            using var handler = new MetricsHandler();
            using var client = new HttpClient(handler);
            var pending = new List<Func<Task>>();
            using var sending = MetricsService.OverrideSendingForTests(client, pending.Add);

            MetricsService.LauncherStart();
            MetricsService.GameLaunch("game", "1.0.0");
            MetricsService.GameSession("game", 60000);
            MetricsService.GameInstall("game", "1.0.0", "ok", 100, 200);
            MetricsService.GameUpdate("game", "1.0.0", "ok", 100, 200);
            MetricsService.IntegrityCheck("game", "1.0.0", ok: true, filesTotal: 10, hashMismatches: 0);
            MetricsService.Error("sync_hash_mismatch", "game");

            // Очередь собирается синхронно. Даже ошибочно разрешённые отправки
            // выполнятся до проверки, а не после завершения теста.
            foreach (var send in pending) {
                await send();
            }

            if (shouldSend) {
                Assert.Equal(7, pending.Count);
                Assert.Equal(new[] {
                    "launcher_start", "game_launch", "game_session", "game_install",
                    "game_update", "integrity_check", "error",
                }, handler.Events);
                Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Post, method));
                Assert.All(handler.Urls, url => Assert.Equal("https://metrics.example.test/api/metrics/report", url));
            }
            else {
                Assert.Empty(handler.Events);
                Assert.Empty(pending);
            }
        }

        private sealed class MetricsHandler : HttpMessageHandler {
            internal List<string> Events { get; } = new();

            internal List<HttpMethod> Methods { get; } = new();

            internal List<string> Urls { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                this.Methods.Add(request.Method);
                this.Urls.Add(request.RequestUri!.AbsoluteUri);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                this.Events.Add(body.RootElement.GetProperty("event").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        /// <summary>Задвоенные разделители в показываемом пути схлопываются.</summary>
        [Theory]
        [InlineData(@"C:\Games\ChillHub", @"C:\Games\ChillHub")]
        [InlineData(@"C:\\Games\\ChillHub", @"C:\Games\ChillHub")]
        [InlineData("C://Games//ChillHub", @"C:\Games\ChillHub")]
        public void ПутьПоказываетсяБезЗадвоенныхРазделителей(string input, string expected) {
            Assert.Equal(expected, HomeFormat.NormalizeDisplayPath(input));
        }

        /// <summary>
        /// Ведущий «\\» сетевого пути сохраняется: это синтаксис UNC, а не дубль.
        /// Схлопнуть его — значит показать путь, указывающий уже не туда.
        /// </summary>
        [Theory]
        [InlineData(@"\\nas\games", @"\\nas\games")]
        [InlineData(@"\\nas\\games\\ChillHub", @"\\nas\games\ChillHub")]
        public void СетевойПутьСохраняетДвойнойПрефикс(string input, string expected) {
            Assert.Equal(expected, HomeFormat.NormalizeDisplayPath(input));
        }

        /// <summary>Пустой путь показывается пустой строкой, а не падает.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойПутьДаётПустуюСтроку(string? input) {
            Assert.Equal(string.Empty, HomeFormat.NormalizeDisplayPath(input!));
        }

        /// <summary>Тот же разбор для windows-формы пути, которую подставляют в настройки.</summary>
        [Theory]
        [InlineData(@"C:\\Games\\ChillHub", @"C:\Games\ChillHub")]
        [InlineData(@"C:\Games\ChillHub", @"C:\Games\ChillHub")]
        [InlineData(@"\\nas\\games", @"\\nas\games")]
        public void WindowsПутьНормализуется(string input, string expected) {
            Assert.Equal(expected, HomeFormat.NormalizeWindowsPath(input));
        }

        /// <summary>
        /// Временно подменяет переменную окружения и тумблер статистики.
        /// Конфиг правится только в памяти: запись ушла бы в настоящий config.json.
        /// </summary>
        private sealed class MetricsScope : IDisposable {
            private readonly string? previousEnv;

            /// <param name="env">Значение CHILLHUB_METRICS на время области.</param>
            /// <param name="sendUsageMetrics">
            /// Оставлен ради читаемости вызовов: тумблера в настройках больше нет,
            /// статистика включена всегда, и «выключено» теперь выражается только
            /// переменной окружения.
            /// </param>
            internal MetricsScope(string? env, bool sendUsageMetrics) {
                this.previousEnv = Environment.GetEnvironmentVariable(MetricsService.EnvVar);
                Environment.SetEnvironmentVariable(
                    MetricsService.EnvVar, sendUsageMetrics ? env : "0");
            }

            public void Dispose() {
                Environment.SetEnvironmentVariable(MetricsService.EnvVar, this.previousEnv);
            }
        }
    }
}
