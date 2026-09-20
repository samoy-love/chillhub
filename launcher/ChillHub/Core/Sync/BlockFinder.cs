// <copyright file="BlockFinder.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// Ищет блоки нового файла в старой копии на любом смещении — окном размером в
    /// блок, которое катится по старому файлу с шагом в байт (схема zsync).
    /// <para>
    /// Поиск только по местам, кратным блоку, не переживает сдвига: правка,
    /// вставившая в файл сотню байт, уводит весь хвост с границ блоков, и ни один
    /// блок после неё не совпадает. Так обновляются игры на Unity — PEAK 2.4.1 →
    /// 2.4.3 по выровненным блокам экономил 30 МБ из 4.28 ГБ, скользящим окном —
    /// 1.8 ГБ. На Unreal (Bodycam) не хуже: 2.61 ГБ к загрузке против 2.64.
    /// </para>
    /// <para>
    /// Сдвиг окна на байт — одно умножение (<see cref="BlockList.Rolling"/>), и
    /// проверка по битовой маске старших битов хеша: в словарь и тем более к
    /// SHA-256 окна поиск идёт только при попадании в маску. После совпадения окно
    /// прыгает сразу на блок вперёд — на совпадающих файлах поиск стоит столько же,
    /// сколько чтение.
    /// </para>
    /// </summary>
    internal static class BlockFinder {
        /// <summary>Маска не больше 2^24 бит (два мегабайта) — на десятки тысяч блоков.</summary>
        private const int MaxMaskBits = 24;

        /// <summary>
        /// Маска не меньше 2^16 бит — восемь килобайт; и не реже одного занятого
        /// бита на 2^10: ложное попадание в маску стоит поиска в словаре.
        /// </summary>
        private const int MinMaskBits = 16;

        /// <summary>Сколько байт старого файла читать за раз сверх окна.</summary>
        private const int ChunkBlocks = 4;

        /// <summary>
        /// Gets or sets сколько раз скользящий хеш может совпасть при другом SHA-256,
        /// прежде чем поиск по файлу перейдёт на шаг в блок.
        /// <para>
        /// Каждое такое совпадение — SHA-256 целого окна, мегабайт работы вместо
        /// одного умножения. На обычных данных их почти не бывает, но полиномиальный
        /// хеш по модулю 2^64 известен совпадениями на особо устроенных данных, и
        /// попадись такие в файле — поиск считал бы мегабайт на каждый байт. Лимит
        /// ограничивает худший случай гигабайтом лишнего хеширования на файл.
        /// </para>
        /// </summary>
        internal static int MaxFalseMatches { get; set; } = 1024;

        /// <summary>
        /// Где в старом файле лежит каждый блок нового.
        /// </summary>
        /// <param name="blocks">Блоки нового файла.</param>
        /// <param name="oldPath">Старая копия.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Смещение в старом файле для каждого блока нового; -1 — не нашёлся.</returns>
        internal static long[] Find(BlockList blocks, string oldPath, CancellationToken ct) {
            var at = new long[blocks.Count];
            Array.Fill(at, -1L);

            using var f = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0);
            var length = f.Length;
            if (length >= blocks.BlockSize) {
                new Scan(blocks, f, at, ct).Run();
            }

            FindTail(blocks, f, length, at);
            return at;
        }

        /// <summary>
        /// Последний блок короче окна, катящимся окном его не найти. Где он может
        /// лежать в старом файле без сдвига внутри себя: в конце (хвост файла не
        /// менялся) или там же, где в новом.
        /// </summary>
        private static void FindTail(BlockList blocks, FileStream f, long length, long[] at) {
            var last = blocks.Count - 1;
            var len = blocks.LengthOf(last);
            if (len == blocks.BlockSize || at[last] >= 0) {
                return;
            }

            var buffer = new byte[len];
            foreach (var offset in new[] { length - len, blocks.OffsetOf(last) }) {
                if (offset < 0 || offset + len > length) {
                    continue;
                }

                if (RandomAccess.Read(f.SafeFileHandle, buffer, offset) == len && blocks.Matches(last, buffer)) {
                    at[last] = offset;
                    return;
                }
            }
        }

        /// <summary>Один проход скользящего окна по старому файлу.</summary>
        private sealed class Scan {
            private readonly BlockList blocks;
            private readonly FileStream file;
            private readonly long[] at;
            private readonly CancellationToken ct;
            private readonly int window;
            private readonly ulong top;
            private readonly int maskShift;
            private readonly ulong[] mask;
            private readonly Dictionary<ulong, int> head = new();

            // Цепочка разных блоков с одним скользящим хешем.
            private readonly int[] next;

            // Цепочка блоков, одинаковых целиком (нули, повторы): их находит одно
            // совпадение, и обходить её на каждом следующем незачем.
            private readonly int[] same;
            private readonly byte[] buf;
            private readonly byte[] strong = new byte[8];

            private int filled;
            private int pos;
            private long bufStart;
            private bool eof;
            private int falseMatches;

            internal Scan(BlockList blocks, FileStream file, long[] at, CancellationToken ct) {
                this.blocks = blocks;
                this.file = file;
                this.at = at;
                this.ct = ct;
                this.window = blocks.BlockSize;
                this.top = BlockList.Power(this.window - 1);
                this.next = new int[blocks.Count];
                this.same = new int[blocks.Count];

                // Маска по числу блоков: у файла в два мегабайта она восемь
                // килобайт, а не два мегабайта в куче больших объектов на каждый.
                var bits = MinMaskBits;
                while (bits < MaxMaskBits && (1L << bits) < (long)blocks.Count << 10) {
                    bits++;
                }

                this.maskShift = 64 - bits;
                this.mask = new ulong[1 << (bits - 6)];
                this.buf = ArrayPool<byte>.Shared.Rent(this.window * (ChunkBlocks + 1));

                for (var i = 0; i < blocks.Count; i++) {
                    this.next[i] = -1;
                    this.same[i] = -1;
                    if (blocks.LengthOf(i) != this.window) {
                        continue;
                    }

                    var w = blocks.WeakAt(i);
                    if (!this.head.TryGetValue(w, out var h)) {
                        this.head[w] = i;
                        var k = w >> this.maskShift;
                        this.mask[k >> 6] |= 1UL << (int)(k & 63);
                        continue;
                    }

                    // Такой же блок уже есть — встаём в его группу.
                    var leader = h;
                    while (leader >= 0 && !blocks.StrongAt(leader).SequenceEqual(blocks.StrongAt(i))) {
                        leader = this.next[leader];
                    }

                    if (leader >= 0) {
                        this.same[i] = this.same[leader];
                        this.same[leader] = i;
                    }
                    else {
                        this.next[i] = h;
                        this.head[w] = i;
                    }
                }
            }

            internal void Run() {
                try {
                    if (this.head.Count == 0 || !this.Ensure(this.window)) {
                        return;
                    }

                    var h = BlockList.Rolling(0, this.buf.AsSpan(this.pos, this.window));
                    var alignedOnly = false;
                    while (true) {
                        this.ct.ThrowIfCancellationRequested();
                        var matched = this.TryMatch(h, ref alignedOnly);

                        if (matched || alignedOnly) {
                            this.pos += this.window;
                            if (!this.Ensure(this.window)) {
                                return;
                            }

                            h = BlockList.Rolling(0, this.buf.AsSpan(this.pos, this.window));
                            continue;
                        }

                        // Катимся на байт, а дальше — быстрым циклом до следующего
                        // попадания в маску или до конца прочитанного.
                        if (!this.Ensure(this.window + 1)) {
                            return;
                        }

                        h = this.RollUntilCandidate(h);
                    }
                }
                finally {
                    ArrayPool<byte>.Shared.Return(this.buf);
                }
            }

            /// <summary>Сдвигает окно, пока хеш не попадёт в маску или не кончатся данные в буфере.</summary>
            private ulong RollUntilCandidate(ulong h) {
                var b = this.buf;
                var m = this.mask;
                var w = this.window;
                var t = this.top;
                var p = this.pos;
                var limit = this.filled - w;
                var shift = this.maskShift;
                unchecked {
                    h = ((h - (b[p] * t)) * BlockList.RollingPrime) + b[p + w];
                    p++;
                    while (p < limit) {
                        var k = h >> shift;
                        if ((m[(int)(k >> 6)] & (1UL << (int)k)) != 0) {
                            break;
                        }

                        h = ((h - (b[p] * t)) * BlockList.RollingPrime) + b[p + w];
                        p++;
                    }
                }

                this.pos = p;
                return h;
            }

            /// <summary>Проверяет окно в текущей позиции: маска, словарь, SHA-256.</summary>
            private bool TryMatch(ulong h, ref bool alignedOnly) {
                var k = h >> this.maskShift;
                if ((this.mask[(int)(k >> 6)] & (1UL << (int)k)) == 0 || !this.head.TryGetValue(h, out var i)) {
                    return false;
                }

                BlockList.Strong(this.buf.AsSpan(this.pos, this.window), this.strong);
                var matched = false;
                for (; i >= 0; i = this.next[i]) {
                    if (!this.blocks.StrongAt(i).SequenceEqual(this.strong)) {
                        continue;
                    }

                    matched = true;
                    if (this.at[i] >= 0) {
                        break; // группа уже найдена раньше
                    }

                    for (var j = i; j >= 0; j = this.same[j]) {
                        this.at[j] = this.bufStart + this.pos;
                    }

                    break;
                }

                if (!matched && ++this.falseMatches > MaxFalseMatches && !alignedOnly) {
                    alignedOnly = true;
                    ChillHub.Core.Logging.Logger.Warn(
                        $"Поиск блоков: скользящий хеш совпал {this.falseMatches} раз впустую — дальше только по границам блоков");
                }

                return matched;
            }

            /// <summary>Добивается, чтобы с позиции окна в буфере лежало не меньше <paramref name="need"/> байт.</summary>
            private bool Ensure(int need) {
                while (this.filled - this.pos < need) {
                    if (this.eof) {
                        return false;
                    }

                    // Прочитанное до окна больше не понадобится — сдвигаем остаток в начало.
                    var keep = this.filled - this.pos;
                    Buffer.BlockCopy(this.buf, this.pos, this.buf, 0, keep);
                    this.bufStart += this.pos;
                    this.pos = 0;
                    this.filled = keep;

                    var capacity = this.window * (ChunkBlocks + 1);
                    var read = this.file.ReadAtLeast(this.buf.AsSpan(this.filled, capacity - this.filled), capacity - this.filled, throwOnEndOfStream: false);
                    this.filled += read;
                    if (this.filled < capacity) {
                        this.eof = true;
                    }
                }

                return true;
            }
        }
    }
}
