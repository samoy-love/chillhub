// <copyright file="BlockList.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Security.Cryptography;

    /// <summary>
    /// Хеши блоков одного файла из манифеста: по ним файл собирается из своей
    /// старой копии и докачанных кусков, а не качается целиком.
    /// <para>
    /// Формат задаёт сервер (server/internal/adminapi/builds/blocks.go): блок —
    /// <see cref="BlockSize"/> байт подряд с начала файла, последний может быть
    /// короче; хеш блока — первые <see cref="DigestBytes"/> байт его SHA-256; хеши
    /// склеены по порядку в одну base64-строку.
    /// </para>
    /// <para>
    /// Хеш блока — подсказка, а не гарантия: собранный файл принимается только по
    /// полным SHA-256 и Blake3, как и скачанный целиком. Поэтому несогласованный
    /// список не повод отвергать манифест — он просто не используется.
    /// </para>
    /// </summary>
    internal sealed class BlockList {
        /// <summary>Сколько байт SHA-256 блока хранит манифест.</summary>
        internal const int DigestBytes = 12;

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
            Span<byte> full = stackalloc byte[32];
            SHA256.HashData(block, full);
            full.Slice(0, DigestBytes).CopyTo(dest);
        }

        /// <summary>Хеш блока номер <paramref name="index"/> по манифесту.</summary>
        /// <param name="index">Номер блока.</param>
        /// <returns>Хеш блока.</returns>
        internal ReadOnlySpan<byte> DigestAt(int index) => this.digests.AsSpan(index * DigestBytes, DigestBytes);

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
        /// <returns>true, если хеш и длина те.</returns>
        internal bool Matches(int index, ReadOnlySpan<byte> block) {
            if (block.Length != this.LengthOf(index)) {
                return false;
            }

            Span<byte> d = stackalloc byte[DigestBytes];
            Digest(block, d);
            return d.SequenceEqual(this.DigestAt(index));
        }
    }
}
