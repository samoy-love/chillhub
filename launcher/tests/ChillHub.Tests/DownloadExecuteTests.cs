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
        /// Пока новый файл не сверен с манифестом, старый остаётся нетронутым.
        /// <para>
        /// Это то единственное, что осталось от staging, когда его убрали: загрузка идёт
        /// в «.part» рядом с целью, и старое содержимое подменяется одним переименованием
        /// уже после сверки хеша. Сорвись загрузка — на диске рабочий старый файл, а не
        /// обрубок под его именем.
        /// </para>
        /// </summary>
        [Fact]
        public async Task СорваннаяЗагрузкаНеПортитСтарыйФайл() {
            using var dir = new TempDir();
            var oldContent = Encoding.UTF8.GetBytes("рабочая старая версия");
            var target = dir.WriteBytes("app/data.bin", oldContent);

            // Сервер отдаёт не то, что обещает манифест: сверка хеша не пройдёт
            await Assert.ThrowsAnyAsync<Exception>(() => DownloadOneAsync(
                dir.Root,
                "app/data.bin",
                Encoding.UTF8.GetBytes("подменённое содержимое"),
                new string('a', 64)));

            Assert.Equal(oldContent, await File.ReadAllBytesAsync(target));
        }

        /// <summary>
        /// Недокачанный «.part» лежит рядом с целью и переживает план: по нему
        /// обновление возобновляется, а не начинается заново.
        /// </summary>
        [Fact]
        public async Task НедокачанныйФайлНеПопадаетВУдаление() {
            using var dir = new TempDir();
            var content = Encoding.UTF8.GetBytes("полное содержимое файла");
            dir.WriteBytes("app/data.bin.part", Encoding.UTF8.GetBytes("полное соде"));

            var sync = new SimpleSyncService(new HttpClient(new StubContentHandler(content)));
            var manifest = new Manifest {
                GameId = "part-keep-" + Guid.NewGuid().ToString("N"),
                Version = "1.0.0",
                Files = new List<ManifestFile> {
                    new ManifestFile {
                        Path = "app/data.bin",
                        Size = content.Length,
                        Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                    },
                },
            };

            try {
                var plan = await sync.PlanAsync(manifest, dir.Root, "https://example.invalid/content", CancellationToken.None);
                Assert.DoesNotContain(plan.ToDelete, p => p.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        /// <summary>
        /// Каталог «.staging», оставшийся от прежней схемы, убирается: он занимает место
        /// под копию сборки, которой больше никто не пользуется.
        /// </summary>
        [Fact]
        public async Task StagingОтПрежнейСхемыУдаляется() {
            using var dir = new TempDir();
            dir.WriteBytes(".staging/app/data.bin", Encoding.UTF8.GetBytes("копия от прежней схемы"));

            var content = Encoding.UTF8.GetBytes("новый файл");
            await DownloadOneAsync(
                dir.Root,
                "app/data.bin",
                content,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

            Assert.False(Directory.Exists(Path.Combine(dir.Root, ".staging")), "брошенный staging должен быть убран");
        }

        /// <summary>
        /// Маркер незавершённого обновления ставится ДО первой записи в папку игры и
        /// снимается только после успеха: с первым же применённым файлом сборка смешанная.
        /// </summary>
        [Fact]
        public async Task МаркерДержитсяПокаОбновлениеНеЗавершено() {
            using var dir = new TempDir();
            dir.WriteBytes("app/data.bin", Encoding.UTF8.GetBytes("старая версия"));

            // Сорванное обновление: маркер обязан остаться
            await Assert.ThrowsAnyAsync<Exception>(() => DownloadOneAsync(
                dir.Root,
                "app/data.bin",
                Encoding.UTF8.GetBytes("подменённое содержимое"),
                new string('a', 64)));
            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root), "после срыва маркер должен остаться");

            // Успешное — снимает
            var content = Encoding.UTF8.GetBytes("новая версия");
            await DownloadOneAsync(
                dir.Root,
                "app/data.bin",
                content,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
            Assert.False(SimpleSyncService.HasUpdateMarker(dir.Root), "после успеха маркера быть не должно");
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

            // Остаток от прошлой такой же попытки. Его обязаны заменить, а не оставить
            // рядом: каждая попытка копила бы по ".new", а замена на перезагрузке
            // подставила бы содержимое от позапрошлой версии.
            dir.WriteBytes("app/data.bin.new", Encoding.UTF8.GetBytes("остаток от прошлой попытки"));

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
