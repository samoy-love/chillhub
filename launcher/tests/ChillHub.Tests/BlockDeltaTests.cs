// <copyright file="BlockDeltaTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Сборка файла из старой копии и докачанных блоков — без сети.
    /// <para>
    /// Источник кусков здесь в памяти: обрыв, подменённые байты, сервер без Range и
    /// отмена посреди куска воспроизводятся одной строкой. Сквозной путь через
    /// загрузчик и HTTP проверяет <see cref="BlockSyncTests"/>.
    /// </para>
    /// </summary>
    public sealed class BlockDeltaTests : IDisposable {
        private const int Bs = (int)BlockList.MinBlockSize;

        private readonly TempDir dir = new TempDir();
        private readonly Func<int, TimeSpan> savedDelay = BlockDelta.RetryDelay;

        public BlockDeltaTests() {
            BlockDelta.RetryDelay = _ => TimeSpan.Zero;
            Directory.CreateDirectory(Path.GetDirectoryName(this.Old)!);
        }

        public void Dispose() {
            BlockDelta.RetryDelay = this.savedDelay;
            this.dir.Dispose();
        }

        // ---------- формат ----------

        /// <summary>
        /// CROSS-LANGUAGE BLOCK CONTRACT: та же строка закреплена на сервере в
        /// server/internal/adminapi/builds/blocks_test.go. Разойдись реализации —
        /// ни один блок не совпадёт, и каждое обновление молча станет полным.
        /// </summary>
        [Fact]
        public void ХешиБлоковСовпадаютССервером() {
            const string pinned = "AABwkJJw2bEff2RABf8w/gAAcN5Z5HW21TW5uQorQJYAAHj4i/JXFdn+Gbn90N8o";
            var data = BlockData.ServerPattern((2 * 1024 * 1024) + (512 * 1024));

            Assert.Equal(pinned, BlockData.Encode(data, 1024 * 1024));
            var list = BlockList.TryParse(1024 * 1024, data.Length, pinned, out var problem);
            Assert.Null(problem);
            Assert.NotNull(list);
            Assert.Equal(3, list!.Count);
            Assert.True(list.Matches(2, data.AsSpan(2 * 1024 * 1024)), "последний неполный блок сверяется по своей длине");
        }

        [Fact]
        public void НесогласованныйСписокБлоковНеИспользуется() {
            var data = BlockData.Random(3 * Bs, 1);
            var good = BlockData.Encode(data, Bs);

            Assert.Null(BlockList.TryParse(Bs, data.Length, null, out var none));
            Assert.Null(none);
            Assert.Null(BlockList.TryParse(Bs, data.Length, string.Empty, out none));
            Assert.Null(none);

            foreach (var (bs, size, blocks) in new (long, long, string)[] {
                (0, data.Length, good),
                (1024, data.Length, good),
                (1L << 30, data.Length, good),
                (Bs, data.Length, "это не base64!"),
                (Bs, data.Length + Bs, good),
                (Bs, data.Length - Bs, good),
                (Bs, Bs, BlockData.Encode(data.AsSpan(0, Bs).ToArray(), Bs)),
            }) {
                Assert.Null(BlockList.TryParse(bs, size, blocks, out var problem));
                Assert.NotNull(problem);
            }

            Assert.NotNull(BlockList.TryParse(Bs, data.Length, good, out var ok));
            Assert.Null(ok);
        }

        // ---------- откуда берутся блоки ----------

        [Fact]
        public async Task ИзменённыйБлокКачаетсяОстальныеБерутсяИзСтаройКопии() {
            var oldData = BlockData.Random(10 * Bs, 1);
            var newData = (byte[])oldData.Clone();
            newData[(3 * Bs) + 17] ^= 0xFF;

            var (result, source) = await this.AssembleAsync(oldData, newData);

            Assert.Equal(Bs, result.Fetched);
            Assert.Equal(9L * Bs, result.FromOld);
            Assert.Equal(1, result.Requests);
            Assert.Equal(new[] { (3L * Bs, (long)Bs) }, source.Requests);
        }

        /// <summary>
        /// Данные, вставленные в начало, сдвигают весь хвост: блоки ищутся по хешу,
        /// а не по месту, и сдвинутые находятся. Так устроено обновление pak-архивов.
        /// </summary>
        [Fact]
        public async Task СдвинутыеБлокиНаходятсяПоХешу() {
            var oldData = BlockData.Random(8 * Bs, 2);
            var newData = BlockData.Random(2 * Bs, 3).Concat(oldData).ToArray();

            var (result, _) = await this.AssembleAsync(oldData, newData);

            Assert.Equal(2L * Bs, result.Fetched);
            Assert.Equal(8L * Bs, result.FromOld);
        }

        /// <summary>
        /// Три байта в начале сдвигают весь файл мимо границ блоков. Окно, катящееся
        /// с шагом в байт, находит сдвинутые блоки: качается только первый, в который
        /// попала вставка. Так растут файлы Unity-игр от версии к версии.
        /// </summary>
        [Fact]
        public async Task СдвигНаНесколькоБайтНаходитсяКатящимсяОкном() {
            var oldData = BlockData.Random(4 * Bs, 4);
            var newData = new byte[] { 1, 2, 3 }.Concat(oldData).ToArray();

            var (result, _) = await this.AssembleAsync(oldData, newData);

            Assert.Equal(Bs, result.Fetched);
            Assert.Equal(newData.Length - Bs, result.FromOld);
        }

        /// <summary>
        /// Вставка и удаление посреди файла: пропадают только блоки, в которые
        /// попала правка, всё до и после находится на своих новых местах.
        /// </summary>
        /// <param name="delta">Сколько байт вставлено (плюс) или удалено (минус).</param>
        [Theory]
        [InlineData(137)]
        [InlineData(-4099)]
        [InlineData(1)]
        public async Task ПравкаПосрединеСтоитТолькоЗадетыхБлоков(int delta) {
            var oldData = BlockData.Random(12 * Bs, 43);
            var cut = (5 * Bs) + 777;
            var newData = delta > 0
                ? oldData.Take(cut).Concat(BlockData.Random(delta, 44)).Concat(oldData.Skip(cut)).ToArray()
                : oldData.Take(cut).Concat(oldData.Skip(cut - delta)).ToArray();

            var (result, _) = await this.AssembleAsync(oldData, newData);

            // Качаются блок с правкой и, может быть, соседний, если правка легла на
            // границу; хвост нового файла короче блока — его находит проверка конца.
            Assert.True(result.Fetched <= 2L * Bs, $"скачано {result.Fetched}, ждали не больше двух блоков");
            Assert.True(result.FromOld >= newData.Length - (2L * Bs));
        }

        /// <summary>
        /// Сдвиг окна на байт даёт то же число, что подсчёт с нуля. На этом стоит
        /// весь поиск: разойдись сдвиг с определением — ни одно смещённое окно не
        /// совпадёт, а ошибкой это не станет нигде.
        /// </summary>
        [Fact]
        public void СдвигСкользящегоХешаРавенПодсчётуСНуля() {
            const int w = 4096;
            var data = BlockData.Random(3 * w, 45);
            var top = BlockList.Power(w - 1);
            var h = BlockList.Rolling(0, data.AsSpan(0, w));
            Assert.Equal(BlockData.Weak(data.AsSpan(0, w)), h);
            for (var pos = 0; pos + w < data.Length; pos++) {
                h = unchecked(((h - (data[pos] * top)) * BlockList.RollingPrime) + data[pos + w]);
                Assert.Equal(BlockList.Rolling(0, data.AsSpan(pos + 1, w)), h);
            }
        }

        /// <summary>
        /// Скользящий хеш совпал, а SHA-256 — нет: так бывает на особо устроенных
        /// данных, и на каждом таком байте поиск считал бы SHA-256 целого окна.
        /// Сверх лимита поиск переходит на шаг в блок — выровненные блоки
        /// по-прежнему находятся.
        /// </summary>
        [Fact]
        public void ЛожныеСовпаденияСкользящегоХешаОграничены() {
            var oldData = BlockData.Random(6 * Bs, 46);
            var newData = (byte[])oldData.Clone();
            newData[(2 * Bs) + 1] ^= 1;

            // У второго блока скользящий хеш — от окна старого файла со смещением 5,
            // а SHA-256 настоящий: окно на смещении 5 совпадёт по хешу и промахнётся
            // по SHA-256.
            var raw = Convert.FromBase64String(BlockData.Encode(newData, Bs));
            BitConverter.GetBytes(BlockData.Weak(oldData.AsSpan(5, Bs))).CopyTo(raw, 1 * BlockList.DigestBytes);
            var list = BlockList.TryParse(Bs, newData.Length, Convert.ToBase64String(raw), out _)!;

            var saved = BlockFinder.MaxFalseMatches;
            BlockFinder.MaxFalseMatches = 0;
            try {
                File.WriteAllBytes(this.Old, oldData);
                var at = BlockFinder.Find(list, this.Old, CancellationToken.None);

                Assert.Equal(0, at[0]);
                Assert.Equal(-1, at[1]);
                Assert.Equal(-1, at[2]);
                Assert.Equal(3L * Bs, at[3]);
                Assert.Equal(5L * Bs, at[5]);
            }
            finally {
                BlockFinder.MaxFalseMatches = saved;
            }
        }

        /// <summary>
        /// Тысячи одинаковых блоков (мегабайты нулей в pak-архиве) находит одно
        /// совпадение. Раньше каждое следующее обходило всю их цепочку — квадрат от
        /// числа блоков, минуты на одном файле.
        /// </summary>
        [Fact]
        public void ОдинаковыеБлокиНеОбходятсяНаКаждомСовпадении() {
            const int count = 600;
            var newData = new byte[count * Bs];
            File.WriteAllBytes(this.Old, new byte[count * Bs]);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var at = BlockFinder.Find(BlockData.List(newData, Bs), this.Old, CancellationToken.None);

            Assert.All(at, a => Assert.True(a >= 0));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"{count} одинаковых блоков нашлись за {sw.Elapsed}");
        }

        /// <summary>
        /// Старый файл без единого блока нового прокатывается окном целиком, байт за
        /// байтом. Это худший случай по времени, и он обязан оставаться порядка
        /// чтения файла, а не часов.
        /// </summary>
        [Fact]
        public void ПоискПоЧужомуФайлуНеЗатягивается() {
            var newData = BlockData.Random(8 * Bs, 47);
            File.WriteAllBytes(this.Old, BlockData.Random(256 * Bs, 48));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var at = BlockFinder.Find(BlockData.List(newData, Bs), this.Old, CancellationToken.None);

            Assert.All(at, a => Assert.Equal(-1, a));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"16 МБ прокатились за {sw.Elapsed}");
        }

        [Theory]
        [InlineData(6, 9)]
        [InlineData(9, 6)]
        public async Task ФайлВыросИлиУменьшился(int oldBlocks, int newBlocks) {
            var tail = BlockData.Random(1000, 5);
            var shared = BlockData.Random(Math.Min(oldBlocks, newBlocks) * Bs, 6);
            var oldData = shared.Concat(BlockData.Random((oldBlocks * Bs) - shared.Length, 7)).Concat(tail).ToArray();
            var newData = shared.Concat(BlockData.Random((newBlocks * Bs) - shared.Length, 8)).Concat(tail).ToArray();

            var (result, _) = await this.AssembleAsync(oldData, newData);

            // Общее начало берётся из старой копии, и одинаковый хвост тоже: он у обоих
            // файлов начинается на границе блока и становится их последним блоком.
            Assert.Equal((long)shared.Length + tail.Length, result.FromOld);
        }

        /// <summary>
        /// Короткий последний блок совпадает, только если совпали и длина, и байты.
        /// Одинаковый хвост одинаковой длины берётся из старой копии.
        /// </summary>
        [Fact]
        public async Task НеполныйПоследнийБлокБерётсяИзСтаройКопии() {
            var oldData = BlockData.Random((3 * Bs) + 777, 9);
            var newData = (byte[])oldData.Clone();
            newData[5] ^= 1;

            var (result, _) = await this.AssembleAsync(oldData, newData);

            Assert.Equal((2L * Bs) + 777, result.FromOld);
            Assert.Equal(Bs, result.Fetched);
        }

        [Fact]
        public async Task ПустаяСтараяКопияКачаетсяЦеликом() {
            var newData = BlockData.Random(3 * Bs, 10);

            var (result, _) = await this.AssembleAsync(Array.Empty<byte>(), newData);

            Assert.Equal(0, result.FromOld);
            Assert.Equal(newData.Length, result.Fetched);
        }

        /// <summary>
        /// Одинаковые блоки нового файла (нули, повторы) берутся из одного и того же
        /// блока старого — сколько бы раз он ни встретился.
        /// </summary>
        [Fact]
        public async Task ПовторяющиесяБлокиБерутсяИзОдногоСтарого() {
            var zero = new byte[Bs];
            var oldData = BlockData.Random(Bs, 11).Concat(zero).ToArray();
            var newData = zero.Concat(zero).Concat(zero).Concat(zero).ToArray();

            var (result, source) = await this.AssembleAsync(oldData, newData);

            Assert.Equal(4L * Bs, result.FromOld);
            Assert.Empty(source.Requests);
        }

        /// <summary>
        /// Недостающие блоки подряд просятся одним запросом, но не длиннее потолка:
        /// обрыв посреди гигантского куска стоил бы слишком долгого ответа.
        /// </summary>
        [Fact]
        public async Task НедостающиеБлокиСклеиваютсяВЗапросыПоПотолку() {
            var count = (2 * BlockDelta.MaxRunBlocks) + 6;
            var newData = BlockData.Random(count * Bs, 12);

            var (_, source) = await this.AssembleAsync(BlockData.Random(Bs, 13), newData);

            Assert.Equal(3, source.Requests.Count);
            Assert.Equal((long)BlockDelta.MaxRunBlocks * Bs, source.Requests[0].Length);
            Assert.Equal(6L * Bs, source.Requests[2].Length);
        }

        [Fact]
        public async Task РазрозненныеПравкиНеСклеиваютсяЧерезСовпавшиеБлоки() {
            var oldData = BlockData.Random(6 * Bs, 14);
            var newData = (byte[])oldData.Clone();
            newData[(1 * Bs) + 1] ^= 1;
            newData[(2 * Bs) + 1] ^= 1;
            newData[(5 * Bs) + 1] ^= 1;

            var (_, source) = await this.AssembleAsync(oldData, newData);

            Assert.Equal(new[] { (1L * Bs, 2L * Bs), (5L * Bs, (long)Bs) }, source.Requests);
        }

        // ---------- .part от прошлой попытки ----------

        [Fact]
        public async Task ПроверенноеНачалоPartНеКачаетсяЗаново() {
            var newData = BlockData.Random(6 * Bs, 15);
            File.WriteAllBytes(this.Part, newData.AsSpan(0, (3 * Bs) + 100).ToArray());

            var (result, source) = await this.AssembleAsync(Array.Empty<byte>(), newData);

            Assert.Equal(3L * Bs, result.Resumed);
            Assert.Equal(new[] { (3L * Bs, 3L * Bs) }, source.Requests);
        }

        /// <summary>
        /// «.part» от другой версии файла или от оборванной записи обрезается по
        /// первому несовпавшему блоку, а не докачивается поверх.
        /// </summary>
        [Fact]
        public async Task ЧужойPartОбрезаетсяПоПервомуНесовпадению() {
            var newData = BlockData.Random(5 * Bs, 16);
            var stale = (byte[])newData.Clone();
            stale[(2 * Bs) + 5] ^= 1;
            File.WriteAllBytes(this.Part, stale);

            var (result, _) = await this.AssembleAsync(Array.Empty<byte>(), newData);

            Assert.Equal(2L * Bs, result.Resumed);
        }

        [Fact]
        public async Task PartДлиннееФайлаОбрезается() {
            var newData = BlockData.Random((3 * Bs) + 10, 17);
            File.WriteAllBytes(this.Part, newData.Concat(new byte[500]).ToArray());

            var (result, source) = await this.AssembleAsync(Array.Empty<byte>(), newData);

            Assert.Equal(newData.Length, result.Resumed);
            Assert.Empty(source.Requests);
        }

        /// <summary>
        /// Готовый «.part» — работа сделана: ни запросов, ни чтения старой копии.
        /// Старой копии здесь нет вовсе, и это не мешает.
        /// </summary>
        [Fact]
        public async Task ГотовыйPartНеТребуетНиСетиНиСтаройКопии() {
            var newData = BlockData.Random(4 * Bs, 18);
            File.WriteAllBytes(this.Part, newData);
            var source = new MemoryRangeSource(newData);

            var result = await BlockDelta.AssembleAsync(
                BlockData.List(newData, Bs), this.dir.PathTo("нет-такого"), this.Part, source, _ => { }, null, CancellationToken.None);

            Assert.Equal(newData.Length, result.Resumed);
            Assert.Empty(source.Requests);
        }

        // ---------- сбои источника ----------

        [Fact]
        public async Task ПорцииЛюбойДлиныСобираютсяВерно() {
            var oldData = BlockData.Random(5 * Bs, 19);
            var newData = BlockData.Random(5 * Bs, 20);
            foreach (var piece in new[] { 1, 7, Bs - 1, Bs + 3, 10 * Bs }) {
                File.Delete(this.Part);
                var source = new MemoryRangeSource(newData) { Piece = piece };
                await this.AssembleAsync(oldData, newData, source);
            }
        }

        /// <summary>
        /// Обрыв посреди куска: сверенные блоки остаются в файле, повтор просит
        /// только остаток.
        /// </summary>
        [Fact]
        public async Task ОбрывПосрединеКускаПовторяетТолькоОстаток() {
            var newData = BlockData.Random(6 * Bs, 21);
            var source = new MemoryRangeSource(newData) {
                Fault = (n, offset, length) => n == 1 ? (2 * Bs) + 100 : -1,
            };

            var (result, _) = await this.AssembleAsync(BlockData.Random(Bs, 22), newData, source);

            Assert.Equal(new[] { (0L, 6L * Bs), (2L * Bs, 4L * Bs) }, source.Requests);
            Assert.Equal(2, result.Requests);
        }

        [Fact]
        public async Task ПостоянныеОбрывыСдаютсяИОставляютПроверенноеНачало() {
            var newData = BlockData.Random(6 * Bs, 23);
            var source = new MemoryRangeSource(newData) {
                // Каждый ответ обрывается, не дойдя до конца первого же блока
                Fault = (n, offset, length) => Bs / 2,
            };

            await Assert.ThrowsAsync<IOException>(() => this.AssembleAsync(BlockData.Random(Bs, 24), newData, source));

            Assert.Equal(BlockDelta.MaxAttempts, source.Requests.Count);
            BlockData.AssertVerifiedPrefix(this.Part, newData, Bs);
        }

        /// <summary>
        /// Если каждый ответ продвигает дело хоть на блок, сдаваться незачем: счётчик
        /// неудач обнуляется продвижением, и файл докачивается на плохом канале.
        /// </summary>
        [Fact]
        public async Task ОбрывыСПродвижениемНеИсчерпываютПопытки() {
            var newData = BlockData.Random(8 * Bs, 25);
            var source = new MemoryRangeSource(newData) {
                Fault = (n, offset, length) => length > Bs ? Bs + 1 : -1,
            };

            var (result, _) = await this.AssembleAsync(BlockData.Random(Bs, 26), newData, source);

            Assert.Equal(8, result.Requests);
        }

        [Fact]
        public async Task ПодменённыеБайтыОднаждыЛечатсяПовтором() {
            var newData = BlockData.Random(4 * Bs, 27);
            var source = new MemoryRangeSource(newData) { CorruptRequests = new HashSet<int> { 1 } };

            var (result, _) = await this.AssembleAsync(BlockData.Random(Bs, 28), newData, source);

            Assert.Equal(2, result.Requests);
        }

        [Fact]
        public async Task ПостоянноПодменённыеБайтыНеПопадаютВФайл() {
            var newData = BlockData.Random(4 * Bs, 29);
            var source = new MemoryRangeSource(newData) { CorruptFrom = Bs + 3 };

            await Assert.ThrowsAsync<InvalidDataException>(() => this.AssembleAsync(BlockData.Random(Bs, 30), newData, source));

            // Первый блок пришёл честным и сверен — он остаётся, подменённый второй — нет
            Assert.Equal(Bs, new FileInfo(this.Part).Length);
            BlockData.AssertVerifiedPrefix(this.Part, newData, Bs);
        }

        /// <summary>
        /// Лишнее после последнего блока в файл не попадает: блоки пишутся только
        /// сверенными, а сверять хвост не с чем.
        /// </summary>
        [Fact]
        public async Task ЛишниеБайтыОтИсточникаНеПопадаютВФайл() {
            var newData = BlockData.Random(3 * Bs, 31);
            var source = new MemoryRangeSource(newData) { Extra = 10 };

            await this.AssembleAsync(BlockData.Random(Bs, 32), newData, source);
        }

        /// <summary>
        /// Источник, который кусков не отдаёт вовсе, не повторяется: повтор ничего не
        /// изменит, а время уйдёт.
        /// </summary>
        [Fact]
        public async Task ИсточникБезКусковНеПовторяется() {
            var newData = BlockData.Random(3 * Bs, 33);
            var source = new MemoryRangeSource(newData) { Unsupported = true };

            await Assert.ThrowsAsync<BlockRangeUnsupportedException>(() => this.AssembleAsync(BlockData.Random(Bs, 34), newData, source));

            Assert.Single(source.Requests);
        }

        /// <summary>
        /// Старую копию поменяли, пока шла сборка (игра, антивирус): блоки
        /// перепроверяются при чтении, и вместо испорченных качаются настоящие.
        /// </summary>
        [Fact]
        public async Task СтараяКопияИзмениласьПоХодуСборки() {
            var oldData = BlockData.Random(6 * Bs, 35);
            var newData = (byte[])oldData.Clone();
            newData[1] ^= 1;
            var source = new MemoryRangeSource(newData) {
                OnRequest = _ => File.WriteAllBytes(this.Old, BlockData.Random(6 * Bs, 36)),
            };

            var (result, _) = await this.AssembleAsync(oldData, newData, source);

            Assert.Equal(0, result.FromOld);
            Assert.Equal(newData.Length, result.Fetched);
        }

        // ---------- отмена ----------

        [Fact]
        public async Task ОтменаОставляетПроверенноеНачалоИСборкаПродолжается() {
            var oldData = BlockData.Random(Bs, 37);
            var newData = BlockData.Random(6 * Bs, 38);
            using var cts = new CancellationTokenSource();
            var source = new MemoryRangeSource(newData) {
                OnDelivered = total => {
                    if (total > (3 * Bs) + 10) {
                        cts.Cancel();
                    }
                },
            };
            File.WriteAllBytes(this.Old, oldData);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BlockDelta.AssembleAsync(
                BlockData.List(newData, Bs), this.Old, this.Part, source, _ => { }, null, cts.Token));
            BlockData.AssertVerifiedPrefix(this.Part, newData, Bs);
            var kept = new FileInfo(this.Part).Length;
            Assert.True(kept >= 3L * Bs, $"сверенное до отмены должно остаться, осталось {kept}");

            var again = new MemoryRangeSource(newData);
            var (result, _) = await this.AssembleAsync(oldData, newData, again, writeOld: false);
            Assert.Equal(kept, result.Resumed);
            Assert.Equal(newData.Length - kept, again.Requests.Sum(r => r.Length));
        }

        [Fact]
        public async Task ОграничительЧтенияСтарыхФайловСоблюдается() {
            var oldData = BlockData.Random(3 * Bs, 39);
            var newData = (byte[])oldData.Clone();
            newData[0] ^= 1;
            File.WriteAllBytes(this.Old, oldData);
            using var gate = new SemaphoreSlim(1);
            await gate.WaitAsync();

            var task = BlockDelta.AssembleAsync(
                BlockData.List(newData, Bs), this.Old, this.Part, new MemoryRangeSource(newData), _ => { }, gate, CancellationToken.None);
            await Task.Delay(100);
            Assert.False(task.IsCompleted, "пока ограничитель занят, старый файл читать нельзя");

            gate.Release();
            await task;
            Assert.Equal(1, gate.CurrentCount);
        }

        /// <summary>
        /// Хеши, посчитанные по ходу записи, — это хеши файла на диске: при докачке
        /// проверенного начала, при обрезке чужого хвоста, из старой копии и из сети
        /// вперемешку. Собранный файл сверяется по ним, не перечитываясь.
        /// </summary>
        /// <param name="partState">Что лежит в .part до начала: ничего, проверенное начало, испорченный хвост.</param>
        [Theory]
        [InlineData("нет")]
        [InlineData("начало")]
        [InlineData("чужой")]
        public async Task ХешиПоХодуЗаписиРавныХешамФайла(string partState) {
            var oldData = BlockData.Random(7 * Bs, 41);
            var newData = (byte[])oldData.Clone();
            newData[(1 * Bs) + 2] ^= 1;
            newData[(5 * Bs) + 2] ^= 1;
            if (partState == "начало") {
                File.WriteAllBytes(this.Part, newData.AsSpan(0, (2 * Bs) + 50).ToArray());
            }
            else if (partState == "чужой") {
                var stale = newData.AsSpan(0, 4 * Bs).ToArray();
                stale[(3 * Bs) + 1] ^= 1;
                File.WriteAllBytes(this.Part, stale);
            }

            File.WriteAllBytes(this.Old, oldData);
            using var hashes = new FileHasher.StreamingHashes();
            await BlockDelta.AssembleAsync(
                BlockData.List(newData, Bs), this.Old, this.Part, new MemoryRangeSource(newData), _ => { }, null, CancellationToken.None, hashes);

            var (sha, b3) = hashes.Finish();
            FileHasher.ComputeHashes(this.Part, out var diskSha, out var diskB3);
            Assert.Equal(diskSha, sha);
            Assert.Equal(diskB3, b3);
            Assert.Equal(newData.Length, hashes.Length);
            Assert.Equal(newData, File.ReadAllBytes(this.Part));
        }

        [Fact]
        public void БезBlake3ХешПоХодуЗаписиТолькоSha256() {
            FileHasher.Blake3AvailableForTests = false;
            try {
                using var hashes = new FileHasher.StreamingHashes();
                var data = BlockData.Random(1000, 42);
                hashes.Append(data, 0, data.Length);
                var (sha, b3) = hashes.Finish();
                Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), sha);
                Assert.Equal(string.Empty, b3);
            }
            finally {
                FileHasher.Blake3AvailableForTests = null;
            }
        }

        [Fact]
        public async Task ПрогрессСообщаетДлинуPartДоКонца() {
            var oldData = BlockData.Random(4 * Bs, 40);
            var newData = (byte[])oldData.Clone();
            newData[(2 * Bs) + 3] ^= 1;
            File.WriteAllBytes(this.Old, oldData);
            var seen = new List<long>();

            await BlockDelta.AssembleAsync(
                BlockData.List(newData, Bs), this.Old, this.Part, new MemoryRangeSource(newData), seen.Add, null, CancellationToken.None);

            Assert.Equal(newData.Length, seen[^1]);
            Assert.Equal(seen.OrderBy(x => x), seen);
        }

        private string Old => this.dir.PathTo("game/data.pak");

        private string Part => this.dir.PathTo("game/data.pak.part");

        private async Task<(BlockDeltaResult Result, MemoryRangeSource Source)> AssembleAsync(
            byte[] oldData, byte[] newData, MemoryRangeSource? source = null, bool writeOld = true) {
            Directory.CreateDirectory(Path.GetDirectoryName(this.Old)!);
            if (writeOld) {
                File.WriteAllBytes(this.Old, oldData);
            }

            source ??= new MemoryRangeSource(newData);
            var result = await BlockDelta.AssembleAsync(
                BlockData.List(newData, Bs), this.Old, this.Part, source, _ => { }, null, CancellationToken.None);

            Assert.Equal(newData, File.ReadAllBytes(this.Part));
            Assert.Equal(newData.Length, result.Resumed + result.FromOld + result.Fetched);
            return (result, source);
        }
    }

    /// <summary>Данные и эталонные хеши блоков для тестов сборки по блокам.</summary>
    internal static class BlockData {
        /// <summary>Случайные байты, воспроизводимые по зерну.</summary>
        internal static byte[] Random(int length, int seed) {
            var b = new byte[length];
            new Random(seed).NextBytes(b);
            return b;
        }

        /// <summary>Та же последовательность, что blockPattern в тестах сервера.</summary>
        internal static byte[] ServerPattern(int length) {
            var b = new byte[length];
            for (var i = 0; i < length; i++) {
                b[i] = unchecked((byte)((i * 31) + (i >> 16)));
            }

            return b;
        }

        /// <summary>
        /// Поле "blocks", посчитанное в лоб, по определению формата: Σ b[i]·P^(n−1−i)
        /// по модулю 2^64 (little-endian), затем первые восемь байт SHA-256.
        /// </summary>
        internal static string Encode(byte[] data, int blockSize) {
            var digests = new List<byte>();
            for (var off = 0; off < data.Length; off += blockSize) {
                var len = Math.Min(blockSize, data.Length - off);
                digests.AddRange(BitConverter.GetBytes(Weak(data.AsSpan(off, len))));
                digests.AddRange(SHA256.HashData(data.AsSpan(off, len)).Take(8));
            }

            return Convert.ToBase64String(digests.ToArray());
        }

        /// <summary>Скользящий хеш по определению — степенями, а не схемой Горнера, как в коде.</summary>
        internal static ulong Weak(ReadOnlySpan<byte> block) {
            ulong h = 0;
            ulong pow = 1;
            for (var i = block.Length - 1; i >= 0; i--) {
                h = unchecked(h + (block[i] * pow));
                pow = unchecked(pow * BlockList.RollingPrime);
            }

            return h;
        }

        internal static BlockList List(byte[] data, int blockSize)
            => BlockList.TryParse(blockSize, data.Length, Encode(data, blockSize), out _)
               ?? throw new InvalidOperationException("эталонный список блоков не разобрался");

        /// <summary>«.part» — это начало нового файла длиной в целое число блоков.</summary>
        internal static void AssertVerifiedPrefix(string part, byte[] expected, int blockSize) {
            var got = File.ReadAllBytes(part);
            Assert.True(got.Length % blockSize == 0 || got.Length == expected.Length, $".part длиной {got.Length} обрывается посреди блока");
            Assert.Equal(expected.AsSpan(0, got.Length).ToArray(), got);
        }
    }

    /// <summary>
    /// Источник кусков в памяти с управляемыми сбоями.
    /// </summary>
    internal sealed class MemoryRangeSource : IBlockRangeSource {
        private readonly byte[] data;
        private long delivered;

        internal MemoryRangeSource(byte[] data) {
            this.data = data;
        }

        /// <summary>Gets запрошенные куски по порядку.</summary>
        internal List<(long Offset, long Length)> Requests { get; } = new();

        /// <summary>Gets or sets длина порции, которой отдаются данные.</summary>
        internal int Piece { get; set; } = 64 * 1024;

        /// <summary>
        /// Gets or sets обрыв: по номеру запроса (с единицы), смещению и длине —
        /// сколько байт отдать до обрыва; отрицательное — не обрывать.
        /// </summary>
        internal Func<int, long, long, long>? Fault { get; set; }

        /// <summary>Gets or sets номера запросов (с единицы), в ответах на которые байты подменяются.</summary>
        internal HashSet<int> CorruptRequests { get; set; } = new();

        /// <summary>Gets or sets с какого смещения файла байты подменяются всегда; -1 — нигде.</summary>
        internal long CorruptFrom { get; set; } = -1;

        /// <summary>Gets or sets сколько лишних байт добавить в конец каждого ответа.</summary>
        internal int Extra { get; set; }

        /// <summary>Gets or sets a value indicating whether источник кусков не отдаёт вовсе.</summary>
        internal bool Unsupported { get; set; }

        /// <summary>Gets or sets что сделать перед ответом на запрос.</summary>
        internal Action<int>? OnRequest { get; set; }

        /// <summary>Gets or sets что сделать после каждой отданной порции (всего отдано байт).</summary>
        internal Action<long>? OnDelivered { get; set; }

        public async Task FetchAsync(long offset, long length, Func<ReadOnlyMemory<byte>, ValueTask> sink, CancellationToken ct) {
            this.Requests.Add((offset, length));
            var n = this.Requests.Count;
            this.OnRequest?.Invoke(n);
            if (this.Unsupported) {
                throw new BlockRangeUnsupportedException("источник отдаёт только целиком");
            }

            var body = this.data.AsSpan((int)offset, (int)length).ToArray().Concat(new byte[this.Extra]).ToArray();
            if (this.CorruptRequests.Contains(n)) {
                body[body.Length / 2] ^= 0xFF;
            }

            if (this.CorruptFrom >= 0) {
                for (var i = 0; i < body.Length; i++) {
                    if (offset + i >= this.CorruptFrom) {
                        body[i] ^= 0x5A;
                    }
                }
            }

            var cut = this.Fault?.Invoke(n, offset, length) ?? -1;
            var limit = cut >= 0 ? Math.Min(cut, body.Length) : body.Length;
            for (var at = 0; at < limit; at += this.Piece) {
                ct.ThrowIfCancellationRequested();
                var len = (int)Math.Min(this.Piece, limit - at);
                await sink(body.AsMemory(at, len));
                this.delivered += len;
                this.OnDelivered?.Invoke(this.delivered);
            }

            if (cut >= 0) {
                throw new IOException("обрыв соединения");
            }
        }
    }
}
