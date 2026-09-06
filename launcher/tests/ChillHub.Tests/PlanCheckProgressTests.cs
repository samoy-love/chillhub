// <copyright file="PlanCheckProgressTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Стадия «Проверка файлов» считает хеши в несколько потоков, а решение по каждому
    /// файлу принимает в один. Здесь проверяется, что от многопоточности не поехало
    /// ни одно из двух: ни состав плана, ни счётчик, по которому игрок видит прогресс.
    /// </summary>
    public class PlanCheckProgressTests {
        /// <summary>
        /// Счётчик проверенных файлов обязан прийти ровно к числу файлов сборки.
        /// <para>
        /// Файл, посчитанный заранее, легко учесть дважды — тогда проверка «кончается»
        /// на середине списка, а полоса упирается в конец и стоит. Ровно так же легко
        /// не учесть его вовсе: полоса замирает и игрок решает, что лаунчер завис.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПроверкаДоходитРовноДоЧислаФайлов() {
            using var dir = new TempDir();
            var files = new List<ManifestFile>();

            // Совпавшие по хешу, испорченные, отсутствующие и неприкосновенные —
            // все ветки цикла разом, потому что каждая считает прогресс сама.
            for (int i = 0; i < 40; i++) {
                var name = $"ok{i}.dat";
                var path = dir.WriteFile(name, new string('о', 100 + i));
                files.Add(PlanTestData.File(name, new FileInfo(path).Length, TestHash.Sha256OfFile(path)));
            }

            for (int i = 0; i < 10; i++) {
                var name = $"bad{i}.dat";
                var path = dir.WriteFile(name, new string('п', 200));
                files.Add(PlanTestData.File(name, new FileInfo(path).Length, new string('a', 64)));
            }

            for (int i = 0; i < 10; i++) {
                files.Add(PlanTestData.File($"gone{i}.dat", 512, new string('b', 64)));
            }

            // Файлы, которые лаунчер правит сам: их не сверяют, но в счётчик они входят
            for (int i = 0; i < 5; i++) {
                var name = $"keep{i}.dat";
                var path = dir.WriteFile(name, new string('р', 300));
                files.Add(PlanTestData.File(name, new FileInfo(path).Length, TestHash.Sha256OfFile(path)));
            }

            var seen = new List<SyncProgress>();
            var options = new PlanOptions {
                Progress = new SyncCollector(seen),
                PreservePaths = new List<string> { "keep0.dat", "keep1.dat" },
            };

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(files.ToArray()), dir.Root, options);

            var last = seen[seen.Count - 1];
            Assert.Equal(files.Count, last.TotalFiles);
            Assert.Equal(files.Count, last.FilesDownloaded);
            Assert.Equal(plan.TotalManifestBytes, last.BytesDownloaded);
            Assert.All(seen, p => Assert.InRange(p.FilesDownloaded, 0, files.Count));
            Assert.All(seen, p => Assert.InRange(p.BytesDownloaded, 0, plan.TotalManifestBytes));
        }

        /// <summary>
        /// Отчёт о проверке не уходит на каждый файл: каждый такой отчёт — это переход
        /// на поток интерфейса, и на большой сборке окно занималось только перерисовкой.
        /// </summary>
        [Fact]
        public async Task ОтчётОПроверкеНеУходитНаКаждыйФайл() {
            using var dir = new TempDir();
            var files = new List<ManifestFile>();
            for (int i = 0; i < 300; i++) {
                var name = $"f{i}.dat";
                var path = dir.WriteFile(name, new string('т', 64));
                files.Add(PlanTestData.File(name, new FileInfo(path).Length, TestHash.Sha256OfFile(path)));
            }

            var seen = new List<SyncProgress>();
            var options = new PlanOptions { Progress = new SyncCollector(seen) };

            await PlanTestData.PlanAsync(PlanTestData.Manifest(files.ToArray()), dir.Root, options);

            Assert.True(
                seen.Count < files.Count,
                $"отчётов {seen.Count} на {files.Count} файлов — дроссель не работает");
        }

        /// <summary>
        /// Порча файла обязана быть видна и тогда, когда хеш считался заранее и в чужом
        /// потоке: ради этого вердикта проверка целостности и существует.
        /// </summary>
        [Fact]
        public async Task ПорчаВиднаПриПодсчётеЗаранее() {
            using var dir = new TempDir();
            var files = new List<ManifestFile>();
            for (int i = 0; i < 30; i++) {
                var name = $"f{i}.dat";
                var path = dir.WriteFile(name, new string('у', 128 + i));
                var sha = i == 17 ? new string('c', 64) : TestHash.Sha256OfFile(path);
                files.Add(PlanTestData.File(name, new FileInfo(path).Length, sha));
            }

            var plan = await PlanTestData.PlanAsync(
                PlanTestData.Manifest(files.ToArray()),
                dir.Root,
                new PlanOptions { ForceRehash = true });

            Assert.Equal(1, plan.HashMismatches);
            Assert.Equal("f17.dat", Assert.Single(plan.Downloads).RelativePath);
        }

        /// <summary>
        /// Один и тот же корень обязан дать один и тот же план — и на холодном кеше,
        /// когда всё считается заранее в несколько потоков, и на тёплом, когда заранее
        /// не считается ничего. Разъехавшийся здесь порядок — это перемешанный список
        /// загрузки, то есть лишние гигабайты у каждого, кто обновляется.
        /// </summary>
        [Fact]
        public async Task ПланОдинаковНаХолодномИТёпломКеше() {
            using var dir = new TempDir();
            var files = new List<ManifestFile>();
            for (int i = 0; i < 25; i++) {
                var name = $"f{i}.dat";
                var path = dir.WriteFile(name, new string('ф', 90 + i));
                var sha = (i % 3 == 0) ? new string('d', 64) : TestHash.Sha256OfFile(path);
                files.Add(PlanTestData.File(name, new FileInfo(path).Length, sha));
            }

            var manifest = PlanTestData.Manifest(files.ToArray());
            try {
                var cold = await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);
                var warm = await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);

                Assert.Equal(
                    cold.Downloads.Select(d => d.RelativePath).ToList(),
                    warm.Downloads.Select(d => d.RelativePath).ToList());
                Assert.Equal(cold.HashMismatches, warm.HashMismatches);
                Assert.Equal(cold.TotalDownloadBytes, warm.TotalDownloadBytes);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        /// <summary>
        /// Отмена обязана останавливать проверку, а не возвращать план, построенный
        /// наполовину: по такому плану лаунчер снёс бы «лишние» файлы, которые просто
        /// не успели проверить.
        /// </summary>
        [Fact]
        public async Task ОтменаНеОставляетПоловинчатогоПлана() {
            using var dir = new TempDir();
            var files = new List<ManifestFile>();
            for (int i = 0; i < 50; i++) {
                var name = $"f{i}.dat";
                var path = dir.WriteFile(name, new string('х', 256));
                files.Add(PlanTestData.File(name, new FileInfo(path).Length, TestHash.Sha256OfFile(path)));
            }

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PlanTestData.PlanAsync(PlanTestData.Manifest(files.ToArray()), dir.Root, null, cts.Token));
        }

        /// <summary>Складывает отчёты о прогрессе в список, чтобы их можно было пересмотреть.</summary>
        private sealed class SyncCollector : IProgress<SyncProgress> {
            private readonly List<SyncProgress> seen;

            internal SyncCollector(List<SyncProgress> seen) => this.seen = seen;

            public void Report(SyncProgress value) {
                lock (this.seen) {
                    this.seen.Add(value);
                }
            }
        }
    }
}
