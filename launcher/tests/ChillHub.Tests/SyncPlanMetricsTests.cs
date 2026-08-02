// <copyright file="SyncPlanMetricsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// План синхронизации считает не только «что скачать», но и то, из чего
    /// складывается продуктовая метрика экономии трафика: полный вес сборки и
    /// число файлов с разошедшимся хешем.
    /// </summary>
    public class SyncPlanMetricsTests {
        [Fact]
        public async Task План_ЗнаетПолныйВесСборки_ДажеБезПодпискиНаПрогресс() {
            // Полный вес раньше считался только при наличии Progress. Тихие
            // сценарии (самообновление, проверка целостности) остались бы без
            // числа, с которым сравнивают фактическую загрузку.
            using var dir = new TempDir();
            var path = dir.WriteBytes("keep.dat", Encoding.ASCII.GetBytes("0123456789"));
            var sha = TestHash.Sha256OfFile(path);

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("keep.dat", 10, sha),
                PlanTestData.File("new.dat", 90, "0000000000000000000000000000000000000000000000000000000000000000"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.Equal(100, plan.TotalManifestBytes);
            Assert.Equal(2, plan.TotalManifestFiles);

            // Именно это соотношение и есть смысл лаунчера: качается 90 из 100.
            Assert.Equal(90, plan.TotalDownloadBytes);
            Assert.Equal(1, plan.TotalFilesToDownload);
        }

        [Fact]
        public async Task План_СчитаетРасхожденияХешейОтдельноОтОтсутствующихФайлов() {
            // «Файла нет» и «файл есть, но не тот» — разные истории: первая
            // штатна для установки, вторая означает порчу.
            using var dir = new TempDir();
            var path = dir.WriteBytes("game.exe", Encoding.ASCII.GetBytes("GOOD-DATA-0123"));
            var size = new FileInfo(path).Length;
            var goodSha = TestHash.Sha256OfFile(path);

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", size, goodSha),
                PlanTestData.File("absent.dat", 5, "1111111111111111111111111111111111111111111111111111111111111111"));

            var mtime = new FileInfo(path).LastWriteTimeUtc;
            var corrupted = new byte[size];
            System.Array.Fill(corrupted, (byte)'Z');
            File.WriteAllBytes(path, corrupted);
            File.SetLastWriteTimeUtc(path, mtime);

            var plan = await PlanTestData.PlanAsync(
                manifest, dir.Root, new PlanOptions { ForceRehash = true });

            Assert.Equal(2, plan.Downloads.Count);
            Assert.Equal(1, plan.HashMismatches);
        }

        [Fact]
        public async Task План_БезРасхождений_НеСчитаетИх() {
            using var dir = new TempDir();
            var path = dir.WriteBytes("game.exe", Encoding.ASCII.GetBytes("0123456789"));
            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", 10, TestHash.Sha256OfFile(path)));

            var plan = await PlanTestData.PlanAsync(
                manifest, dir.Root, new PlanOptions { ForceRehash = true });

            Assert.Empty(plan.Downloads);
            Assert.Equal(0, plan.HashMismatches);
            Assert.Equal(10, plan.TotalManifestBytes);
        }
    }
}
