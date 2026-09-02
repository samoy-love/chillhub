// <copyright file="FileHasherNativeMemoryTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Diagnostics;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Расход памяти на подсчёте хешей.
    /// <para>
    /// Хеши считаются по одному разу на КАЖДЫЙ файл: при обходе сборки, при сверке
    /// каждого скачанного куска, при проверке файлов самого лаунчера. Состояние Blake3
    /// живёт в нативной куче, а у ref-struct не бывает финализатора — если его не
    /// освобождать, «Проверить файлы» на сборке в пятнадцать тысяч файлов оставляет за
    /// собой десятки мегабайт до самого выхода из лаунчера, и каждая следующая проверка
    /// добавляет столько же. Со стороны это «лаунчер ничего не делает, а память растёт».
    /// </para>
    /// </summary>
    public class FileHasherNativeMemoryTests {
        /// <summary>Сколько раз хешируем: примерно сборка среднего размера.</summary>
        private const int Iterations = 50000;

        /// <summary>
        /// Хеширование сборки не оставляет за собой памяти.
        /// <para>
        /// Порог намеренно грубый: измеряется вся память процесса, и попасть в него
        /// точно нельзя. Незакрытый хешер на этом числе файлов стоит несколько десятков
        /// мегабайт — расхождение с порогом кратное, поэтому шум прогона на вердикт
        /// не влияет.
        /// </para>
        /// </summary>
        [Fact]
        public void ХешированиеСборкиНеОставляетПамятьЗаСобой() {
            if (!FileHasher.Blake3Available) {
                // Без Blake3 считается один SHA-256, и проверять тут нечего.
                return;
            }

            using var dir = new TempDir();
            var path = dir.WriteFile("chunk.bin", new string('x', 8192));

            // Прогрев: первый проход поднимает нативную библиотеку и раздаёт буферы,
            // и без него в замер попал бы разовый расход, а не утечка.
            for (var i = 0; i < 500; i++) {
                FileHasher.ComputeHashes(path, out _, out _);
            }

            var before = NativeBytes();
            for (var i = 0; i < Iterations; i++) {
                FileHasher.ComputeHashes(path, out _, out _);
            }

            var grown = NativeBytes() - before;
            Assert.True(
                grown < 6L * 1024 * 1024,
                $"после {Iterations} хеширований нативная память выросла на {grown / (1024 * 1024)} МБ — состояние хешера не освобождается");
        }

        /// <summary>
        /// Память процесса за вычетом управляемой кучи — то, что удерживает нативный код.
        /// <para>
        /// Раньше здесь были просто private bytes, и тест мерил заодно управляемую кучу.
        /// Сборщик мусора не обязан возвращать системе уже занятые сегменты, а сколько
        /// их — зависит от числа ядер и объёма памяти машины. Из-за этого тест проходил
        /// на одном раннере и падал на другом, ничего не сообщая о том, ради чего
        /// написан: об утечке в нативном состоянии Blake3.
        /// </para>
        /// <para>
        /// Вычитается именно <c>TotalCommittedBytes</c>, а не <c>GetTotalMemory</c>:
        /// второй возвращает байты, занятые живыми объектами, и разница между ним и
        /// private bytes всё ещё включает свободные, но уже взятые у системы сегменты
        /// кучи. С ним замер оставался шумным ровно так же.
        /// </para>
        /// </summary>
        private static long NativeBytes() {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var self = Process.GetCurrentProcess();
            return self.PrivateMemorySize64 - GC.GetGCMemoryInfo().TotalCommittedBytes;
        }
    }
}
