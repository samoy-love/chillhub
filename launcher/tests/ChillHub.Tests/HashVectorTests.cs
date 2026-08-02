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
        /// ПЕРЕКРЁСТНАЯ ПРОВЕРКА С СЕРВЕРОМ.
        /// <para>
        /// Хеши в манифест пишет сервер (Go, github.com/zeebo/blake3), а сверяет их
        /// клиент (C#, пакет Blake3). Это две независимые реализации, и разойтись они
        /// могут при обновлении любой из них. Последствие — не ошибка на экране: все
        /// клиенты просто увидят расхождение по каждому файлу и пойдут перекачивать
        /// игры целиком.
        /// </para>
        /// <para>
        /// Вход в 1 МиБ выбран намеренно: он длиннее одного блока и задействует
        /// SIMD-путь, тогда как короткие векторы из спецификации проверяют только
        /// вырожденный случай. ТА ЖЕ константа продублирована в серверном тесте
        /// server/internal/adminapi/builds/hashvector_test.go — менять её можно
        /// только в обоих файлах сразу, и только если эталон действительно пересчитан.
        /// </para>
        /// </summary>
        [Fact]
        public void Blake3СовпадаетСРеализациейСервера() {
            var ramp = new byte[1 << 20];
            for (var i = 0; i < ramp.Length; i++) {
                ramp[i] = (byte)i;
            }

            Assert.Equal(
                "64479cf7293960210547db8d982359e0c4ce054525ed7086cf93030828fc0533",
                Blake3.Hasher.Hash(ramp).ToString().ToLowerInvariant());
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
