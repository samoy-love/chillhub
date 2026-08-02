// <copyright file="HashVectorTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Эталонные векторы хешей.
    /// <para>
    /// Значения хешей — это контракт между сервером и КАЖДЫМ установленным лаунчером.
    /// Манифест published-версии хранит их навсегда; если библиотека начнёт считать
    /// иначе хотя бы на бит, все клиенты при следующем запуске увидят расхождение и
    /// пойдут перекачивать игры целиком — это гигабайты трафика на пустом месте, и
    /// заметит это не разработчик, а пользователи.
    /// </para>
    /// <para>
    /// Поэтому векторы прибиты здесь отдельно от остальных тестов: обычные тесты
    /// сравнивают хеш файла с хешем, посчитанным той же самой библиотекой, и смену
    /// алгоритма НЕ ловят. Эти — ловят.
    /// </para>
    /// </summary>
    public class HashVectorTests {
        /// <summary>
        /// Официальные векторы BLAKE3 (из эталонной реализации BLAKE3-team/BLAKE3).
        /// Проверяются при обновлении пакета Blake3 — он менял мажорную версию.
        /// </summary>
        [Theory]
        [InlineData("", "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")]
        [InlineData("abc", "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85")]
        public void Blake3СовпадаетСЭталоном(string input, string expected) {
            var bytes = Encoding.UTF8.GetBytes(input);
            var actual = Blake3.Hasher.Hash(bytes).ToString().ToLowerInvariant();
            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// Эталон SHA-256 от пустого входа: страхует от подмены реализации в BCL.
        /// </summary>
        [Fact]
        public void Sha256ПустогоВводаСовпадаетСЭталоном() {
            var hash = SHA256.HashData(Array.Empty<byte>());
            Assert.Equal(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                Convert.ToHexString(hash).ToLowerInvariant());
        }

        /// <summary>
        /// Тот же вектор, но через реальный путь чтения файла: важно, что многопроходный
        /// цикл по буферу не искажает результат на входе крупнее буфера.
        /// </summary>
        [Fact]
        public void ХешФайлаКрупнееБуфераСовпадаетСОднопроходным() {
            using var dir = new TempDir();

            // Больше 256 КБ — внутренний буфер FileHasher, то есть несколько проходов.
            var payload = new byte[700 * 1024];
            for (var i = 0; i < payload.Length; i++) {
                payload[i] = (byte)(i % 251);
            }

            var path = dir.WriteBytes("big.bin", payload);
            FileHasher.ComputeHashes(path, out var shaHex, out var b3Hex);

            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), shaHex);
            Assert.Equal(Blake3.Hasher.Hash(payload).ToString().ToLowerInvariant(), b3Hex);
        }

        /// <summary>
        /// Hex всегда в нижнем регистре: манифест сравнивается посимвольно, и верхний
        /// регистр означал бы расхождение на каждом файле.
        /// </summary>
        [Fact]
        public void HexВсегдаВНижнемРегистре() {
            using var dir = new TempDir();
            var path = dir.WriteFile("f.txt", "содержимое");
            FileHasher.ComputeHashes(path, out var shaHex, out var b3Hex);

            Assert.Equal(shaHex.ToLowerInvariant(), shaHex);
            Assert.Equal(b3Hex.ToLowerInvariant(), b3Hex);
            Assert.Equal(64, shaHex.Length);
            Assert.Equal(64, b3Hex.Length);
        }
    }
}
