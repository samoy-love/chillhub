// <copyright file="BlockList.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Buffers.Binary;
    using System.Security.Cryptography;

    /// <summary>
    /// Хеши блоков одного файла из манифеста: по ним файл собирается из своей
    /// старой копии и докачанных кусков, а не качается целиком.
    /// <para>
    /// Формат задаёт сервер (server/internal/adminapi/builds/blocks.go): блок —
    /// <see cref="BlockSize"/> байт подряд с начала файла, последний может быть
    /// короче. На блок — <see cref="DigestBytes"/> байт: скользящий хеш (8 байт,
    /// little-endian, см. <see cref="Rolling"/>) и первые 8 байт SHA-256; всё
    /// склеено по порядку в одну base64-строку.
    /// </para>
    /// <para>
    /// Хеш блока — подсказка, а не гарантия: собранный файл принимается только по
    /// полным SHA-256 и Blake3, как и скачанный целиком. Поэтому несогласованный
    /// список не повод отвергать манифест — он просто не используется.
    /// </para>
    /// </summary>
    internal sealed class BlockList {
        /// <summary>Сколько байт на блок хранит манифест: скользящий хеш и начало SHA-256.</summary>
        internal const int DigestBytes = 16;

        /// <summary>
        /// Множитель скользящего хеша — тот же, что у сервера (rollingPrime в blocks.go).
        /// </summary>
        internal const ulong RollingPrime = 0x100000001B3;

        /// <summary>Сколько байт SHA-256 блока хранит манифест.</summary>
        private const int StrongBytes = 8;

        /// <summary>Нижняя граница размера блока, который манифест вправе объявить.</summary>
        internal const long MinBlockSize = 64 * 1024;

        /// <summary>Верхняя граница размера блока.</summary>
        internal const long MaxBlockSize = 64 * 1024 * 1024;

        private readonly byte[] digests;

        private BlockList(int blockSize, long fileSize, byte[] digests) {
            this.BlockSize = blockSize;
            this.FileSize = fileSize;
            this.digests = digests;
        }

        /// <summary>Gets размер блока.</summary>
        internal int BlockSize { get; }

        /// <summary>Gets размер файла, которому принадлежит список.</summary>
        internal long FileSize { get; }

        /// <summary>Gets число блоков.</summary>
        internal int Count => this.digests.Length / DigestBytes;

        /// <summary>
        /// Разбирает поле "blocks" записи манифеста.
        /// </summary>
        /// <param name="blockSize">"blockSize" манифеста.</param>
        /// <param name="fileSize">Размер файла.</param>
        /// <param name="encoded">Значение "blocks".</param>
        /// <param name="problem">Почему список не годится; null, если его просто нет.</param>
        /// <returns>Список блоков либо null.</returns>
        internal static BlockList? TryParse(long blockSize, long fileSize, string? encoded, out string? problem) {
            problem = null;
            if (string.IsNullOrEmpty(encoded)) {
                return null;
            }

            if (blockSize < MinBlockSize || blockSize > MaxBlockSize) {
                problem = $"размер блока {blockSize} вне допустимого";
                return null;
            }

            byte[] raw;
            try {
                raw = Convert.FromBase64String(encoded);
            }
            catch (FormatException) {
                problem = "список блоков не в base64";
                return null;
            }

            var want = fileSize <= 0 ? 0 : ((fileSize + blockSize - 1) / blockSize);
            if (want < 2 || raw.Length != want * DigestBytes) {
                problem = $"в списке {raw.Length / DigestBytes} блоков, а размеру файла нужно {want}";
                return null;
            }

            return new BlockList((int)blockSize, fileSize, raw);
        }

        /// <summary>
        /// Хеш блока в том виде, в каком его хранит манифест.
        /// </summary>
        /// <param name="block">Содержимое блока.</param>
        /// <param name="dest">Куда положить <see cref="DigestBytes"/> байт.</param>
        internal static void Digest(ReadOnlySpan<byte> block, Span<byte> dest) {
            BinaryPrimitives.WriteUInt64LittleEndian(dest, Rolling(0, block));
            Strong(block, dest.Slice(8, StrongBytes));
        }

        /// <summary>
        /// Продолжает скользящий хеш: h·P^n + Σ data[i]·P^(n−1−i) по модулю 2^64.
        /// Хеш блока — Rolling(0, блок). Сдвиг окна длины W на байт —
        /// (h − вышедший·P^(W−1))·P + вошедший, и это то же число, что подсчёт с нуля.
        /// </summary>
        /// <param name="h">Хеш уже учтённого.</param>
        /// <param name="data">Следующие байты.</param>
        /// <returns>Хеш с учётом <paramref name="data"/>.</returns>
        internal static ulong Rolling(ulong h, ReadOnlySpan<byte> data) {
            foreach (var b in data) {
                h = unchecked((h * RollingPrime) + b);
            }

            return h;
        }

        /// <summary>P в степени <paramref name="n"/> по модулю 2^64 — множитель вышедшего байта.</summary>
        /// <param name="n">Показатель.</param>
        /// <returns>P^n.</returns>
        internal static ulong Power(int n) {
            ulong result = 1;
            ulong b = RollingPrime;
            while (n > 0) {
                if ((n & 1) != 0) {
                    result = unchecked(result * b);
                }

                b = unchecked(b * b);
                n >>= 1;
            }

            return result;
        }

        /// <summary>Скользящий хеш блока номер <paramref name="index"/> по манифесту.</summary>
        /// <param name="index">Номер блока.</param>
        /// <returns>Скользящий хеш.</returns>
        internal ulong WeakAt(int index) => BinaryPrimitives.ReadUInt64LittleEndian(this.digests.AsSpan(index * DigestBytes, 8));

        /// <summary>Начало SHA-256 блока номер <paramref name="index"/> по манифесту.</summary>
        /// <param name="index">Номер блока.</param>
        /// <returns>Восемь байт.</returns>
        internal ReadOnlySpan<byte> StrongAt(int index) => this.digests.AsSpan((index * DigestBytes) + 8, StrongBytes);

        /// <summary>Смещение блока в файле.</summary>
        /// <param name="index">Номер блока.</param>
        /// <returns>Смещение в байтах.</returns>
        internal long OffsetOf(int index) => (long)index * this.BlockSize;

        /// <summary>Длина блока: у последнего она может быть меньше <see cref="BlockSize"/>.</summary>
        /// <param name="index">Номер блока.</param>
        /// <returns>Длина в байтах.</returns>
        internal int LengthOf(int index) => (int)Math.Min(this.BlockSize, this.FileSize - this.OffsetOf(index));

        /// <summary>Совпадает ли содержимое с блоком номер <paramref name="index"/>.</summary>
        /// <param name="index">Номер блока.</param>
        /// <param name="block">Содержимое.</param>
        /// <returns>true, если длина и SHA-256 те.</returns>
        internal bool Matches(int index, ReadOnlySpan<byte> block) {
            if (block.Length != this.LengthOf(index)) {
                return false;
            }

            Span<byte> d = stackalloc byte[StrongBytes];
            Strong(block, d);
            return d.SequenceEqual(this.StrongAt(index));
        }

        /// <summary>Первые восемь байт SHA-256.</summary>
        /// <param name="block">Содержимое.</param>
        /// <param name="dest">Куда положить.</param>
        internal static void Strong(ReadOnlySpan<byte> block, Span<byte> dest) {
            Span<byte> full = stackalloc byte[32];
            SHA256.HashData(block, full);
            full.Slice(0, StrongBytes).CopyTo(dest);
        }
    }
}
