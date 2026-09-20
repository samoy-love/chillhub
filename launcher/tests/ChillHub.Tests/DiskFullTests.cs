// <copyright file="DiskFullTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core.Game;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Кончившееся посреди закачки место обязано называться своим именем.
    /// <para>
    /// ПРЕДВАРИТЕЛЬНАЯ ПРОВЕРКА ЛОВИТ НЕ ВСЁ: она смотрит на диск один раз, в начале,
    /// а обновление большой сборки идёт часами, и за это время том успевают забить и
    /// игра, и браузер, и сосед по диску. Пока такой отказ приезжал наверх обычным
    /// <c>IOException</c>, игрок читал «проверьте свободное место и права доступа» —
    /// про права там ни при чём, а сколько освободить, не сказано.
    /// </para>
    /// </summary>
    public class DiskFullTests {
        /// <summary>ERROR_DISK_FULL в HRESULT узнаётся.</summary>
        [Fact]
        public void ОтказПоМестуУзнаётсяПоКодуWindows() {
            Assert.True(SimpleSyncService.IsDiskFull(WinIo(0x70)));
        }

        /// <summary>ERROR_HANDLE_DISK_FULL — тот же отказ, но от записи в открытый файл.</summary>
        [Fact]
        public void ОтказПоМестуОтЗаписиВФайлТожеУзнаётся() {
            Assert.True(SimpleSyncService.IsDiskFull(WinIo(0x27)));
        }

        /// <summary>
        /// Цепочка вложенных исключений проходится целиком: цикл повторов заворачивает
        /// причину в своё исключение с именем файла, а блочная сборка — в своё.
        /// </summary>
        [Fact]
        public void ОтказПоМестуУзнаётсяИЗавёрнутымВЧужоеИсключение() {
            var wrapped = new IOException("Ошибка загрузки data.pak", WinIo(0x70));

            Assert.True(SimpleSyncService.IsDiskFull(wrapped));
        }

        /// <summary>
        /// Отказ в доступе местом не считается: лечится он по-другому, и советовать
        /// игроку чистить диск из-за прав — значит отправить его не туда.
        /// </summary>
        [Fact]
        public void ОтказВДоступеЗаНехваткуМестаНеСчитается() {
            // ERROR_ACCESS_DENIED
            Assert.False(SimpleSyncService.IsDiskFull(WinIo(0x05)));
            Assert.False(SimpleSyncService.IsDiskFull(new IOException("соединение оборвалось")));
            Assert.False(SimpleSyncService.IsDiskFull(null));
        }

        /// <summary>
        /// Из отказа собирается вопрос с ответом: сколько освободить. Число — остаток
        /// работы, то есть оценка сверху: освободивший меньше нужного упрётся в тот же
        /// отказ второй раз.
        /// </summary>
        [Fact]
        public void ОтказСобираетсяСОбъёмомКОсвобождению() {
            var ex = SimpleSyncService.DiskFull(Path.GetTempPath(), 7_000_000, WinIo(0x70));

            Assert.Equal(7_000_000, ex.MissingBytes);
            Assert.NotNull(ex.InnerException);
        }

        /// <summary>«Освободите 0 байт» — не совет, поэтому объём всегда положителен.</summary>
        [Fact]
        public void ПустойОстатокНеПревращаетсяВСоветОсвободитьНоль() {
            var ex = SimpleSyncService.DiskFull(Path.GetTempPath(), 0, WinIo(0x70));

            Assert.True(ex.MissingBytes > 0);
        }

        /// <summary>
        /// Строка для игрока называет диск и объём и молчит про права доступа —
        /// одна и та же и у движка, и у предварительной проверки страницы.
        /// </summary>
        [Fact]
        public void СтрокаДляИгрокаНазываетДискИОбъём() {
            var text = GameSyncRunner.NoSpaceStatus(new NotEnoughSpaceException("D:", 3_000, 1_000));

            Assert.Contains("D:", text, StringComparison.Ordinal);
            Assert.Contains("освободите", text, StringComparison.Ordinal);
            Assert.DoesNotContain("права доступа", text, StringComparison.Ordinal);
        }

        /// <summary>Неопределившийся том оставляет строку без буквы, но не ломает её.</summary>
        [Fact]
        public void БезИзвестногоТомаСтрокаОстаётсяЧитаемой() {
            var text = GameSyncRunner.NoSpaceStatus(string.Empty, 1024);

            Assert.StartsWith("На диске не хватает места", text, StringComparison.Ordinal);
        }

        /// <summary>Отказ файловой системы с кодом Win32 — так их собирает сама Windows.</summary>
        /// <param name="code">Код ошибки Win32.</param>
        /// <returns>Исключение с нужным HRESULT.</returns>
        private static IOException WinIo(int code)
            => new IOException("отказ файловой системы") { HResult = unchecked((int)0x80070000) | code };
    }
}
