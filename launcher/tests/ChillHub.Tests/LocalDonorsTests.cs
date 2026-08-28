// <copyright file="LocalDonorsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Файл, который уже лежит в другой копии этой же игры, берётся с диска, а не из сети.
    /// <para>
    /// Модпак принадлежит папке: играть и в копию из Steam, и в сборку с сервера значит
    /// поставить его дважды. Побайтово это одни и те же файлы, и полтора гигабайта
    /// качались по второму разу молча — в логе это видно только двумя одинаковыми
    /// строчками плана, а на счётчике трафика — уже деньгами.
    /// </para>
    /// </summary>
    public class LocalDonorsTests {
        private const string Rel = "BepInEx/plugins/Author-Mod/mod.dll";

        /// <summary>Совпали путь, размер и хеши — берём с диска.</summary>
        [Fact]
        public void ГотовыйФайлНаходитсяУСоседа() {
            using var donor = new TempDir();
            var content = Encoding.UTF8.GetBytes("содержимое мода");
            donor.WriteBytes(Rel, content);
            WriteModPackManifest(donor.Root, content);

            var donors = LocalDonors.FromModPacks(new[] { donor.Root });
            var found = LocalDonors.Find(donors, Need(content));

            Assert.Equal(donor.PathTo(Rel), found);
        }

        /// <summary>
        /// РАСХОЖДЕНИЕ ХЕША — НЕ ПОВОД БРАТЬ ФАЙЛ. Под тем же именем у соседа спокойно
        /// лежит другая версия мода: возьми её — и игрок получит не тот модпак, который
        /// установил, причём молча.
        /// </summary>
        [Fact]
        public void ЧужаяВерсияПоТомуЖеПутиНеПодходит() {
            using var donor = new TempDir();
            var theirs = Encoding.UTF8.GetBytes("другая версия мода");
            donor.WriteBytes(Rel, theirs);
            WriteModPackManifest(donor.Root, theirs);

            var donors = LocalDonors.FromModPacks(new[] { donor.Root });

            Assert.Null(LocalDonors.Find(donors, Need(Encoding.UTF8.GetBytes("нужная версия мода"))));
        }

        /// <summary>
        /// Манифест обещает файл, которого на диске уже нет: донор молча пропускается,
        /// файл поедет из сети.
        /// </summary>
        [Fact]
        public void ОбещанныйНоИсчезнувшийФайлНеПредлагается() {
            using var donor = new TempDir();
            var content = Encoding.UTF8.GetBytes("содержимое мода");
            WriteModPackManifest(donor.Root, content);

            Assert.Null(LocalDonors.Find(LocalDonors.FromModPacks(new[] { donor.Root }), Need(content)));
        }

        /// <summary>Папка, в которую ставим, сама себе донором не бывает.</summary>
        [Fact]
        public void ЦелеваяПапкаИзДоноровИсключается() {
            using var donor = new TempDir();
            var content = Encoding.UTF8.GetBytes("содержимое мода");
            donor.WriteBytes(Rel, content);
            WriteModPackManifest(donor.Root, content);

            Assert.Empty(LocalDonors.FromModPacks(new[] { donor.Root }, exclude: donor.Root));
        }

        /// <summary>Папка без установленного модпака предлагать нечего.</summary>
        [Fact]
        public void ПапкаБезМодпакаДоноромНеСтановится() {
            using var donor = new TempDir();
            donor.WriteBytes(Rel, Encoding.UTF8.GetBytes("файл сам по себе"));

            Assert.Empty(LocalDonors.FromModPacks(new[] { donor.Root }));
            Assert.Null(LocalDonors.Find(null, Need(Encoding.UTF8.GetBytes("что угодно"))));
        }

        /// <summary>
        /// Сквозная проверка: сервер недоступен, а установка проходит — файл целиком
        /// пришёл из соседней папки.
        /// </summary>
        [Fact]
        public async Task ФайлСоседаСтавитсяБезЕдиногоЗапросаВСеть() {
            using var donor = new TempDir();
            using var target = new TempDir();
            using var scope = new HashCacheScope("donor");

            var content = Encoding.UTF8.GetBytes("содержимое мода, которое незачем качать дважды");
            donor.WriteBytes(Rel, content);
            WriteModPackManifest(donor.Root, content);

            var manifest = new Manifest {
                GameId = scope.GameId,
                Version = "2.2.12",
                Files = new List<ManifestFile> { ManifestEntry(content) },
            };

            // Сеть отвечает отказом на что угодно: дойди дело до загрузки — тест упадёт.
            var sync = new SimpleSyncService(new HttpClient(new DeadHandler()));
            var options = PlanOptions.ForModPack(target.Root, new[] { donor.Root });

            var plan = await sync.PlanAsync(manifest, target.Root, "https://example.invalid/content", options, CancellationToken.None);

            Assert.Equal(1, plan.ReusedFiles);
            Assert.Equal(content.Length, plan.ReusedBytes);

            await sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None);

            Assert.Equal(content, await File.ReadAllBytesAsync(target.PathTo(Rel)));
        }

        /// <summary>
        /// Донор с испорченным файлом не ломает установку: сверка его отвергает, и
        /// файл едет из сети, как раньше.
        /// </summary>
        [Fact]
        public async Task ИспорченныйФайлСоседаОтправляетЗаЗагрузкой() {
            using var donor = new TempDir();
            using var target = new TempDir();
            using var scope = new HashCacheScope("donor");

            var content = Encoding.UTF8.GetBytes("правильное содержимое мода");

            // Манифест соседа обещает нужный хеш, а на диске — подменённые байты того же
            // размера. Так выглядит порча файла, которую манифест ещё не заметил.
            donor.WriteBytes(Rel, Encoding.UTF8.GetBytes(new string('X', content.Length)));
            WriteModPackManifest(donor.Root, content);

            var manifest = new Manifest {
                GameId = scope.GameId,
                Version = "2.2.12",
                Files = new List<ManifestFile> { ManifestEntry(content) },
            };

            var sync = new SimpleSyncService(new HttpClient(new StubContentHandler(content)));
            var options = PlanOptions.ForModPack(target.Root, new[] { donor.Root });

            var plan = await sync.PlanAsync(manifest, target.Root, "https://example.invalid/content", options, CancellationToken.None);
            await sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None);

            Assert.Equal(content, await File.ReadAllBytesAsync(target.PathTo(Rel)));
        }

        /// <summary>
        /// Хеши поставленного файла попадают в кеш: следующая проверка не должна
        /// перечитывать с диска то, что лаунчер только что посчитал сам.
        /// </summary>
        [Fact]
        public async Task ХешиПоставленногоФайлаПопадаютВКеш() {
            using var target = new TempDir();
            using var scope = new HashCacheScope("cache-fill");

            var content = Encoding.UTF8.GetBytes("свежескачанный файл");
            var manifest = new Manifest {
                GameId = scope.GameId,
                Version = "1.0.0",
                Files = new List<ManifestFile> { ManifestEntry(content) },
            };

            var sync = new SimpleSyncService(new HttpClient(new StubContentHandler(content)));
            var plan = await sync.PlanAsync(manifest, target.Root, "https://example.invalid/content", CancellationToken.None);
            await sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None);

            var info = new FileInfo(target.PathTo(Rel));
            Assert.True(
                FileHashCache.Load(scope.GameId, target.Root)
                    .TryGet(Rel, info.Length, info.LastWriteTimeUtc.Ticks, out var sha, out _),
                "хеши скачанного файла должны осесть в кеше папки");
            Assert.Equal(Sha256(content), sha);
        }

        /// <summary>
        /// КЕШ ПРИНАДЛЕЖИТ ПАПКЕ. Одна игра живёт в двух копиях, пути внутри совпадают —
        /// и раньше синхронизация одной папки выбрасывала из кеша записи другой.
        /// </summary>
        [Fact]
        public void ДвеПапкиОднойИгрыНеДелятКеш() {
            using var scope = new HashCacheScope("two-roots");
            var steam = @"C:\Steam\steamapps\common\Game";
            var local = @"D:\Games\ChillHub\game";

            var a = FileHashCache.Load(scope.GameId, steam);
            a.Set(Rel, 10, 20, new string('a', 64), new string('b', 64));
            a.PruneAndSave(new List<string> { Rel });

            var b = FileHashCache.Load(scope.GameId, local);
            b.Set("другой.dll", 30, 40, new string('c', 64), new string('d', 64));

            // Прополка второй папки знает только про свои файлы — и не должна выбросить
            // записи первой.
            b.PruneAndSave(new List<string> { "другой.dll" });

            Assert.True(FileHashCache.Load(scope.GameId, steam).TryGet(Rel, 10, 20, out _, out _));
            Assert.False(FileHashCache.Load(scope.GameId, steam).TryGet("другой.dll", 30, 40, out _, out _));
            Assert.NotEqual(
                FileHashCache.PathFor(scope.GameId, steam),
                FileHashCache.PathFor(scope.GameId, local));
        }

        /// <summary>Удаление игры уносит кеши всех её папок сразу.</summary>
        [Fact]
        public void УдалениеИгрыУноситКешиВсехЕёПапок() {
            using var scope = new HashCacheScope("remove-all");
            foreach (var root in new[] { @"C:\one", @"D:\two" }) {
                var cache = FileHashCache.Load(scope.GameId, root);
                cache.Set(Rel, 1, 1, new string('a', 64), new string('b', 64));
                cache.PruneAndSave(new List<string> { Rel });
            }

            FileHashCache.Remove(scope.GameId);

            Assert.False(File.Exists(FileHashCache.PathFor(scope.GameId, @"C:\one")!));
            Assert.False(File.Exists(FileHashCache.PathFor(scope.GameId, @"D:\two")!));
        }

        /// <summary>Задача загрузки под это содержимое.</summary>
        /// <param name="content">Байты файла.</param>
        /// <returns>Задача загрузки.</returns>
        private static FileTask Need(byte[] content) => new FileTask {
            RelativePath = Rel,
            Size = content.Length,
            Url = "https://example.invalid/content/" + Rel,
            Sha256 = Sha256(content),
            Blake3 = Blake3.Hasher.Hash(content).ToString().ToLowerInvariant(),
        };

        /// <summary>Запись манифеста под это содержимое.</summary>
        /// <param name="content">Байты файла.</param>
        /// <returns>Запись манифеста.</returns>
        private static ManifestFile ManifestEntry(byte[] content) => new ManifestFile {
            Path = Rel,
            Size = content.Length,
            Sha256 = Sha256(content),
            Blake3 = Blake3.Hasher.Hash(content).ToString().ToLowerInvariant(),
        };

        private static string Sha256(byte[] content)
            => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        /// <summary>
        /// Кладёт в папку копию манифеста установленного модпака — ровно то, что после
        /// установки пишет лаунчер и по чему потом ищутся готовые файлы.
        /// </summary>
        /// <param name="root">Папка-донор.</param>
        /// <param name="content">Содержимое, которое манифест обещает.</param>
        private static void WriteModPackManifest(string root, byte[] content) {
            var manifest = new Manifest {
                GameId = "donor",
                Version = "2.2.12",
                Files = new List<ManifestFile> { ManifestEntry(content) },
            };

            Assert.True(ChillHub.Core.Home.GameLocalState.WriteInstalledModPackManifest(root, manifest));
        }

        /// <summary>Сервер, отдающий одно и то же тело на любой запрос.</summary>
        private sealed class StubContentHandler : HttpMessageHandler {
            private readonly byte[] payload;

            internal StubContentHandler(byte[] payload) => this.payload = payload;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                    Content = new ByteArrayContent(this.payload),
                });
        }

        /// <summary>Сеть, которой нет: любой запрос — провал теста по существу.</summary>
        private sealed class DeadHandler : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new HttpRequestException($"тест не должен ходить в сеть: {request.RequestUri}");
        }
    }
}
