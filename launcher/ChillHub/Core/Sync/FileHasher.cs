// <copyright file="FileHasher.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;

    using ChillHub.Core;

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
        /// Доступен ли Blake3. Считается один раз за запуск.
        /// <para>
        /// БЕЗ ЭТОГО ПРОПАВШАЯ БИБЛИОТЕКА ЛОЖИЛА ВСЮ ЗАГРУЗКУ. Blake3 приезжает
        /// отдельной сборкой рядом с лаунчером, и её может не оказаться: недоведённое
        /// самообновление, антивирус, ручная чистка папки. Тогда каждая сверка
        /// скачанного файла падала с FileNotFoundException, загрузчик принимал это за
        /// сбой сети и качал файл заново — три попытки на каждый из без малого тысячи
        /// файлов модпака. В журнале обращения это выглядело как 2,4 ГБ трафика,
        /// три с половиной минуты и отказ с пустым текстом.
        /// </para>
        /// <para>
        /// SHA-256 при этом на месте: он в самой платформе. Blake3 — ускорение, а не
        /// условие работы, и его отсутствие обязано стоить скорости, а не установки.
        /// </para>
        /// </summary>
        private static readonly Lazy<bool> Blake3Probe = new Lazy<bool>(ProbeBlake3, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Подменённый на время теста ответ «доступен ли Blake3». Настоящая проверка
        /// зависит от того, что лежит рядом с лаунчером, — в прогоне это не подделать,
        /// а поведение без Blake3 проверить надо.
        /// </summary>
        internal static bool? Blake3AvailableForTests { get; set; }

        /// <summary>Считается ли Blake3 на этой машине.</summary>
        internal static bool Blake3Available => Blake3AvailableForTests ?? Blake3Probe.Value;

        /// <summary>
        /// Считает SHA-256 и Blake3 за один проход по файлу.
        /// Отмену проверяем на каждом блоке: у больших файлов один проход — это минуты.
        /// </summary>
        /// <param name="path">Путь к файлу.</param>
        /// <param name="sha256Hex">SHA-256 в hex, нижний регистр.</param>
        /// <param name="blake3Hex">Blake3 в hex, нижний регистр.</param>
        /// <param name="ct">Токен отмены.</param>
        internal static void ComputeHashes(string path, out string sha256Hex, out string blake3Hex, CancellationToken ct = default) {
            if (!Blake3Available) {
                sha256Hex = ComputeSha256(path, ct);
                blake3Hex = string.Empty;
                return;
            }

            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, useAsync: false);
            using var sha = SHA256.Create();

            // using обязателен: состояние хешера живёт в НАТИВНОЙ куче, а у ref-struct
            // не бывает финализатора — без Dispose эту память не вернёт никто до выхода
            // из процесса. Считается она на КАЖДЫЙ файл, так что «Проверить файлы» на
            // сборке в пятнадцать тысяч файлов оставляла за собой десятки мегабайт.
            using var b3 = Blake3.Hasher.New();
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

        /// <summary>Один SHA-256 за проход — путь без Blake3.</summary>
        /// <param name="path">Путь к файлу.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>SHA-256 в hex, нижний регистр.</returns>
        private static string ComputeSha256(string path, CancellationToken ct) {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, useAsync: false);
            using var sha = SHA256.Create();
            var buf = new byte[ChunkSize];
            int r;
            while ((r = f.Read(buf, 0, buf.Length)) > 0) {
                ct.ThrowIfCancellationRequested();
                sha.TransformBlock(buf, 0, r, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        /// <summary>
        /// Пробует посчитать Blake3 от пустого входа. Сборка грузится при первом
        /// обращении, поэтому проверка обязана быть настоящим вызовом, а не
        /// разглядыванием файлов рядом с лаунчером.
        /// </summary>
        /// <returns>true, если Blake3 работает.</returns>
        private static bool ProbeBlake3() {
            try {
                using var h = Blake3.Hasher.New();
                Span<byte> probe = stackalloc byte[32];
                h.Finalize(probe);
                return true;
            }
            catch (Exception ex) {
                // Сюда приходят FileNotFoundException (нет Blake3.dll),
                // DllNotFoundException и BadImageFormatException (нет или не та
                // разрядность нативной blake3_dotnet.dll).
                const string note = "FileHasher: Blake3 недоступен, проверка файлов пойдёт по SHA-256. " +
                                    "Обычно это значит, что файлы лаунчера неполные — помогает переустановка";
                Logging.Logger.Error(ex, note);
                Metrics.MetricsService.Error("blake3_unavailable");
                return false;
            }
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

            // Пустой посчитанный Blake3 — это «на этой машине его не посчитать», а не
            // «не совпал». Сверять по нему нечем, и вердикт выносит SHA-256; сам файл
            // при этом проверен не хуже — оба хеша описывают одно и то же содержимое.
            var b3Checked = !string.IsNullOrWhiteSpace(expectedBlake3) && !string.IsNullOrWhiteSpace(b3Hex);
            var okB3 = !b3Checked || string.Equals(b3Hex, expectedBlake3, StringComparison.OrdinalIgnoreCase);

            // Единственный случай, когда проверить нечем совсем: манифест дал только
            // Blake3, а его нет. Молча согласиться значило бы поставить непроверенные
            // файлы, поэтому расхождение — честнее.
            if (string.IsNullOrWhiteSpace(expectedSha256) && !b3Checked) {
                reason = "blake3_unavailable";
                return false;
            }

            if (okSha && okB3) {
                return true;
            }

            reason = $"hash_mismatch shaOk={okSha} b3Ok={okB3}";
            return false;
        }
    }
}
