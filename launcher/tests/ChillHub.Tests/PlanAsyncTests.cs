// <copyright file="PlanAsyncTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Планирование диффа — место, где решается, что качать. Ошибка тут либо тянет
    /// гигабайты заново, либо (хуже) оставляет испорченный файл на диске.
    /// Сеть не задействована: PlanAsync смотрит только на локальные файлы и манифест.
    /// </summary>
    public class PlanAsyncTests {
        [Fact]
        public async Task СовпавшийПоХешуФайлВПланНеПопадает() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var mf = PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path));

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            Assert.Empty(plan.Downloads);
            Assert.Equal(0, plan.TotalFilesToDownload);
            Assert.Equal(0, plan.TotalDownloadBytes);
        }

        [Fact]
        public async Task СовпавшийПоBlake3ФайлВПланНеПопадает() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var mf = PlanTestData.File("game.exe", new FileInfo(path).Length, sha256: null, blake3: TestHash.Blake3OfFile(path));

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            Assert.Empty(plan.Downloads);
        }

        [Fact]
        public async Task ФайлОтличающийсяПоСодержимомуПриТомЖеРазмереПопадаетВПлан() {
            // Ловит классическую ошибку «сравнили по размеру вместо хеша»:
            // размеры совпадают до байта, а содержимое разное.
            using var dir = new TempDir();
            var path = dir.WriteBytes("game.exe", Encoding.ASCII.GetBytes("AAAAAAAAAA"));
            var expectedSha = TestHash.Sha256OfFile(path);

            dir.WriteBytes("game.exe", Encoding.ASCII.GetBytes("BBBBBBBBBB"));
            Assert.Equal(10, new FileInfo(path).Length);

            var mf = PlanTestData.File("game.exe", 10, expectedSha);
            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            Assert.Single(plan.Downloads);
            Assert.Equal("game.exe", plan.Downloads[0].RelativePath);
        }

        [Fact]
        public async Task ОтсутствующийЛокальноФайлПопадаетВПлан() {
            using var dir = new TempDir();
            var mf = PlanTestData.File("data/pack.bin", 123, "aa");

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            Assert.Single(plan.Downloads);
            Assert.Equal("data/pack.bin", plan.Downloads[0].RelativePath);
            Assert.Equal(123, plan.TotalDownloadBytes);
            Assert.Equal(1, plan.TotalFilesToDownload);
        }

        [Fact]
        public async Task ФайлСДругимРазмеромПопадаетВПланБезЧтенияСДиска() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "короткий");
            var mf = PlanTestData.File("game.exe", new FileInfo(path).Length + 1000, "aa");

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            Assert.Single(plan.Downloads);
        }

        [Fact]
        public async Task БезХешейВМанифестеСравнениеИдётПоРазмеру() {
            using var dir = new TempDir();
            var same = dir.WriteFile("same.bin", "12345");
            dir.WriteFile("other.bin", "12345");

            var plan = await PlanTestData.PlanAsync(
                PlanTestData.Manifest(
                    PlanTestData.File("same.bin", new FileInfo(same).Length),
                    PlanTestData.File("other.bin", 999)),
                dir.Root);

            Assert.Single(plan.Downloads);
            Assert.Equal("other.bin", plan.Downloads[0].RelativePath);
        }

        [Fact]
        public async Task ЛишнийЛокальныйФайлПопадаетВToDelete() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "игра");
            dir.WriteFile("old/legacy.dll", "старьё");

            var mf = PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path));
            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            Assert.Equal(new[] { "old/legacy.dll" }, plan.ToDelete);
        }

        [Fact]
        public async Task ИсключённыйФайлFreeTpНеУдаляетсяИНеКачается() {
            // FreeTP/.hash намеренно исключён из синхронизации для пиратских сборок:
            // лаунчер не должен его ни проверять, ни скачивать, ни стирать.
            using var dir = new TempDir();
            dir.WriteFile("FreeTP/.hash", "не трогать");
            var path = dir.WriteFile("game.exe", "игра");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path)),
                PlanTestData.File("FreeTP/.hash", 42, "aa"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.Empty(plan.ToDelete);
            Assert.Empty(plan.Downloads);
        }

        [Fact]
        public async Task ФайлВПланеПолучаетКорректныйUrlИМетаданные() {
            using var dir = new TempDir();
            var mf = PlanTestData.File("data/sub dir/pack.bin", 7, "aabb", blake3: "ccdd");
            mf.Executable = true;

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            var task = Assert.Single(plan.Downloads);
            Assert.Equal(PlanTestData.ContentBase + "/data/sub dir/pack.bin", task.Url);
            Assert.Equal("aabb", task.Sha256);
            Assert.Equal("ccdd", task.Blake3);
            Assert.True(task.Executable);
            Assert.Equal(7, task.Size);
        }

        [Fact]
        public async Task ОбратныеСлешиВМанифестеПриводятсяКПрямым() {
            using var dir = new TempDir();
            var path = dir.WriteFile("data/pack.bin", "содержимое");

            var mf = PlanTestData.File(@"data\pack.bin", new FileInfo(path).Length, TestHash.Sha256OfFile(path));
            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);

            // Локальный файл найден по нормализованному пути, значит качать нечего,
            // и он же не считается «лишним».
            Assert.Empty(plan.Downloads);
            Assert.Empty(plan.ToDelete);
        }

        [Fact]
        public async Task ПустыеДиректорииИзМанифестаПопадаютВПлан() {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest();
            manifest.EmptyDirs = new List<string> { "/saves", @"logs\crash" };

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.Equal(new[] { "saves", "logs/crash" }, plan.EmptyDirsToCreate);
        }

        [Fact]
        public async Task ПланСодержитИдентификаторыИгрыИВерсии() {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest();

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.Equal(manifest.GameId, plan.GameId);
            Assert.Equal("1.0.0", plan.Version);
            Assert.Equal(dir.Root, plan.LocalRoot);
        }

        [Fact]
        public async Task НесуществующийКореньИгрыДаётПланНаПолнуюУстановку() {
            using var dir = new TempDir();
            var root = Path.Combine(dir.Root, "ещё-не-установлено");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", 10, "aa"),
                PlanTestData.File("data/pack.bin", 20, "bb"));

            var plan = await PlanTestData.PlanAsync(manifest, root);

            Assert.Equal(2, plan.TotalFilesToDownload);
            Assert.Equal(30, plan.TotalDownloadBytes);
            Assert.Empty(plan.ToDelete);
        }

        [Fact]
        public async Task ОтменаПрерываетПланирование() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "игра");
            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 4, "aa"));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PlanTestData.PlanAsync(manifest, dir.Root, ct: cts.Token));
        }

        [Fact]
        public async Task ПрогрессСообщаетОбЭтапеПроверки() {
            using var dir = new TempDir();
            var reports = new List<SyncProgress>();
            var manifest = PlanTestData.Manifest(
                PlanTestData.File("a.bin", 10, "aa"),
                PlanTestData.File("b.bin", 20, "bb"));

            // Progress<T> отправляет отчёты через контекст синхронизации асинхронно,
            // поэтому берём реализацию, которая складывает их прямо на месте.
            var options = new PlanOptions { Progress = new SyncProgressCollector(reports) };

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, options);

            Assert.NotEmpty(reports);
            Assert.All(reports, r => Assert.Equal("Checking", r.Stage));

            var last = reports[reports.Count - 1];
            Assert.Equal(2, last.TotalFiles);
            Assert.Equal(2, last.FilesDownloaded);
            Assert.Equal(30, last.TotalBytes);
            Assert.Equal(30, last.BytesDownloaded);
            Assert.Equal(2, plan.TotalFilesToDownload);
        }

        [Fact]
        public async Task КешХешейЗаполняетсяПослеПланированияИПереживаетПовторныйЗапуск() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path)));

            try {
                await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);

                var info = new FileInfo(path);
                var cache = FileHashCache.Load(manifest.GameId);
                Assert.True(cache.TryGet("game.exe", info.Length, info.LastWriteTimeUtc.Ticks, out var sha, out _));
                Assert.Equal(TestHash.Sha256OfFile(path), sha);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        [Fact]
        public async Task ForceRehash_ОбходитКешПодтверждающийИспорченныйФайл() {
            // Готовим ровно ту ситуацию, ради которой существует ForceRehash:
            // файл испорчен «на месте» — размер и время модификации прежние,
            // поэтому кеш уверенно подтверждает старый (правильный) хеш.
            using var dir = new TempDir();
            var path = dir.WriteBytes("game.exe", Encoding.ASCII.GetBytes("GOOD-DATA-0123"));
            var goodSha = TestHash.Sha256OfFile(path);
            var size = new FileInfo(path).Length;
            var mtime = new FileInfo(path).LastWriteTimeUtc;

            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", size, goodSha));

            try {
                // Первый проход: файл целый, план пуст, кеш запомнил хеш.
                var clean = await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);
                Assert.Empty(clean.Downloads);

                // Портим содержимое, сохраняя размер и время модификации.
                var corrupted = new byte[size];
                Array.Fill(corrupted, (byte)'Z');
                File.WriteAllBytes(path, corrupted);
                File.SetLastWriteTimeUtc(path, mtime);
                Assert.Equal(size, new FileInfo(path).Length);

                // Обычный проход: кеш попадает и «подтверждает» испорченный файл.
                // Это не ошибка планировщика, а осознанный компромисс ради скорости —
                // и ровно причина, по которой проверке целостности нужен ForceRehash.
                var fooled = await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);
                Assert.Empty(fooled.Downloads);

                // С ForceRehash кеш не спрашивают — повреждение обнаружено.
                var forced = await PlanTestData.PlanAsync(
                    manifest, dir.Root, new PlanOptions { ForceRehash = true }, keepCache: true);
                Assert.Single(forced.Downloads);
                Assert.Equal("game.exe", forced.Downloads[0].RelativePath);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        [Fact]
        public async Task ForceRehash_ОбновляетКешАктуальнымХешем() {
            using var dir = new TempDir();
            var path = dir.WriteBytes("game.exe", Encoding.ASCII.GetBytes("0123456789"));
            var goodSha = TestHash.Sha256OfFile(path);
            var mtime = new FileInfo(path).LastWriteTimeUtc;
            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 10, goodSha));

            try {
                await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);

                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("9876543210"));
                File.SetLastWriteTimeUtc(path, mtime);
                var corruptedSha = TestHash.Sha256OfFile(path);

                await PlanTestData.PlanAsync(manifest, dir.Root, new PlanOptions { ForceRehash = true }, keepCache: true);

                // После пересчёта в кеше должен лежать хеш того, что реально лежит на диске,
                // иначе следующая обычная синхронизация снова поверит устаревшей записи.
                var cache = FileHashCache.Load(manifest.GameId);
                Assert.True(cache.TryGet("game.exe", 10, mtime.Ticks, out var sha, out _));
                Assert.Equal(corruptedSha, sha);
                Assert.NotEqual(goodSha, sha);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        [Fact]
        public async Task КешЧиститсяОтИсчезнувшихФайловПриПланировании() {
            using var dir = new TempDir();
            var a = dir.WriteFile("a.bin", "первый");
            var b = dir.WriteFile("b.bin", "второй");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("a.bin", new FileInfo(a).Length, TestHash.Sha256OfFile(a)),
                PlanTestData.File("b.bin", new FileInfo(b).Length, TestHash.Sha256OfFile(b)));

            try {
                await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);

                var bInfo = new FileInfo(b);
                var bSize = bInfo.Length;
                var bTicks = bInfo.LastWriteTimeUtc.Ticks;
                File.Delete(b);

                // Второй проход: b.bin исчез с диска, запись о нём должна уйти из кеша.
                await PlanTestData.PlanAsync(manifest, dir.Root, keepCache: true);

                var cache = FileHashCache.Load(manifest.GameId);
                Assert.False(cache.TryGet("b.bin", bSize, bTicks, out _, out _));
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        /// <summary>
        /// Синхронный приёмник прогресса: отчёты нужны в тесте сразу, без прыжков по потокам.
        /// </summary>
        private sealed class SyncProgressCollector : IProgress<SyncProgress> {
            private readonly List<SyncProgress> sink;

            public SyncProgressCollector(List<SyncProgress> sink) => this.sink = sink;

            public void Report(SyncProgress value) => this.sink.Add(value);
        }
    }
}
