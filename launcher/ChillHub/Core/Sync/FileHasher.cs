// <copyright file="FileHasher.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;

    /// <summary>
    /// Единственное место, где считаются хеши файла и принимается решение
    /// «локальный файл соответствует записи манифеста».
    ///
    /// Раньше этот цикл существовал в трёх независимых копиях (планировщик диффа,
    /// верификация скачанного .part и сверка файлов самообновления в UpdateWindow),
    /// и копии успели разойтись по поведению: одна проверяла отмену, другая нет.
    /// Разные вердикты на одинаковых входах — это ровно тот класс бага, при котором
    /// лаунчер бесконечно предлагает одно и то же обновление.
    /// </summary>
    internal static class FileHasher {
        /// <summary>Размер блока, который за раз скармливается обоим хешерам.</summary>
        private const int ChunkSize = 256 * 1024;

        /// <summary>Размер буфера FileStream.</summary>
        private const int StreamBufferSize = 128 * 1024;

        /// <summary>
        /// Считает SHA-256 и Blake3 за один проход по файлу.
        /// Отмену проверяем на каждом блоке: у больших файлов один проход — это минуты.
        /// </summary>
        /// <param name="path">Путь к файлу.</param>
        /// <param name="sha256Hex">SHA-256 в hex, нижний регистр.</param>
        /// <param name="blake3Hex">Blake3 в hex, нижний регистр.</param>
        /// <param name="ct">Токен отмены.</param>
        internal static void ComputeHashes(string path, out string sha256Hex, out string blake3Hex, CancellationToken ct = default) {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, useAsync: false);
            using var sha = SHA256.Create();
            var b3 = Blake3.Hasher.New();
            var buf = new byte[ChunkSize];
            int r;

            // NOTE: Use synchronous reads to avoid awaiting while a ref-struct (Hasher) is alive (C# 12 limitation)
            while ((r = f.Read(buf, 0, buf.Length)) > 0) {
                ct.ThrowIfCancellationRequested();
                sha.TransformBlock(buf, 0, r, null, 0);
                b3.Update(new ReadOnlySpan<byte>(buf, 0, r));
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha256Hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            var b3out = new byte[32];
            b3.Finalize(b3out);
            blake3Hex = Convert.ToHexString(b3out).ToLowerInvariant();
        }

        /// <summary>
        /// Сравнивает фактический файл на диске с ожидаемыми размером и хешами.
        /// Пустой sha256/blake3 означает «этот хеш не проверяем»; если пусты оба —
        /// остаётся сравнение по размеру.
        /// </summary>
        /// <param name="path">Полный путь к локальному файлу.</param>
        /// <param name="expectedSize">Размер из манифеста (0 — размер неизвестен).</param>
        /// <param name="expectedSha256">Ожидаемый SHA-256 (hex, регистр не важен) или пусто.</param>
        /// <param name="expectedBlake3">Ожидаемый Blake3 (hex, регистр не важен) или пусто.</param>
        /// <param name="reason">Человекочитаемая причина расхождения (для лога).</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>true, если файл на месте и соответствует манифесту.</returns>
        internal static bool Matches(
            string path,
            long expectedSize,
            string? expectedSha256,
            string? expectedBlake3,
            out string reason,
            CancellationToken ct = default) {
            reason = string.Empty;
            if (!File.Exists(path)) {
                reason = "missing";
                return false;
            }

            var info = new FileInfo(path);
            if (string.IsNullOrWhiteSpace(expectedSha256) && string.IsNullOrWhiteSpace(expectedBlake3)) {
                // Фоллбэк: хешей нет, сверяем только длину.
                if (info.Length == expectedSize) {
                    return true;
                }

                reason = $"size {info.Length} != {expectedSize}";
                return false;
            }

            // Размер отличается — хеш заведомо не совпадёт, файл читать незачем.
            // Это чистая оптимизация (экономит чтение гигабайтов), вердикт она не меняет.
            if (expectedSize > 0 && info.Length != expectedSize) {
                reason = $"size {info.Length} != {expectedSize}";
                return false;
            }

            ComputeHashes(path, out var shaHex, out var b3Hex, ct);
            var okSha = string.IsNullOrWhiteSpace(expectedSha256) || string.Equals(shaHex, expectedSha256, StringComparison.OrdinalIgnoreCase);
            var okB3 = string.IsNullOrWhiteSpace(expectedBlake3) || string.Equals(b3Hex, expectedBlake3, StringComparison.OrdinalIgnoreCase);
            if (okSha && okB3) {
                return true;
            }

            reason = $"hash_mismatch shaOk={okSha} b3Ok={okB3}";
            return false;
        }
    }
}
