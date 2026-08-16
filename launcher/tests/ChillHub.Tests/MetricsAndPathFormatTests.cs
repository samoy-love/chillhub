// <copyright file="MetricsAndPathFormatTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

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

        /// <summary>Выключенная отправка не ходит в сеть и не бросает исключений.</summary>
        [Fact]
        public void ВыключеннаяОтправкаНичегоНеДелает() {
            using var scope = new MetricsScope(env: "0", sendUsageMetrics: true);

            MetricsService.LauncherStart();
            MetricsService.GameLaunch("game", "1.0.0");
            MetricsService.GameSession("game", 60000);
            MetricsService.GameInstall("game", "1.0.0", "ok", 100, 200);
            MetricsService.GameUpdate("game", "1.0.0", "ok", 100, 200);
            MetricsService.IntegrityCheck("game", "1.0.0", ok: true, filesTotal: 10, hashMismatches: 0);
            MetricsService.Error("sync_hash_mismatch", "game");
        }

        /// <summary>Задвоенные разделители в показываемом пути схлопываются.</summary>
        [Theory]
        [InlineData(@"C:\Games\ChillHub", "C:/Games/ChillHub")]
        [InlineData(@"C:\\Games\\ChillHub", "C:/Games/ChillHub")]
        [InlineData("C://Games//ChillHub", "C:/Games/ChillHub")]
        public void ПутьПоказываетсяБезЗадвоенныхРазделителей(string input, string expected) {
            Assert.Equal(expected, HomeFormat.NormalizeDisplayPath(input));
        }

        /// <summary>
        /// Ведущий «\\» сетевого пути сохраняется: это синтаксис UNC, а не дубль.
        /// Схлопнуть его — значит показать путь, указывающий уже не туда.
        /// </summary>
        [Theory]
        [InlineData(@"\\nas\games", "//nas/games")]
        [InlineData(@"\\nas\\games\\ChillHub", "//nas/games/ChillHub")]
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
            private readonly bool previousSetting;

            internal MetricsScope(string? env, bool sendUsageMetrics) {
                this.previousEnv = Environment.GetEnvironmentVariable(MetricsService.EnvVar);
                this.previousSetting = ConfigService.Current.SendUsageMetrics;

                Environment.SetEnvironmentVariable(MetricsService.EnvVar, env);
                ConfigService.Current.SendUsageMetrics = sendUsageMetrics;
            }

            public void Dispose() {
                Environment.SetEnvironmentVariable(MetricsService.EnvVar, this.previousEnv);
                ConfigService.Current.SendUsageMetrics = this.previousSetting;
            }
        }
    }
}
