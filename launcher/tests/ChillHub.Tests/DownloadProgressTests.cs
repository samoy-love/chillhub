// <copyright file="DownloadProgressTests.cs" company="PlaceholderCompany">
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
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Сколько скачано — по счёту лаунчера.
    /// <para>
    /// Модпак на 892 МБ показывал «2,4 ГБ из 892,3 МБ»: полосу, упёртую в 100%, при
    /// живой скорости рядом — со стороны загрузка, которая никогда не кончится.
    /// Счётчик складывал КАЖДЫЙ прочитанный из сети байт и не вычитал ни одного из
    /// выброшенных, а выбрасывают их регулярно: сервер ответил на Range целым файлом,
    /// .part не прошёл сверку хеша, соединение оборвалось на середине. Каждая
    /// перезакачка ложилась поверх первой.
    /// </para>
    /// <para>
    /// Правило, которое проверяется здесь: счётчик говорит, сколько ИЗ ПЛАНА лежит на
    /// диске. Он не обгоняет общий объём и приходит ровно к нему.
    /// </para>
    /// </summary>
    public class DownloadProgressTests {
        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА. Сервер не понимает Range и на докачку отдаёт файл целиком.
        /// Байты недокачанного куска уже были засчитаны — и второй раз считаться не
        /// должны.
        /// </summary>
        [Fact]
        public async Task ПовторнаяЗакачкаНеСчитаетсяДважды() {
            using var dir = new TempDir();
            var content = Bytes(400_000);

            // Половина файла уже лежит от прерванной попытки.
            var part = Path.Combine(dir.Root, "data.bin.part");
            await File.WriteAllBytesAsync(part, content.Take(content.Length / 2).ToArray());

            // Сервер отдаёт 200 и файл целиком, сколько бы Range его ни просили.
            var reports = await RunAsync(dir.Root, content, _ => Whole(content));

            Assert.Equal(content.Length, Last(reports).TotalBytes);
            Assert.DoesNotContain(reports, r => r.BytesDownloaded > r.TotalBytes);
            Assert.Equal(content.Length, Last(reports).BytesDownloaded);
        }

        /// <summary>
        /// Файл не прошёл сверку хеша и качается заново: выброшенное не остаётся в
        /// счёте. Ровно это и накручивало гигабайты на модпаке из сотен мелких файлов.
        /// </summary>
        [Fact]
        public async Task НеПрошедшийСверкуФайлНеУдваиваетСчёт() {
            using var dir = new TempDir();
            var content = Bytes(300_000);
            var attempt = 0;

            // Первая попытка отдаёт мусор той же длины — сверка её отвергнет,
            // вторая отдаёт настоящее содержимое.
            var reports = await RunAsync(dir.Root, content, _ => {
                var body = Interlocked.Increment(ref attempt) == 1 ? Bytes(content.Length, seed: 7) : content;
                return Whole(body);
            });

            Assert.True(attempt >= 2, "тест должен был застать повторную попытку");
            Assert.DoesNotContain(reports, r => r.BytesDownloaded > r.TotalBytes);
            Assert.Equal(content.Length, Last(reports).BytesDownloaded);
        }

        /// <summary>
        /// Уцелевший от прошлого запуска кусок — сделанная работа, а не ноль: докачка
        /// показывала меньше, чем на диске уже лежит, и полоса прыгала назад.
        /// </summary>
        [Fact]
        public async Task ДокачкаЗачитываетУжеЛежащееНаДиске() {
            using var dir = new TempDir();
            var content = Bytes(2_000_000);
            var half = content.Length / 2;

            var part = Path.Combine(dir.Root, "data.bin.part");
            await File.WriteAllBytesAsync(part, content.Take(half).ToArray());

            // Честный сервер: понимает Range и отдаёт только хвост. Отдаёт медленно —
            // иначе всё уложится в один отчёт (они идут не чаще десяти раз в секунду),
            // и промежуточные цифры, ради которых тест и написан, никто не увидит.
            var reports = await RunAsync(dir.Root, content, req => {
                var from = (int)(req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0);
                var body = content.Skip(from).ToArray();
                return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) {
                    Content = new StreamContent(new SlowStream(body)),
                };
            });

            // Отчётов должно быть больше одного — иначе проверять нечего.
            Assert.True(reports.Count > 2, $"ожидались промежуточные отчёты, пришло {reports.Count}");

            // Первый отчёт — «начинаем качать», он всегда нулевой. А вот дальше счётчик
            // обязан знать про лежащую на диске половину, а не отсчитывать с нуля:
            // иначе докачка показывает меньше, чем уже есть, и полоса прыгает назад.
            Assert.All(
                reports.Skip(1),
                r => Assert.True(r.BytesDownloaded >= half, $"отчёт откатился к {r.BytesDownloaded}"));
            Assert.Equal(content.Length, Last(reports).BytesDownloaded);
        }

        /// <summary>Обычная загрузка приходит ровно к обещанному объёму, без перелёта.</summary>
        [Fact]
        public async Task ОбычнаяЗагрузкаПриходитРовноКОбъёмуПлана() {
            using var dir = new TempDir();
            var content = Bytes(250_000);

            var reports = await RunAsync(dir.Root, content, _ => Whole(content));

            Assert.DoesNotContain(reports, r => r.BytesDownloaded > r.TotalBytes);
            Assert.Equal(content.Length, Last(reports).BytesDownloaded);
            Assert.Equal(content.Length, Last(reports).TotalBytes);
        }

        /// <summary>Скачивает один файл, собирая все отчёты о прогрессе.</summary>
        private static async Task<List<SyncProgress>> RunAsync(
            string root, byte[] content, Func<HttpRequestMessage, HttpResponseMessage> respond) {
            var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var sync = new SimpleSyncService(new HttpClient(new ScriptedHandler(respond)));
            var manifest = new Manifest {
                GameId = "progress-test",
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
                await sync.ExecuteAsync(plan, sink, CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }

            // Только отчёты фазы скачивания: у остальных свой смысл цифр.
            var downloading = reports.Where(r => r.Stage == "Downloading").ToList();
            Assert.NotEmpty(downloading);
            return downloading;
        }

        private static SyncProgress Last(List<SyncProgress> reports) => reports[^1];

        private static HttpResponseMessage Whole(byte[] body)
            => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        /// <summary>Предсказуемые байты: содержимое проверяется хешем, поэтому важно, чтобы оно повторялось.</summary>
        private static byte[] Bytes(int length, int seed = 1) {
            var data = new byte[length];
            for (var i = 0; i < length; i++) {
                data[i] = (byte)((i * 31 + seed) % 251);
            }

            return data;
        }

        /// <summary>Собирает отчёты как есть: <see cref="Progress{T}"/> здесь не годится — он асинхронный.</summary>
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
        /// Медленный ответ: отдаёт по 64 КиБ с паузой, чтобы отчёты о прогрессе успели
        /// выйти не одним последним. Троттлинг отчётов — сто миллисекунд.
        /// </summary>
        private sealed class SlowStream : Stream {
            private const int Chunk = 64 * 1024;
            private readonly byte[] data;
            private int position;

            internal SlowStream(byte[] data) => this.data = data;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => this.data.Length;

            public override long Position {
                get => this.position;
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default) {
                if (this.position >= this.data.Length) {
                    return 0;
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                var take = Math.Min(Math.Min(Chunk, buffer.Length), this.data.Length - this.position);
                this.data.AsMemory(this.position, take).CopyTo(buffer);
                this.position += take;
                return take;
            }

            public override int Read(byte[] buffer, int offset, int count)
                => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush() {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
