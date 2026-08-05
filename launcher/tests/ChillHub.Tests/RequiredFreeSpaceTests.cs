// <copyright file="RequiredFreeSpaceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Сколько места обновление требует свободным.
    /// <para>
    /// Пока файлы качались в staging, ответом был весь объём загрузки: вторая копия
    /// лежала на диске целиком, рядом со старой сборкой. Теперь файл качается в «.part»
    /// рядом с целью и подменяет её сразу после сверки, поэтому старые байты
    /// освобождаются по ходу дела — и прежний ответ стал отказом в обновлении, которое
    /// спокойно поместилось бы.
    /// </para>
    /// </summary>
    public class RequiredFreeSpaceTests {
        /// <summary>
        /// Сборка меняется целиком: требовать её второй размер нельзя, нужно только
        /// место под файлы, которые качаются одновременно.
        /// </summary>
        [Fact]
        public void ЗаменаЦеликомТребуетТолькоМестоПодФайлыВРаботе() {
            var plan = Plan(replaced: 9000, sizes: new long[] { 3000, 3000, 3000 });

            // Прироста нет — новая сборка весит столько же. Остаётся запас на два
            // одновременно качаемых файла.
            Assert.Equal(6000, SimpleSyncService.RequiredFreeBytes(plan, degree: 2));
        }

        /// <summary>Чистая установка заменять не может — нужен весь объём.</summary>
        [Fact]
        public void ЧистаяУстановкаТребуетВесьОбъём() {
            var plan = Plan(replaced: 0, sizes: new long[] { 3000, 3000, 3000 });

            // 9000 прироста плюс запас на два файла в работе
            Assert.Equal(15000, SimpleSyncService.RequiredFreeBytes(plan, degree: 2));
        }

        /// <summary>
        /// Новая сборка легче старой: прирост отрицательный, но требовать «минус место»
        /// нельзя — по этой части требований просто нет.
        /// </summary>
        [Fact]
        public void СборкаЛегчеСтаройНеДаётОтрицательныхТребований() {
            var plan = Plan(replaced: 50000, sizes: new long[] { 1000, 1000 });

            Assert.Equal(2000, SimpleSyncService.RequiredFreeBytes(plan, degree: 8));
        }

        /// <summary>Качать нечего — и требовать нечего.</summary>
        [Fact]
        public void ПустойПланНичегоНеТребует() {
            Assert.Equal(0, SimpleSyncService.RequiredFreeBytes(new DiffPlan(), degree: 8));
        }

        /// <summary>
        /// Запас считается по САМЫМ ТЯЖЁЛЫМ файлам: одновременно может оказаться в работе
        /// именно худший набор, и оценка по лёгким пропустила бы переполнение диска.
        /// </summary>
        [Fact]
        public void ЗапасСчитаетсяПоСамымТяжёлымФайлам() {
            var plan = Plan(replaced: 10000, sizes: new long[] { 100, 5000, 100, 4000 });

            // Прироста нет (9200 < 10000); запас — два самых тяжёлых: 5000 + 4000
            Assert.Equal(9000, SimpleSyncService.RequiredFreeBytes(plan, degree: 2));
        }

        private static DiffPlan Plan(long replaced, long[] sizes) {
            var plan = new DiffPlan { ReplacedBytes = replaced, Downloads = new List<FileTask>() };
            for (var i = 0; i < sizes.Length; i++) {
                plan.Downloads.Add(new FileTask { RelativePath = $"file{i}.bin", Size = sizes[i] });
                plan.TotalDownloadBytes += sizes[i];
            }

            return plan;
        }
    }
}
