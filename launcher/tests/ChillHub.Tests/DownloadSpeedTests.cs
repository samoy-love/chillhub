// <copyright file="DownloadSpeedTests.cs" company="PlaceholderCompany">
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

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Скорость скачивания — по тому, что пришло по проводу.
    /// <para>
    /// НА КАНАЛЕ, ГДЕ БОЛЬШЕ ШЕСТИДЕСЯТИ МЕГАБАЙТ В СЕКУНДУ НЕ БЫВАЕТ, ЛАУНЧЕР
    /// ПОКАЗЫВАЛ СТО С ЛИШНИМ. Скорость считалась по счётчику сделанного, а в него идут
    /// и файлы, взятые из соседней копии игры на диске, и уцелевший от прошлого запуска
    /// кусок .part. Копирование с диска быстрее сети в разы — вот цифра и улетала.
    /// </para>
    /// <para>
    /// Два числа разведены: <see cref="SyncProgress.BytesDownloaded"/> отвечает на
    /// «сколько из плана сделано» и рисует полосу, <see cref="SyncProgress.NetworkBytes"/>
    /// — на «сколько прошло по проводу», и по нему считается скорость.
    /// </para>
    /// </summary>
    public class DownloadSpeedTests {
        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА. Файл взят из соседней копии на диске: план продвинулся, а
        /// по сети не пришло ничего — и скорости взяться неоткуда.
        /// </summary>
        [Fact]
        public async Task ВзятоеСДискаСкоростьНеНакручивает() {
            using var dir = new TempDir();
            var content = Bytes(300_000);

            // Донор рядом: тот же файл уже лежит в другой копии игры.
            var donor = Path.Combine(dir.Root, "donor.bin");
            await File.WriteAllBytesAsync(donor, content);

            var reports = await RunAsync(dir.Root, content, donor);

            // План выполнен целиком...
            Assert.Equal(content.Length, Last(reports).BytesDownloaded);

            // ...а по сети не пришло ни байта.
            Assert.All(reports, r => Assert.Equal(0, r.NetworkBytes));
        }

        /// <summary>Обычная закачка: по проводу пришло ровно то, что легло на диск.</summary>
        [Fact]
        public async Task ОбычнаяЗакачкаСчитаетСетьНаравнеСПланом() {
            using var dir = new TempDir();
            var content = Bytes(250_000);

            var reports = await RunAsync(dir.Root, content, donorPath: null);

            Assert.Equal(content.Length, Last(reports).BytesDownloaded);
            Assert.Equal(content.Length, Last(reports).NetworkBytes);
        }

        /// <summary>
        /// Перезакачанные байты из сетевого счёта не вычитаются: по проводу они прошли,
        /// и для скорости это правда. Разница между числами — ровно та работа, которую
        /// пришлось делать дважды.
        /// </summary>
        [Fact]
        public async Task ПерезакачкаОстаётсяВСчётеСети() {
            using var dir = new TempDir();
            var content = Bytes(200_000);
            var attempt = 0;

            var reports = await RunAsync(dir.Root, content, donorPath: null, respond: _ => {
                var body = Interlocked.Increment(ref attempt) == 1 ? Bytes(content.Length, seed: 7) : content;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            });

            Assert.True(attempt >= 2, "тест должен был застать повторную попытку");

            // Сделано — ровно план, а по проводу прошло вдвое больше.
            Assert.Equal(content.Length, Last(reports).BytesDownloaded);
            Assert.True(
                Last(reports).NetworkBytes >= content.Length * 2,
                $"по сети прошло {Last(reports).NetworkBytes}, а качали дважды по {content.Length}");
        }

        /// <summary>
        /// Та же правда в очереди загрузок: строка показывает скорость по сети. Скачано
        /// с диска — в строке ноль, а не мгновенная скорость копирования.
        /// </summary>
        [Fact]
        public async Task СкоростьВОчередиНеРастётОтВзятогоСДиска() {
            var speeds = await SpeedsFromQueueAsync(networkGrows: false);

            Assert.All(speeds, s => Assert.Equal(0, s));
        }

        /// <summary>А на настоящей сети очередь считает скорость как считала.</summary>
        [Fact]
        public async Task СкоростьВОчередиСчитаетНастоящуюСеть() {
            var speeds = await SpeedsFromQueueAsync(networkGrows: true);

            Assert.Contains(speeds, s => s > 0);
        }

        /// <summary>
        /// Гоняет позицию очереди через два отчёта и собирает показанную скорость.
        /// <para>
        /// Между отчётами секунда по часам, которые двигает сам тест: счётчик отбрасывает
        /// интервалы короче полусекунды, и ждать их настоящими часами — значит гадать,
        /// успеет ли загруженная машина отмерить паузу длиннее порога.
        /// </para>
        /// <para>
        /// Второй отчёт уходит только после того, как очередь показала первый: отчёты едут
        /// через <see cref="Progress{T}"/>, то есть асинхронно, и без этой отсечки оба
        /// доезжают до счётчика разом — с нулевым интервалом между ними, а то и задом наперёд.
        /// Ждём тоже второй отчёт, а не завершение позиции: скорость приходит именно в нём,
        /// и он вполне может опоздать за <c>ItemCompleted</c>.
        /// </para>
        /// </summary>
        /// <param name="networkGrows">Растёт ли счётчик пришедшего по сети.</param>
        /// <returns>Скорости из отчётов очереди.</returns>
        private static async Task<List<double>> SpeedsFromQueueAsync(bool networkGrows) {
            var game = new GameInfo {
                GameId = "a",
                Title = "a",
                LatestVersion = "1.0.0",
                ExeRelativePath = "game.exe",
            };

            var clock = new TestClock();
            var firstShown = Signal();
            var secondShown = Signal();

            using var queue = new DownloadQueue(
                gid => gid == "a" ? game : null,
                () => "https://example.test",
                () => new TwoReportSync(networkGrows, clock, firstShown.Task),
                clock: clock.Now);

            var speeds = new List<double>();
            var done = Signal();
            queue.ItemProgress += item => {
                lock (speeds) {
                    speeds.Add(item.BytesPerSecond);
                }

                if (item.BytesDownloaded >= TwoReportSync.StepBytes * 2) {
                    secondShown.TrySetResult();
                }
                else if (item.BytesDownloaded >= TwoReportSync.StepBytes) {
                    firstShown.TrySetResult();
                }
            };
            queue.ItemCompleted += _ => done.TrySetResult();
            queue.ItemRemoved += _ => done.TrySetResult();

            Assert.True(queue.Enqueue("a"));
            await Task.WhenAll(done.Task, secondShown.Task).WaitAsync(TimeSpan.FromSeconds(20));

            lock (speeds) {
                Assert.NotEmpty(speeds);
                return speeds.ToList();
            }
        }

        /// <summary>Одноразовый сигнал между потоками. Продолжения — не на том, кто его подал.</summary>
        /// <returns>Незавершённый источник задачи.</returns>
        private static TaskCompletionSource Signal()
            => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private static SyncProgress Last(List<SyncProgress> reports) => reports[^1];

        /// <summary>Качает один файл, собирая отчёты фазы скачивания.</summary>
        private static async Task<List<SyncProgress>> RunAsync(
            string root,
            byte[] content,
            string? donorPath,
            Func<HttpRequestMessage, HttpResponseMessage>? respond = null) {
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            respond ??= _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            var sync = new SimpleSyncService(new HttpClient(new ScriptedHandler(respond)));

            var manifest = new Manifest {
                GameId = "speed-test",
                Version = "1.0.0",
                Files = new List<ManifestFile> {
                    new ManifestFile { Path = "data.bin", Size = content.Length, Sha256 = sha },
                },
            };

            var reports = new List<SyncProgress>();
            var sink = new CollectingProgress(reports);

            try {
                var plan = await sync.PlanAsync(manifest, root, "https://example.invalid/content", CancellationToken.None);
                Assert.Single(plan.Downloads);

                // Донор подставляется в задание так же, как его находит LocalDonors.
                if (donorPath != null) {
                    plan.Downloads[0].LocalSource = donorPath;
                }

                await sync.ExecuteAsync(plan, sink, CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }

            var downloading = reports.Where(r => r.Stage == "Downloading").ToList();
            Assert.NotEmpty(downloading);
            return downloading;
        }

        /// <summary>Предсказуемые байты: содержимое сверяется хешем.</summary>
        private static byte[] Bytes(int length, int seed = 1) {
            var data = new byte[length];
            for (var i = 0; i < length; i++) {
                data[i] = (byte)((i * 31 + seed) % 251);
            }

            return data;
        }

        /// <summary>Собирает отчёты как есть: Progress&lt;T&gt; здесь не годится — он асинхронный.</summary>
        private sealed class CollectingProgress : IProgress<SyncProgress> {
            private readonly List<SyncProgress> sink;

            internal CollectingProgress(List<SyncProgress> sink) => this.sink = sink;

            public void Report(SyncProgress value) {
                lock (this.sink) {
                    this.sink.Add(value);
                }
            }
        }

        /// <summary>
        /// Синхронизация с двумя отчётами: план продвигается всегда, а сетевой счётчик —
        /// только когда тест этого просит.
        /// </summary>
        private sealed class TwoReportSync : ISyncService {
            /// <summary>Сколько «скачивается» за один отчёт.</summary>
            internal const long StepBytes = 5_000_000;

            private readonly bool networkGrows;
            private readonly TestClock clock;
            private readonly Task firstShown;

            internal TwoReportSync(bool networkGrows, TestClock clock, Task firstShown) {
                this.networkGrows = networkGrows;
                this.clock = clock;
                this.firstShown = firstShown;
            }

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct)
                => Task.FromResult(new Manifest());

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => Task.FromResult(new DiffPlan());

            public Task<DiffPlan> PlanAsync(
                Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => Task.FromResult(new DiffPlan());

            public async Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
                progress.Report(this.Step(1));

                // Первый отчёт только засевает базу — второй шлём, когда он дошёл до счётчика.
                await this.firstShown.WaitAsync(ct).ConfigureAwait(false);

                // Счётчик скорости отбрасывает интервалы короче полусекунды как шум.
                this.clock.Advance(1000);
                progress.Report(this.Step(2));
            }

            private SyncProgress Step(int n) => new SyncProgress {
                Stage = "Downloading",
                BytesDownloaded = StepBytes * n,
                NetworkBytes = this.networkGrows ? StepBytes * n : 0,
                TotalBytes = StepBytes * 2,
            };
        }

        /// <summary>
        /// Часы, которые двигает тест. Скорость считается по интервалу между отчётами, и
        /// интервал здесь задаётся, а не выжидается: настоящая пауза на загруженной машине
        /// может оказаться и короче, и длиннее заказанной.
        /// </summary>
        private sealed class TestClock {
            // Не с нуля: нулевое показание счётчик скорости считает «ещё не измеряли»
            // и на таком отчёте только засевает базу.
            private long ms = 1_000_000;

            /// <summary>Текущее показание в миллисекундах — таким его видит счётчик скорости.</summary>
            /// <returns>Миллисекунды.</returns>
            internal long Now() => Interlocked.Read(ref this.ms);

            /// <summary>Переводит часы вперёд.</summary>
            /// <param name="delta">На сколько миллисекунд.</param>
            internal void Advance(long delta) => Interlocked.Add(ref this.ms, delta);
        }

        /// <summary>Сеть, которая отвечает так, как велит тест.</summary>
        private sealed class ScriptedHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

            internal ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(this.respond(request));
        }
    }
}
