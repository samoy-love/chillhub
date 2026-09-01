// <copyright file="SelfUpdateManifestGateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Проверка манифеста на пути самообновления.
    /// <para>
    /// Манифест лаунчера определяет, что ляжет поверх каталога установки вместо
    /// ChillHub.exe, и второго рубежа у него нет: сверка скачанного файла запись без
    /// хешей пропускает молча — сверять не с чем. Поэтому проверяются обе точки, через
    /// которые манифест попадает в код, трогающий диск: загрузка манифеста и построение
    /// плана самообновления, которое идёт мимо SimpleSyncService.PlanAsync.
    /// </para>
    /// </summary>
    public class SelfUpdateManifestGateTests {
        /// <summary>
        /// Настоящая загрузка манифеста (не подставной ISyncService) отвергает запись
        /// без хешей: до сих пор эту точку входа не звал ни один тест.
        /// </summary>
        [Fact]
        public async Task ЗагрузкаМанифестаОтвергаетЗаписьБезХешей() {
            const string json = """
                {"version":"1.2.4","gameId":"launcher","files":[{"path":"ChillHub.exe","size":10}]}
                """;
            using var http = new HttpClient(SelfUpdateHandler.Json(json));
            var sync = new SimpleSyncService(http);

            var ex = await Assert.ThrowsAsync<ManifestValidationException>(
                () => sync.GetManifestAsync("https://example.test/manifests/launcher/1.2.4.json", CancellationToken.None));

            Assert.Contains("нет ни одного хеша", ex.Message, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Обычный манифест той же дорогой проходит: проверка отвергает форму записи,
        /// а не сам факт загрузки.
        /// </summary>
        [Fact]
        public async Task ЗагрузкаМанифестаПропускаетЗаписьСХешем() {
            const string json = """
                {"version":"1.2.4","gameId":"launcher","files":[{"path":"ChillHub.exe","size":10,"sha256":"aa"}]}
                """;
            using var http = new HttpClient(SelfUpdateHandler.Json(json));
            var sync = new SimpleSyncService(http);

            var manifest = await sync.GetManifestAsync(
                "https://example.test/manifests/launcher/1.2.4.json", CancellationToken.None);

            Assert.Equal("ChillHub.exe", Assert.Single(manifest.Files).Path);
        }

        /// <summary>
        /// План самообновления строится в обход SimpleSyncService.PlanAsync, то есть в
        /// обход его проверки. Запись без хешей обязана остановить обновление здесь же:
        /// иначе непроверяемый файл скачается и ляжет в каталог установки лаунчера.
        /// </summary>
        [Fact]
        public async Task ПланСамообновленияОтвергаетЗаписьБезХешей() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => new Manifest {
                    Version = "1.2.4",
                    GameId = "launcher",
                    Files = new List<ManifestFile> {
                        new ManifestFile { Path = "ChillHub.dll", Size = 10 },
                    },
                },
            };

            var result = await SelfUpdateDownloadTests.NewDownloader(stand, sync, out var ui).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.ManifestRejected, result.Result);
            Assert.False(result.Downloaded);
            Assert.Null(sync.LastPlan);
            Assert.Contains("нет ни одного хеша", ui.LastStatus!, System.StringComparison.Ordinal);
        }
    }
}
