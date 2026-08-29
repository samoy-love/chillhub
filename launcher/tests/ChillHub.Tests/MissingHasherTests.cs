// <copyright file="MissingHasherTests.cs" company="PlaceholderCompany">
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
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Что происходит, когда рядом с лаунчером не хватает его собственных файлов.
    /// <para>
    /// Из обращения в админке: у игрока пропала сборка Blake3 — недоведённое
    /// самообновление, антивирус, ручная чистка папки. Сверка КАЖДОГО скачанного
    /// файла падала с FileNotFoundException, а загрузчик принимал это за сбой сети и
    /// качал файл заново: три попытки на каждый из без малого тысячи файлов модпака.
    /// Три с половиной минуты, 2,4 ГБ трафика — и отказ «Ошибка загрузки
    /// .doorstop_version: », с двоеточием и пустотой за ним.
    /// </para>
    /// <para>
    /// Три правила: сверка переживает пропажу Blake3 на SHA-256; то, что повтором не
    /// чинится, не повторяется; отказ называет причину словами.
    /// </para>
    /// </summary>
    public class MissingHasherTests : IDisposable {
        public void Dispose() => FileHasher.Blake3AvailableForTests = null;

        /// <summary>
        /// БЕЗ BLAKE3 УСТАНОВКА ПРОДОЛЖАЕТСЯ. SHA-256 лежит в самой платформе и
        /// проверяет то же самое содержимое; Blake3 — ускорение, и его пропажа обязана
        /// стоить скорости, а не установки.
        /// </summary>
        [Fact]
        public async Task БезBlake3ЗагрузкаИдётПоSha256() {
            using var dir = new TempDir();
            var content = new byte[] { 8, 9, 10, 11 };
            FileHasher.Blake3AvailableForTests = false;

            var requests = 0;
            await DownloadAsync(dir.Root, content, HashKind.Both, () => Interlocked.Increment(ref requests));

            Assert.Equal(1, requests);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir.Root, "data.bin")));
        }

        /// <summary>Без Blake3 файл на диске признаётся своим и не качается заново.</summary>
        [Fact]
        public void БезBlake3ФайлНаДискеОстаётсяСвоим() {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Root, "data.bin");
            var content = new byte[] { 4, 5, 6, 7 };
            File.WriteAllBytes(file, content);
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            FileHasher.ComputeHashes(file, out _, out var realB3);
            FileHasher.Blake3AvailableForTests = false;

            Assert.True(
                FileHasher.Matches(file, content.Length, sha, realB3, out var reason),
                $"файл должен совпасть по SHA-256, а не «{reason}»");
        }

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА. Проверять нечем совсем — загрузка прекращается на первом
        /// же файле, не тратя ни попыток, ни трафика.
        /// </summary>
        [Fact]
        public async Task КогдаПроверитьНечемЗагрузкаОстанавливаетсяСразу() {
            using var dir = new TempDir();
            var content = new byte[] { 12, 13, 14, 15 };
            FileHasher.Blake3AvailableForTests = false;

            var requests = 0;
            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => DownloadAsync(dir.Root, content, HashKind.Blake3Only, () => Interlocked.Increment(ref requests)));

            // Один файл — один запрос. Три означали бы, что отказ снова приняли за сбой сети.
            Assert.Equal(1, requests);
            Assert.Contains("переустановите", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// А несовпадение хеша — по-прежнему повод перекачать: битый .part лечится
        /// именно этим, и терять повторы здесь нельзя.
        /// </summary>
        [Fact]
        public async Task НесовпадениеХешаПоПрежнемуДаётПовторы() {
            using var dir = new TempDir();
            var content = new byte[] { 16, 17, 18, 19 };

            var requests = 0;
            await Assert.ThrowsAnyAsync<Exception>(
                () => DownloadAsync(dir.Root, content, HashKind.WrongSha, () => Interlocked.Increment(ref requests)));

            Assert.Equal(3, requests);
        }

        /// <summary>
        /// Тот самый отказ из обращения: пропала сборка. Загрузчик обязан узнать в нём
        /// нечинимое — иначе он снова пойдёт качать по три раза каждый файл.
        /// </summary>
        [Fact]
        public void ПропавшаяСборкаУзнаётсяКакНечинимое() {
            // Ровно то, что стояло в журнале: FileNotFoundException, а в FileName —
            // имя СБОРКИ, не путь к файлу.
            var assemblyGone = new FileNotFoundException(
                string.Empty, "Blake3, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null");

            Assert.True(SimpleSyncService.IsUnrecoverable(assemblyGone));
            Assert.True(SimpleSyncService.IsUnrecoverable(new DllNotFoundException("blake3_dotnet")));
            Assert.True(SimpleSyncService.IsUnrecoverable(new BadImageFormatException("не та разрядность")));
        }

        /// <summary>
        /// Пропавший файл игры — совсем другое дело: он и лечится повтором. Отличаем
        /// по FileName: у сборки там имя, у файла — путь.
        /// </summary>
        [Fact]
        public void ОбычныйСбойСетиОстаётсяЧинимым() {
            Assert.False(SimpleSyncService.IsUnrecoverable(new HttpRequestException("сеть недоступна")));
            Assert.False(SimpleSyncService.IsUnrecoverable(new IOException("соединение разорвано")));
            Assert.False(SimpleSyncService.IsUnrecoverable(
                new FileNotFoundException("нет файла", @"C:\Games\repo\data.bin")));
        }

        /// <summary>
        /// Отказ не остаётся без текста. У FileNotFoundException по сборке Message
        /// пуст, и в обращение уезжала строка «Ошибка загрузки .doorstop_version: » —
        /// с двоеточием и пустотой за ним.
        /// </summary>
        [Fact]
        public void ОтказНеОстаётсяБезТекста() {
            var assemblyGone = new FileNotFoundException(
                string.Empty, "Blake3, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null");

            var text = SimpleSyncService.Describe(assemblyGone);

            Assert.NotEmpty(text);
            Assert.Contains("Blake3", text, StringComparison.Ordinal);
            Assert.Equal("сеть недоступна", SimpleSyncService.Describe(new HttpRequestException("сеть недоступна")));
        }

        /// <summary>
        /// САМООБНОВЛЕНИЕ ЛЕЧИТ ТУ САМУЮ ПОЛОМКУ, ИЗ-ЗА КОТОРОЙ ПАДАЛО.
        /// <para>
        /// Пропавшая сборка ломала и обновление лаунчера: 274 файла из 274
        /// скачивались, а потом всё упиралось в «Ошибка загрузки Accessibility.dll: »
        /// — первый по алфавиту файл, на котором споткнулась сверка. Обновление —
        /// ровно то, что кладёт пропавшую сборку обратно, и оно обязано доезжать.
        /// </para>
        /// <para>
        /// Диффу тоже нечем считать Blake3: файл, уже лежащий на диске, обязан
        /// признаваться своим по SHA-256 — иначе обновление каждый раз тянет весь
        /// пакет целиком.
        /// </para>
        /// </summary>
        [Fact]
        public void СамообновлениеДоезжаетБезBlake3() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("same.dll", "не менялось");
            var manifest = SelfUpdateManifest.Of(
                SelfUpdateManifest.Matching(stand.Install.Root, "same.dll"),
                SelfUpdateManifest.Different("Accessibility.dll", size: 20776));

            // Хеши посчитаны, пока Blake3 был жив; дальше его как будто нет.
            FileHasher.Blake3AvailableForTests = false;

            var plan = SelfUpdateDownloadTests.NewDownloader(stand, new FakeSync(), out _)
                .BuildSelfUpdatePlan(manifest, string.Empty, stand.Temp.Root, "https://example.test/content");

            // Совпавший файл остался совпавшим: качать заново нечего.
            Assert.Equal(new[] { "Accessibility.dll" }, plan.Downloads.Select(d => d.RelativePath).ToArray());
        }

        /// <summary>Какие хеши манифест обещает по файлу.</summary>
        private enum HashKind {
            /// <summary>И SHA-256, и Blake3 — как в настоящих манифестах.</summary>
            Both,

            /// <summary>Только Blake3: без него проверять файл не по чему.</summary>
            Blake3Only,

            /// <summary>Заведомо неверный SHA-256 — обычное несовпадение.</summary>
            WrongSha,
        }

        /// <summary>Качает один файл, считая обращения к сети.</summary>
        private static async Task DownloadAsync(string root, byte[] content, HashKind kind, Action onRequest) {
            var sync = new SimpleSyncService(new HttpClient(new CountingHandler(content, onRequest)));
            var file = new ManifestFile { Path = "data.bin", Size = content.Length };

            switch (kind) {
                case HashKind.Blake3Only:
                    file.Blake3 = new string('b', 64);
                    break;
                case HashKind.WrongSha:
                    file.Sha256 = new string('a', 64);
                    break;
                default:
                    file.Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                    file.Blake3 = new string('b', 64);
                    break;
            }

            var manifest = new Manifest {
                GameId = "missing-hasher-test",
                Version = "1.0.0",
                Files = new List<ManifestFile> { file },
            };

            try {
                var plan = await sync.PlanAsync(manifest, root, "https://example.invalid/content", CancellationToken.None);
                Assert.Single(plan.Downloads);
                await sync.ExecuteAsync(plan, new Progress<SyncProgress>(), CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        /// <summary>Сеть, которая считает обращения: по ним и видно лишние повторы.</summary>
        private sealed class CountingHandler : HttpMessageHandler {
            private readonly byte[] payload;
            private readonly Action onRequest;

            internal CountingHandler(byte[] payload, Action onRequest) {
                this.payload = payload;
                this.onRequest = onRequest;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                this.onRequest();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new ByteArrayContent(this.payload),
                });
            }
        }
    }
}
