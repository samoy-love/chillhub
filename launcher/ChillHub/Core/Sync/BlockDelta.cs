// <copyright file="BlockDelta.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Buffers;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Источник недостающих кусков файла: для лаунчера это Range-запрос к раздаче.
    /// <para>
    /// Вынесен в интерфейс, чтобы сборку файла из блоков можно было проверить без
    /// сети: обрывы, подменённые байты и сервер без Range воспроизводятся здесь
    /// одной строкой, а через настоящий HTTP — никак.
    /// </para>
    /// </summary>
    internal interface IBlockRangeSource {
        /// <summary>
        /// Отдаёт в <paramref name="sink"/> ровно <paramref name="length"/> байт файла,
        /// начиная с <paramref name="offset"/>, порциями любой длины.
        /// </summary>
        /// <param name="offset">Смещение первого байта.</param>
        /// <param name="length">Сколько байт.</param>
        /// <param name="sink">Приёмник; порции приходят по порядку.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Задача, завершающаяся, когда кусок отдан целиком.</returns>
        /// <exception cref="BlockRangeUnsupportedException">Кусок таким способом не получить вовсе.</exception>
        Task FetchAsync(long offset, long length, Func<ReadOnlyMemory<byte>, ValueTask> sink, CancellationToken ct);
    }

    /// <summary>
    /// Источник не умеет отдавать куски файла: например, раздача ответила на
    /// Range-запрос файлом целиком. Повтор тут не поможет — только загрузка целиком.
    /// </summary>
    internal sealed class BlockRangeUnsupportedException : IOException {
        /// <summary>Initializes a new instance of the <see cref="BlockRangeUnsupportedException"/> class.</summary>
        /// <param name="message">Что именно ответил источник.</param>
        internal BlockRangeUnsupportedException(string message)
            : base(message) {
        }
    }

    /// <summary>Не удалось записать в .part: это диск, а не сеть, и повтор куска не поможет.</summary>
    internal sealed class LocalWriteException : IOException {
        /// <summary>Initializes a new instance of the <see cref="LocalWriteException"/> class.</summary>
        /// <param name="inner">Что ответила файловая система.</param>
        internal LocalWriteException(IOException inner)
            : base(inner.Message, inner) {
        }
    }

    /// <summary>Что вышло из сборки файла по блокам.</summary>
    internal sealed class BlockDeltaResult {
        /// <summary>Gets or sets сколько байт уже лежало в .part от прошлой попытки и прошло сверку.</summary>
        internal long Resumed { get; set; }

        /// <summary>Gets or sets сколько байт взято из старой копии файла.</summary>
        internal long FromOld { get; set; }

        /// <summary>Gets or sets сколько байт пришло от источника и легло в файл.</summary>
        internal long Fetched { get; set; }

        /// <summary>Gets or sets сколько запросов к источнику сделано, включая повторы.</summary>
        internal int Requests { get; set; }
    }

    /// <summary>
    /// Сборка нового файла из старой копии и докачанных блоков.
    /// <para>
    /// Файл собирается в «.part» строго по порядку, и каждый блок пишется только
    /// после сверки с хешем из манифеста. Поэтому «.part» в любой момент — это
    /// проверенное начало нового файла, то же, что оставляет обычная загрузка.
    /// Прервись сборка где угодно, дальше можно идти любым путём: снова по блокам
    /// или обычной докачкой по Range, и сделанное не пропадёт.
    /// </para>
    /// <para>
    /// Блоки в старой копии ищутся не по месту, а на любом смещении (см.
    /// <see cref="BlockFinder"/>): и pak-архив Unreal, куда дописали мегабайты, и
    /// файл Unity, подросший на сотню байт, сдвигают весь хвост. Замер на
    /// обновлении Bodycam: блоки «на своём месте» экономят 42 ГБ из 53, «где
    /// угодно» — 50.
    /// </para>
    /// </summary>
    internal static class BlockDelta {
        /// <summary>
        /// Сколько блоков подряд просить одним запросом. Длиннее — меньше запросов,
        /// но обрыв посреди куска стоит повтора всего недописанного: в блоках, а не
        /// в запросах, поэтому потолок здесь скорее про время ответа, чем про трафик.
        /// </summary>
        internal const int MaxRunBlocks = 32;

        /// <summary>Сколько неудач подряд без продвижения терпим, прежде чем сдаться.</summary>
        internal const int MaxAttempts = 3;

        /// <summary>
        /// Gets or sets пауза перед повтором после неудачи номер N (с единицы).
        /// Тесты ставят ноль: ждать секунды ради проверки повтора незачем.
        /// </summary>
        internal static Func<int, TimeSpan> RetryDelay { get; set; } =
            attempt => TimeSpan.FromMilliseconds(Math.Min(4000, 500 << Math.Min(attempt - 1, 3)));

        /// <summary>
        /// Собирает новый файл в <paramref name="partPath"/>.
        /// </summary>
        /// <param name="blocks">Хеши блоков нового файла.</param>
        /// <param name="oldPath">Старая копия файла.</param>
        /// <param name="partPath">Куда собирать; уцелевшее от прошлой попытки проверяется и продолжается.</param>
        /// <param name="source">Откуда брать недостающие блоки.</param>
        /// <param name="onPartLength">Сообщает, сколько байт нового файла уже лежит в .part.</param>
        /// <param name="indexGate">Ограничитель одновременных проходов по старым файлам; null — без ограничения.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <param name="hashes">
        /// Куда отдавать каждый байт, попавший в .part, по порядку, — чтобы сверить
        /// собранный файл без его перечитывания. null — не нужно.
        /// </param>
        /// <returns>Откуда что взялось.</returns>
        /// <exception cref="BlockRangeUnsupportedException">Источник не отдаёт куски.</exception>
        /// <exception cref="InvalidDataException">Источник раз за разом отдаёт не те байты.</exception>
        /// <exception cref="IOException">Источник раз за разом обрывается.</exception>
        internal static async Task<BlockDeltaResult> AssembleAsync(
            BlockList blocks,
            string oldPath,
            string partPath,
            IBlockRangeSource source,
            Action<long> onPartLength,
            SemaphoreSlim? indexGate,
            CancellationToken ct,
            FileHasher.StreamingHashes? hashes = null) {
            var result = new BlockDeltaResult();

            // Проверка уцелевшего .part и поиск по старому файлу — чтение их
            // целиком. Потоков загрузки до шестнадцати, и без ограничителя на
            // обычном винчестере шестнадцать гигабайтных файлов читались бы разом,
            // вперемешку.
            int start;
            long[]? inOld = null;
            if (indexGate != null) {
                await indexGate.WaitAsync(ct).ConfigureAwait(false);
            }

            try {
                start = VerifyPrefix(blocks, partPath, ct, hashes);
                if (start < blocks.Count) {
                    inOld = BlockFinder.Find(blocks, oldPath, ct);
                }
            }
            finally {
                indexGate?.Release();
            }

            var written = start == blocks.Count ? blocks.FileSize : blocks.OffsetOf(start);
            result.Resumed = written;
            onPartLength(written);
            if (inOld == null) {
                return result;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(blocks.BlockSize);
            try {
                using var old = File.OpenHandle(oldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                // Запись сквозная, мимо отложенной записи Windows. Блоки из старой копии
                // идут со скоростью диска, и обычная запись за секунды набивает кеш
                // гигабайтами несброшенных данных — после чего система останавливает
                // всех пишущих, пока не сбросит их. Отмена в такой момент ждала по
                // 20–40 секунд: поток стоит внутри WriteFile и токена не видит. Замер
                // на обновлении Bodycam (53 ГБ, NVMe, кеш холодный): 405 с обычной
                // записью, 390 с сквозной — скорость та же, а отмена мгновенная и кеш
                // не вытесняет из памяти всё остальное, пока игрок во что-то играет.
                using var part = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough);
                part.SetLength(written);
                part.Seek(written, SeekOrigin.Begin);

                var i = start;
                while (i < blocks.Count) {
                    ct.ThrowIfCancellationRequested();
                    if (TryCopyFromOld(blocks, i, inOld, old, buffer, part, result, hashes)) {
                        onPartLength(part.Position);
                        i++;
                        continue;
                    }

                    var end = i + 1;
                    while (end < blocks.Count && end - i < MaxRunBlocks && inOld[end] < 0) {
                        end++;
                    }

                    await FetchRunAsync(blocks, i, end, source, part, result, onPartLength, hashes, ct).ConfigureAwait(false);
                    i = end;
                }

                part.Flush();
            }
            finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return result;
        }

        /// <summary>
        /// Проверяет уцелевший .part поблочно и обрезает его по первому несовпадению.
        /// <para>
        /// «.part» мог остаться от другой версии файла, от оборванной записи или от
        /// загрузки, которую кто-то прервал посреди блока. Докачивать поверх такого
        /// — значит собрать заведомо битый файл и узнать об этом только по полному
        /// хешу, после всей работы.
        /// </para>
        /// </summary>
        /// <param name="blocks">Хеши блоков нового файла.</param>
        /// <param name="partPath">Путь к .part.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <param name="hashes">Куда отдать байты оставленного начала; null — не нужно.</param>
        /// <returns>Сколько блоков с начала можно оставить.</returns>
        internal static int VerifyPrefix(BlockList blocks, string partPath, CancellationToken ct, FileHasher.StreamingHashes? hashes = null) {
            if (!File.Exists(partPath)) {
                return 0;
            }

            long length;
            var good = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(blocks.BlockSize);
            try {
                using (var part = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.None, 0)) {
                    length = part.Length;
                    for (var i = 0; i < blocks.Count; i++) {
                        ct.ThrowIfCancellationRequested();
                        var len = blocks.LengthOf(i);
                        if (blocks.OffsetOf(i) + len > length) {
                            break;
                        }

                        part.ReadExactly(buffer, 0, len);
                        if (!blocks.Matches(i, buffer.AsSpan(0, len))) {
                            break;
                        }

                        hashes?.Append(buffer, 0, len);
                        good = i + 1;
                    }
                }
            }
            finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var keep = good == blocks.Count ? blocks.FileSize : blocks.OffsetOf(good);
            if (keep != length) {
                using var part = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None);
                part.SetLength(keep);
            }

            return good;
        }

        private static bool TryCopyFromOld(
            BlockList blocks, int i, long[] inOld, Microsoft.Win32.SafeHandles.SafeFileHandle old,
            byte[] buffer, FileStream part, BlockDeltaResult result, FileHasher.StreamingHashes? hashes) {
            var offset = inOld[i];
            if (offset < 0) {
                return false;
            }

            var len = blocks.LengthOf(i);
            var span = buffer.AsSpan(0, len);
            var got = 0;
            while (got < len) {
                var n = RandomAccess.Read(old, span.Slice(got), offset + got);
                if (n == 0) {
                    break;
                }

                got += n;
            }

            // Старый файл могли поменять между проходами (игра, антивирус, игрок).
            // Блок перепроверяется на том, что прочитано сейчас, а не тогда.
            if (got != len || !blocks.Matches(i, span)) {
                inOld[i] = -1;
                return false;
            }

            part.Write(span);
            hashes?.Append(buffer, 0, len);
            result.FromOld += len;
            return true;
        }

        private static async Task FetchRunAsync(
            BlockList blocks, int first, int end, IBlockRangeSource source, FileStream part,
            BlockDeltaResult result, Action<long> onPartLength, FileHasher.StreamingHashes? hashes, CancellationToken ct) {
            var pending = ArrayPool<byte>.Shared.Rent(blocks.BlockSize);
            try {
                var next = first;
                var failures = 0;
                while (next < end) {
                    var current = next;
                    var filled = 0;

                    // Порции приходят какой угодно длины: копим до конца блока, сверяем,
                    // и только сверенный блок уходит в файл.
                    ValueTask Sink(ReadOnlyMemory<byte> data) {
                        var span = data.Span;
                        while (span.Length > 0) {
                            if (current >= end) {
                                throw new InvalidDataException("источник отдал больше, чем просили");
                            }

                            var len = blocks.LengthOf(current);
                            var take = Math.Min(len - filled, span.Length);
                            span.Slice(0, take).CopyTo(pending.AsSpan(filled));
                            filled += take;
                            span = span.Slice(take);
                            if (filled == len) {
                                if (!blocks.Matches(current, pending.AsSpan(0, len))) {
                                    throw new InvalidDataException($"блок {current} не совпал с манифестом");
                                }

                                try {
                                    part.Write(pending, 0, len);
                                }
                                catch (IOException ex) {
                                    // Отказ диска (кончилось место) — не сбой сети: повтор
                                    // того же куска упрётся в то же самое.
                                    throw new LocalWriteException(ex);
                                }

                                hashes?.Append(pending, 0, len);
                                result.Fetched += len;
                                current++;
                                filled = 0;
                                onPartLength(part.Position);
                            }
                        }

                        return ValueTask.CompletedTask;
                    }

                    var offset = blocks.OffsetOf(next);
                    var length = blocks.OffsetOf(end - 1) + blocks.LengthOf(end - 1) - offset;
                    try {
                        result.Requests++;
                        await source.FetchAsync(offset, length, Sink, ct).ConfigureAwait(false);
                        if (current != end) {
                            throw new IOException($"источник отдал {current - next} блок(ов) из {end - next}");
                        }

                        next = end;
                    }
                    catch (Exception ex) when (ex is not BlockRangeUnsupportedException && ex is not LocalWriteException && !ct.IsCancellationRequested) {
                        // Обрыв посреди куска не отменяет сверенных блоков: они уже в
                        // файле, и повтор просит только остаток. Счётчик неудач
                        // обнуляется всякий раз, когда дело сдвинулось.
                        failures = current > next ? 1 : failures + 1;
                        next = current;
                        if (failures >= MaxAttempts) {
                            throw;
                        }

                        ChillHub.Core.Logging.Logger.Warn(
                            $"Блоки {next}..{end - 1}: {ex.Message} — повтор {failures} из {MaxAttempts - 1}");
                        await Task.Delay(RetryDelay(failures), ct).ConfigureAwait(false);
                    }
                }
            }
            finally {
                ArrayPool<byte>.Shared.Return(pending);
            }
        }
    }
}
