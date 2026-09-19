// <copyright file="BlockSyncTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Обновление по блокам целиком: план → сборка файла из старой копии и Range-кусков
    /// → сверка → замена. Сервер поддельный, но честный в том, что важно: отвечает
    /// на Range кодом 206 с Content-Range, а по команде — игнорирует Range, отдаёт
    /// не тот кусок, портит байты или рвёт соединение.
    /// </summary>
    public sealed class BlockSyncTests : IDisposable {
        private const int Bs = (int)BlockList.MinBlockSize;
        private const string Base = "https://example.invalid/content";

        private readonly TempDir dir = new TempDir();
        private readonly string gameId = "blocks-" + Guid.NewGuid().ToString("N");
        private readonly Func<int, TimeSpan> savedDelay = BlockDelta.RetryDelay;

        public BlockSyncTests() {
            BlockDelta.RetryDelay = _ => TimeSpan.Zero;
        }

        public void Dispose() {
            BlockDelta.RetryDelay = this.savedDelay;
            FileHashCache.Remove(this.gameId);
            this.dir.Dispose();
        }

        /// <summary>
        /// Главный сценарий: в большом файле поменялся один блок — по сети едет он
        /// один, остальное берётся из старой копии. Мелкий файл без блоков качается
        /// целиком, как раньше, а неизменный не качается вовсе.
        /// </summary>
        [Fact]
        public async Task ОбновлениеКачаетТолькоИзменённыеБлоки() {
            var oldPak = BlockData.Random(8 * Bs, 1);
            var newPak = Changed(oldPak, 5);
            var same = BlockData.Random(3 * Bs, 2);
            this.dir.WriteBytes("Paks/data.pak", oldPak);
            this.dir.WriteBytes("same.bin", same);
            this.dir.WriteBytes("cfg.ini", new byte[] { 1, 2, 3 });
            var server = new RangeServer(("Paks/data.pak", newPak), ("same.bin", same), ("cfg.ini", new byte[] { 4, 5, 6, 7 }));

            var (plan, progress) = await this.SyncAsync(server, Manifest(true, ("Paks/data.pak", newPak), ("same.bin", same), ("cfg.ini", new byte[] { 4, 5, 6, 7 })));

            this.AssertOnDisk(("Paks/data.pak", newPak), ("cfg.ini", new byte[] { 4, 5, 6, 7 }));
            Assert.Equal(new[] { ("Paks/data.pak", (long?)(5L * Bs), (long)Bs) }, server.RangedRequests);
            Assert.Equal(new[] { "cfg.ini" }, server.WholeRequests);
            Assert.Equal(7L * Bs, plan.BlockReusedBytes);

            var last = progress.Last;
            Assert.Equal(last.TotalBytes, last.BytesDownloaded);
            Assert.Equal(server.BytesServed, last.NetworkBytes);
            Assert.Equal(Bs + 4, last.NetworkBytes);
            Assert.False(SimpleSyncService.HasUpdateMarker(this.dir.Root));
        }

        [Fact]
        public async Task ВМанифестеБезБлоковФайлКачаетсяЦеликом() {
            var oldPak = BlockData.Random(4 * Bs, 3);
            var newPak = Changed(oldPak, 1);
            this.dir.WriteBytes("data.pak", oldPak);
            var server = new RangeServer(("data.pak", newPak));

            var (plan, _) = await this.SyncAsync(server, Manifest(false, ("data.pak", newPak)));

            this.AssertOnDisk(("data.pak", newPak));
            Assert.Empty(server.RangedRequests);
            Assert.Equal(new[] { "data.pak" }, server.WholeRequests);
            Assert.Equal(0, plan.BlockReusedBytes);
        }

        [Fact]
        public async Task БезСтаройКопииФайлКачаетсяЦеликом() {
            var newPak = BlockData.Random(4 * Bs, 4);
            var server = new RangeServer(("data.pak", newPak));

            await this.SyncAsync(server, Manifest(true, ("data.pak", newPak)));

            this.AssertOnDisk(("data.pak", newPak));
            Assert.Empty(server.RangedRequests);
            Assert.Equal(new[] { "data.pak" }, server.WholeRequests);
        }

        /// <summary>
        /// Раздача без Range (ответ 200 на запрос куска) — обычная загрузка целиком,
        /// без ошибки и без попыток повторять запрос куска.
        /// </summary>
        [Fact]
        public async Task СерверБезRangeОткатываетНаЗагрузкуЦеликом() {
            var oldPak = BlockData.Random(6 * Bs, 5);
            var newPak = Changed(oldPak, 2);
            this.dir.WriteBytes("data.pak", oldPak);
            var server = new RangeServer(("data.pak", newPak)) { IgnoreRange = true };

            var (plan, _) = await this.SyncAsync(server, Manifest(true, ("data.pak", newPak)));

            // Два запроса: кусок, на который пришёл весь файл, и обычная докачка —
            // сервер отдаёт целиком и её, и она начинает файл заново.
            this.AssertOnDisk(("data.pak", newPak));
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal(0, plan.BlockReusedBytes);
        }

        [Fact]
        public async Task НеТотКусокОткатываетНаЗагрузкуЦеликом() {
            var oldPak = BlockData.Random(6 * Bs, 6);
            var newPak = Changed(oldPak, 3);
            this.dir.WriteBytes("data.pak", oldPak);
            var server = new RangeServer(("data.pak", newPak)) { ShiftContentRange = 1 };

            var (plan, _) = await this.SyncAsync(server, Manifest(true, ("data.pak", newPak)));

            this.AssertOnDisk(("data.pak", newPak));
            Assert.Equal(0, plan.BlockReusedBytes);
        }

        /// <summary>
        /// Раздача портит байты в ответах на куски. Сборка по блокам сдаётся, обычная
        /// загрузка перекачивает файл — и на диск попадает только сверенное.
        /// </summary>
        [Fact]
        public async Task ИспорченныеКускиНеПопадаютНаДиск() {
            var oldPak = BlockData.Random(6 * Bs, 7);
            var newPak = Changed(oldPak, 4);
            this.dir.WriteBytes("data.pak", oldPak);
            var server = new RangeServer(("data.pak", newPak)) { CorruptRanges = true };

            await this.SyncAsync(server, Manifest(true, ("data.pak", newPak)));

            this.AssertOnDisk(("data.pak", newPak));
        }

        [Fact]
        public async Task ОбрывКускаЛечитсяПовторомБезПолнойЗагрузки() {
            var oldPak = BlockData.Random(6 * Bs, 8);
            var newPak = Changed(oldPak, 1);
            this.dir.WriteBytes("data.pak", oldPak);
            var server = new RangeServer(("data.pak", newPak)) { FailRequests = new HashSet<int> { 1 } };

            var (plan, _) = await this.SyncAsync(server, Manifest(true, ("data.pak", newPak)));

            this.AssertOnDisk(("data.pak", newPak));
            Assert.Empty(server.WholeRequests);
            Assert.Equal(5L * Bs, plan.BlockReusedBytes);
        }

        /// <summary>
        /// Блоки сошлись, а файл целиком — нет (манифест врёт о полном хеше). Такой
        /// файл не встаёт на место ни собранным, ни скачанным: старый остаётся цел,
        /// а обновление — незавершённым.
        /// </summary>
        [Fact]
        public async Task НесошедшийсяПолныйХешНеПодменяетФайл() {
            var oldPak = BlockData.Random(4 * Bs, 9);
            var newPak = Changed(oldPak, 0);
            this.dir.WriteBytes("data.pak", oldPak);
            var server = new RangeServer(("data.pak", newPak));
            var manifest = Manifest(true, ("data.pak", newPak));
            manifest.Files[0].Sha256 = new string('a', 64);

            await Assert.ThrowsAnyAsync<Exception>(() => this.SyncAsync(server, manifest));

            Assert.Equal(oldPak, File.ReadAllBytes(this.dir.PathTo("data.pak")));
            Assert.True(SimpleSyncService.HasUpdateMarker(this.dir.Root));
            Assert.NotEmpty(server.RangedRequests);
        }

        /// <summary>
        /// Такой же файл в соседней копии игры берётся с диска целиком, и сборка по
        /// блокам даже не начинается: копировать дешевле, чем собирать.
        /// </summary>
        [Fact]
        public async Task ДонорВажнееСборкиПоБлокам() {
            var oldPak = BlockData.Random(4 * Bs, 10);
            var newPak = Changed(oldPak, 2);
            this.dir.WriteBytes("data.pak", oldPak);
            using var donorDir = new TempDir();
            var donor = donorDir.WriteBytes("data.pak", newPak);
            var server = new RangeServer(("data.pak", newPak));
            var sync = new SimpleSyncService(new HttpClient(server));
            var manifest = Manifest(true, ("data.pak", newPak));
            manifest.GameId = this.gameId;

            var plan = await sync.PlanAsync(manifest, this.dir.Root, Base, CancellationToken.None);
            plan.Downloads[0].LocalSource = donor;
            await sync.ExecuteAsync(plan, new ProgressLog(), CancellationToken.None);

            this.AssertOnDisk(("data.pak", newPak));
            Assert.Empty(server.Requests);
        }

        /// <summary>
        /// Обновление прервали посреди сборки по блокам. Следующий запуск продолжает с
        /// проверенного начала «.part» и заново качает только недостающее.
        /// </summary>
        [Fact]
        public async Task ПрерваннаяСборкаПродолжаетсяСледующимЗапуском() {
            var oldPak = BlockData.Random(10 * Bs, 11);
            var newPak = Changed(Changed(oldPak, 2), 8);
            this.dir.WriteBytes("data.pak", oldPak);
            var manifest = Manifest(true, ("data.pak", newPak));

            using var cts = new CancellationTokenSource();
            var first = new RangeServer(("data.pak", newPak)) { OnRequest = n => { if (n == 2) { cts.Cancel(); } } };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.SyncAsync(first, manifest, cts.Token));
            var part = this.dir.PathTo("data.pak.part");
            Assert.True(File.Exists(part), "начатая сборка должна остаться на диске");
            Assert.True(new FileInfo(part).Length >= 3L * Bs, "блоки до второй правки уже собраны");

            var second = new RangeServer(("data.pak", newPak));
            await this.SyncAsync(second, manifest);

            this.AssertOnDisk(("data.pak", newPak));
            Assert.Equal(new[] { ("data.pak", (long?)(8L * Bs), (long)Bs) }, second.RangedRequests);
            Assert.False(File.Exists(part));
        }

        /// <summary>
        /// Проверка целостности нашла испорченный блок в файле нужного размера —
        /// чинится этот блок, а не весь файл.
        /// </summary>
        [Fact]
        public async Task ПорчаВФайлеЧинитсяОднимБлоком() {
            var good = BlockData.Random(8 * Bs, 12);
            this.dir.WriteBytes("data.pak", Changed(good, 6));
            var server = new RangeServer(("data.pak", good));

            var (plan, _) = await this.SyncAsync(server, Manifest(true, ("data.pak", good)));

            this.AssertOnDisk(("data.pak", good));
            Assert.Equal(1, plan.HashMismatches);
            Assert.Equal(Bs, server.BytesServed);
        }

        [Fact]
        public async Task НесколькоФайловСобираютсяПараллельно() {
            var files = new List<(string, byte[])>();
            var served = new List<(string, byte[])>();
            for (var i = 0; i < 6; i++) {
                var old = BlockData.Random(5 * Bs, 20 + i);
                var rel = $"Paks/p{i}.pak";
                this.dir.WriteBytes(rel, old);
                var fresh = Changed(old, i % 5);
                files.Add((rel, fresh));
            }

            var server = new RangeServer(files.ToArray());
            var (plan, _) = await this.SyncAsync(server, Manifest(true, files.ToArray()));

            this.AssertOnDisk(files.ToArray());
            Assert.Equal(6L * Bs, server.BytesServed);
            Assert.Equal(24L * Bs, plan.BlockReusedBytes);
        }

        /// <summary>
        /// Несогласованные хеши блоков в манифесте не мешают ни принять манифест, ни
        /// обновиться: файл просто качается целиком.
        /// </summary>
        [Fact]
        public async Task БитыеБлокиВМанифестеНеМешаютОбновлению() {
            var oldPak = BlockData.Random(4 * Bs, 13);
            var newPak = Changed(oldPak, 1);
            this.dir.WriteBytes("data.pak", oldPak);
            var server = new RangeServer(("data.pak", newPak));
            var manifest = Manifest(true, ("data.pak", newPak));
            manifest.Files[0].Blocks = manifest.Files[0].Blocks!.Substring(16);

            var (plan, _) = await this.SyncAsync(server, manifest);

            this.AssertOnDisk(("data.pak", newPak));
            Assert.Empty(server.RangedRequests);
            Assert.Null(plan.Downloads.Single().Blocks);
        }

        /// <summary>
        /// Манифест из JSON — таким, каким его пишет сервер, — разбирается в блоки.
        /// </summary>
        [Fact]
        public void ПоляБлоковЧитаютсяИзJson() {
            var json = "{\"version\":\"1\",\"gameId\":\"g\",\"blockSize\":1048576,\"files\":[" +
                       "{\"path\":\"a.pak\",\"size\":2621440,\"blake3\":\"\",\"sha256\":\"x\",\"executable\":false," +
                       "\"blocks\":\"H39kQAX/MP6y/vPk1TW5uQorQJYe+vO52f4Zuf3Q3yhS4Lq0\"}],\"emptyDirs\":[]}";

            var m = System.Text.Json.JsonSerializer.Deserialize<Manifest>(json)!;

            Assert.Equal(1048576, m.BlockSize);
            var list = BlockList.TryParse(m.BlockSize, m.Files[0].Size, m.Files[0].Blocks, out var problem);
            Assert.Null(problem);
            Assert.Equal(3, list!.Count);
        }

        private static byte[] Changed(byte[] data, int block) {
            var copy = (byte[])data.Clone();
            copy[(block * Bs) + 7] ^= 0xFF;
            return copy;
        }

        private static Manifest Manifest(bool withBlocks, params (string Rel, byte[] Data)[] files) => new Manifest {
            GameId = "unused",
            Version = "2.0",
            BlockSize = withBlocks ? Bs : 0,
            Files = files.Select(f => new ManifestFile {
                Path = f.Rel,
                Size = f.Data.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(f.Data)).ToLowerInvariant(),
                Blocks = withBlocks && f.Data.Length > Bs ? BlockData.Encode(f.Data, Bs) : null,
            }).ToList(),
        };

        private async Task<(DiffPlan Plan, ProgressLog Progress)> SyncAsync(RangeServer server, Manifest manifest, CancellationToken ct = default) {
            manifest.GameId = this.gameId;
            var sync = new SimpleSyncService(new HttpClient(server));
            var plan = await sync.PlanAsync(manifest, this.dir.Root, Base, ct);
            var progress = new ProgressLog();
            await sync.ExecuteAsync(plan, progress, ct);
            return (plan, progress);
        }

        private void AssertOnDisk(params (string Rel, byte[] Data)[] files) {
            foreach (var (rel, data) in files) {
                Assert.True(data.AsSpan().SequenceEqual(File.ReadAllBytes(this.dir.PathTo(rel))), $"{rel}: содержимое не то");
                Assert.False(File.Exists(this.dir.PathTo(rel) + ".part"), $"{rel}: .part должен уйти после установки");
            }
        }

        /// <summary>Прогресс без пересылки в другой поток: каждый отчёт виден сразу.</summary>
        internal sealed class ProgressLog : IProgress<SyncProgress> {
            private readonly List<SyncProgress> all = new();

            internal SyncProgress Last {
                get {
                    lock (this.all) {
                        return this.all[^1];
                    }
                }
            }

            public void Report(SyncProgress value) {
                lock (this.all) {
                    this.all.Add(value);
                }
            }
        }

        /// <summary>Раздача в памяти с Range и управляемыми поломками.</summary>
        private sealed class RangeServer : HttpMessageHandler {
            private readonly Dictionary<string, byte[]> files;
            private readonly List<(string Rel, RangeHeaderValue? Range)> requests = new();
            private long bytesServed;

            internal RangeServer(params (string Rel, byte[] Data)[] files) {
                this.files = files.ToDictionary(f => f.Rel, f => f.Data);
            }

            internal bool IgnoreRange { get; set; }

            internal long ShiftContentRange { get; set; }

            internal bool CorruptRanges { get; set; }

            internal HashSet<int> FailRequests { get; set; } = new();

            internal Action<int>? OnRequest { get; set; }

            internal List<(string Rel, RangeHeaderValue? Range)> Requests {
                get {
                    lock (this.requests) {
                        return this.requests.ToList();
                    }
                }
            }

            /// <summary>Gets запросы кусков: путь, начало, длина.</summary>
            internal (string, long?, long)[] RangedRequests => this.Requests
                .Where(r => r.Range != null)
                .Select(r => {
                    var item = r.Range!.Ranges.Single();
                    return (r.Rel, item.From, item.To!.Value - item.From!.Value + 1);
                })
                .ToArray();

            internal string[] WholeRequests => this.Requests.Where(r => r.Range == null).Select(r => r.Rel).ToArray();

            internal long BytesServed => Interlocked.Read(ref this.bytesServed);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
                var rel = this.files.Keys.SingleOrDefault(k => path.EndsWith("/" + k, StringComparison.Ordinal));
                int n;
                lock (this.requests) {
                    this.requests.Add((rel ?? path, request.Headers.Range));
                    n = this.requests.Count;
                }

                this.OnRequest?.Invoke(n);
                ct.ThrowIfCancellationRequested();
                if (this.FailRequests.Contains(n)) {
                    throw new HttpRequestException("соединение сброшено");
                }

                if (rel == null) {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                var data = this.files[rel];
                var range = request.Headers.Range?.Ranges.SingleOrDefault();
                if (range == null || this.IgnoreRange) {
                    Interlocked.Add(ref this.bytesServed, data.Length);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
                }

                var from = range.From!.Value;
                var to = range.To ?? (data.Length - 1);
                var body = data.AsSpan((int)from, (int)(to - from + 1)).ToArray();
                if (this.CorruptRanges) {
                    body[body.Length / 2] ^= 0xFF;
                }

                Interlocked.Add(ref this.bytesServed, body.Length);
                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from + this.ShiftContentRange, to + this.ShiftContentRange, data.Length);
                return Task.FromResult(resp);
            }
        }
    }
}
