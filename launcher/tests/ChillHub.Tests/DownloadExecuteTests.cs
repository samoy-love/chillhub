// <copyright file="DownloadExecuteTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Сквозная проверка фазы скачивания: план → загрузка → сверка хеша → активация.
    /// <para>
    /// Этот путь не был покрыт тестами вовсе, и это дорого обошлось. Проверку хеша
    /// перенесли внутрь цикла повторов, но вызвали её при ещё ОТКРЫТОМ потоке записи
    /// (<c>using var</c> живёт до конца try, а файл открыт с <see cref="FileShare.None"/>).
    /// Сверка не могла открыть файл и падала с «занят другим процессом» — процессом,
    /// которым были мы сами. Ломались все загрузки, включая самообновление лаунчера;
    /// сборка, тесты и линтеры при этом проходили чисто.
    /// </para>
    /// </summary>
    public class DownloadExecuteTests {
        /// <summary>Обычная загрузка доходит до диска и проходит сверку хеша.</summary>
        [Fact]
        public async Task СкачиваниеДоходитДоДискаИПроверяетХеш() {
            using var dir = new TempDir();
            var content = Encoding.UTF8.GetBytes("содержимое файла для проверки скачивания");
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            var landed = await DownloadOneAsync(dir.Root, "app/data.bin", content, sha);

            Assert.True(File.Exists(landed), "файл должен оказаться в папке игры");
            Assert.Equal(content, await File.ReadAllBytesAsync(landed));
        }

        /// <summary>
        /// Несовпадение хеша не должно молча пропускать файл: сверка обязана
        /// сработать, а значит — суметь ОТКРЫТЬ уже записанный файл.
        /// </summary>
        [Fact]
        public async Task НеверныйХешОтвергаетФайл() {
            using var dir = new TempDir();
            var content = Encoding.UTF8.GetBytes("содержимое");
            var wrongSha = new string('a', 64);

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => DownloadOneAsync(dir.Root, "app/data.bin", content, wrongSha));

            // Важно: причина — именно несовпадение хеша, а не «файл занят».
            Assert.DoesNotContain("another process", ex.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("используется другим", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Пустой каталог с завершающим слешем доходит до диска.
        /// <para>
        /// Валидатор такую запись пропускает (иначе игры с уже опубликованными
        /// манифестами не ставятся вовсе), но в план она клалась сырой, а применение
        /// плана гоняет путь через <c>ManifestPath.Combine</c>, который неканоническую
        /// форму отвергает. Обновление lethal-company падало на ровном месте: файлов
        /// качать нечего, а «Небезопасный путь в манифесте» — есть.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПустойКаталогСоСлешемСоздаётсяНаДиске() {
            using var dir = new TempDir();
            var rel = "BepInEx/plugins/Bertogim-LoadingScreen";
            var sync = new SimpleSyncService(new HttpClient(new StubContentHandler(Array.Empty<byte>())));
            var manifest = new Manifest {
                GameId = "emptydir-test",
                Version = "1.0.7",
                Files = new List<ManifestFile>(),
                EmptyDirs = new List<string> { rel + "/" },
            };

            try {
                var plan = await sync.PlanAsync(manifest, dir.Root, "https://example.invalid/content", CancellationToken.None);
                Assert.Empty(plan.Downloads);
                await sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }

            Assert.True(
                Directory.Exists(Path.Combine(dir.Root, rel.Replace('/', Path.DirectorySeparatorChar))),
                "каталог из манифеста должен быть создан");
        }

        /// <summary>
        /// Уже скачанное в staging переживает отмену и не качается второй раз.
        /// <para>
        /// Staging переезжает в папку игры только в фазе активации, поэтому после отмены
        /// план строится по старому содержимому и снова просит скачать всё. Докачка по
        /// Range спасала лишь файлы, бывшие в работе в момент отмены, а всё завершённое
        /// качалось заново: из 9 ГБ второй заход опять требовал 9 ГБ.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ЗавершённыйФайлИзStagingНеКачаетсяЗаново() {
            using var dir = new TempDir();
            var rel = "app/data.bin";
            var content = Encoding.UTF8.GetBytes("файл, докачанный до отмены");
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            // Ровно то, что осталось бы на диске от прерванной попытки
            var staged = Path.Combine(dir.Root, ".staging", "app", "data.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllBytesAsync(staged, content);

            // Любой сетевой запрос — провал теста: качать тут нечего
            var sync = new SimpleSyncService(new HttpClient(new FailingHandler()));
            var manifest = new Manifest {
                GameId = "staging-reuse-test",
                Version = "1.0.0",
                Files = new List<ManifestFile> {
                    new ManifestFile { Path = rel, Size = content.Length, Sha256 = sha },
                },
            };

            try {
                var plan = await sync.PlanAsync(manifest, dir.Root, "https://example.invalid/content", CancellationToken.None);
                Assert.Single(plan.Downloads);
                await sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }

            var landed = Path.Combine(dir.Root, "app", "data.bin");
            Assert.True(File.Exists(landed), "файл должен переехать из staging в папку игры");
            Assert.Equal(content, await File.ReadAllBytesAsync(landed));
        }

        /// <summary>
        /// Обрывок в staging не выдаётся за готовый файл: он не совпадает с манифестом
        /// ни размером, ни хешем, и его нужно перекачать.
        /// </summary>
        [Fact]
        public async Task ОбрывокВStagingПерекачивается() {
            using var dir = new TempDir();
            var content = Encoding.UTF8.GetBytes("полное содержимое файла");
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            var staged = Path.Combine(dir.Root, ".staging", "app", "data.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllBytesAsync(staged, Encoding.UTF8.GetBytes("полное соде"));

            var landed = await DownloadOneAsync(dir.Root, "app/data.bin", content, sha);

            Assert.Equal(content, await File.ReadAllBytesAsync(landed));
        }

        /// <summary>
        /// Файл того же размера, но от другой сборки, за готовый не сходит: staging
        /// мог остаться от прерванного обновления на другую версию, и совпадение
        /// размера там не значит ничего.
        /// </summary>
        [Fact]
        public async Task ЧужаяВерсияВStagingПерекачивается() {
            using var dir = new TempDir();
            var content = Encoding.UTF8.GetBytes("содержимое версии 2");
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            var staged = Path.Combine(dir.Root, ".staging", "app", "data.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllBytesAsync(staged, Encoding.UTF8.GetBytes("содержимое версии 1"));
            Assert.Equal(content.Length, new FileInfo(staged).Length);

            var landed = await DownloadOneAsync(dir.Root, "app/data.bin", content, sha);

            Assert.Equal(content, await File.ReadAllBytesAsync(landed));
        }

        /// <summary>
        /// Обновление заменяет уже лежащий на месте файл.
        /// <para>
        /// Основной путь активации: одно переименование с заменой вместо связки
        /// «проверить — удалить — проверить — переместить». Тестов на замену не было
        /// вовсе — покрыта была только установка на пустое место, где заменять нечего.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ОбновлениеЗаменяетСуществующийФайл() {
            using var dir = new TempDir();
            var oldContent = Encoding.UTF8.GetBytes("старая версия файла");
            var newContent = Encoding.UTF8.GetBytes("новая версия файла, заметно длиннее старой");
            dir.WriteBytes("app/data.bin", oldContent);

            var landed = await DownloadOneAsync(
                dir.Root,
                "app/data.bin",
                newContent,
                Convert.ToHexString(SHA256.HashData(newContent)).ToLowerInvariant());

            Assert.Equal(newContent, await File.ReadAllBytesAsync(landed));
        }

        /// <summary>
        /// Занятый файл не валит обновление: он уходит в отложенную замену, а игра
        /// честно остаётся помеченной как обновлённая не до конца.
        /// <para>
        /// Это запасная ветка активации — та самая, ради которой существуют
        /// <c>SafeDeleteFile</c>, <c>.new</c> и <c>MoveFileEx</c>. Проверяем именно её
        /// поведение, а не то, что успеет сделать система на перезагрузке.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ЗанятыйФайлУходитВОтложеннуюЗамену() {
            using var dir = new TempDir();
            var oldContent = Encoding.UTF8.GetBytes("старая версия");
            var newContent = Encoding.UTF8.GetBytes("новая версия!");
            var target = dir.WriteBytes("app/data.bin", oldContent);

            // Держим файл так, как его держит запущенная игра свой exe: читать можно,
            // а переименовать или удалить — нет. Именно на этом спотыкается активация.
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var landed = await DownloadOneAsync(
                    dir.Root,
                    "app/data.bin",
                    newContent,
                    Convert.ToHexString(SHA256.HashData(newContent)).ToLowerInvariant());

                // Старое содержимое на месте — заменить его сейчас физически нельзя
                Assert.Equal(oldContent, await File.ReadAllBytesAsync(landed));

                // Новое лежит рядом и ждёт перезагрузки
                Assert.True(File.Exists(landed + ".new"), "новый файл должен ждать заменой на перезагрузку");
                Assert.Equal(newContent, await File.ReadAllBytesAsync(landed + ".new"));
            }

            // Маркер обязан остаться: игра обновлена не полностью, запускать её нельзя
            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root), "маркер незавершённого обновления должен остаться");
            Assert.Contains("reboot-required", SimpleSyncService.ReadUpdateMarker(dir.Root));
        }

        /// <summary>Скачивает один файл через подставной HTTP и возвращает путь, куда он лёг.</summary>
        private static async Task<string> DownloadOneAsync(string root, string rel, byte[] content, string sha256) {
            var handler = new StubContentHandler(content);
            var sync = new SimpleSyncService(new HttpClient(handler));

            var manifest = new Manifest {
                GameId = "download-test",
                Version = "1.0.0",
                Files = new List<ManifestFile> {
                    new ManifestFile { Path = rel, Size = content.Length, Sha256 = sha256 },
                },
            };

            try {
                var plan = await sync.PlanAsync(manifest, root, "https://example.invalid/content", CancellationToken.None);
                Assert.Single(plan.Downloads);
                await sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }

            return Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Падает на любом запросе: используется там, где сеть трогать не должны.</summary>
        private sealed class FailingHandler : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                throw new InvalidOperationException($"неожиданный запрос к сети: {request.RequestUri}");
            }
        }

        /// <summary>Отдаёт заранее заданное содержимое на любой запрос.</summary>
        private sealed class StubContentHandler : HttpMessageHandler {
            private readonly byte[] payload;

            internal StubContentHandler(byte[] payload) => this.payload = payload;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                var resp = new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new ByteArrayContent(this.payload),
                };
                return Task.FromResult(resp);
            }
        }
    }
}
