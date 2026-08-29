// <copyright file="InstalledAppsEntryTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core.Shell;

    using Microsoft.Win32;

    using Xunit;

    /// <summary>
    /// Строка «Chill Hub» в «Установка и удаление программ»: когда она появляется,
    /// что в ней написано и какой размер она показывает.
    /// <para>
    /// Настоящий ключ реестра тесты не трогают: он один на машину, и запись в него
    /// стёрла бы разработчику строку его собственной установленной копии — ровно та
    /// беда, из-за которой этот класс и появился. Ключ на время теста подставляется.
    /// </para>
    /// </summary>
    public class InstalledAppsEntryTests : IDisposable {
        /// <summary>Подставной ключ реестра: своя ветка на каждый экземпляр теста.</summary>
        private readonly string testKey = @"Software\ChillHub\Tests\InstalledAppsEntry\" + Guid.NewGuid().ToString("N");

        public InstalledAppsEntryTests() => InstalledAppsEntry.RegistryKeyPath = this.testKey;

        public void Dispose() {
            InstalledAppsEntry.ResetForTests();
            try {
                Registry.CurrentUser.DeleteSubKeyTree(this.testKey, throwOnMissingSubKey: false);
            }
            catch (Exception) {
                // Мусор в HKCU\Software\ChillHub\Tests не повод валить прогон.
            }
        }

        // ---- Размер: лаунчер плюс игры ----

        /// <summary>
        /// Размер в списке программ — это лаунчер И папка с играми.
        /// <para>
        /// Ради этого числа в список программ и заходят: сам лаунчер весит пару сотен
        /// мегабайт и среди прочих программ не выделяется ничем, а игры — десятки
        /// гигабайт. Показав только каталог установки, мы спрятали бы от человека
        /// ровно тот объём, который он ищет.
        /// </para>
        /// </summary>
        [Fact]
        public void РазмерСкладываетКаталогУстановкиИПапкуСИграми() {
            InstalledAppsEntry.DirectorySize = path => path switch {
                @"C:\App" => 200L * 1024 * 1024,
                @"D:\Games" => 30L * 1024 * 1024 * 1024,
                _ => 0,
            };

            var kib = InstalledAppsEntry.TotalSizeKib(@"C:\App", @"D:\Games");

            Assert.Equal(((200L * 1024) + (30L * 1024 * 1024)) * 1, kib);
        }

        /// <summary>
        /// Папка с играми, указанная ВНУТРИ каталога установки, не считается дважды.
        /// Путь берётся из свободного поля в настройках, так что вложенность — не
        /// экзотика, а обычная опечатка; удвоенный размер выглядел бы как утечка места.
        /// </summary>
        [Fact]
        public void ВложеннаяПапкаСИграмиНеСчитаетсяДважды() {
            InstalledAppsEntry.DirectorySize = path => path switch {
                @"C:\App" => 5L * 1024,
                @"C:\App\Games" => 3L * 1024,
                _ => 0,
            };

            Assert.Equal(5, InstalledAppsEntry.TotalSizeKib(@"C:\App", @"C:\App\Games"));
        }

        /// <summary>
        /// Соседний каталог с общим началом имени вложенным НЕ считается: D:\GamesData
        /// лежит рядом с D:\Games, а не внутри него. Сравнение префиксом строки дало бы
        /// здесь потерю всего объёма игр.
        /// </summary>
        [Theory]
        [InlineData(@"D:\Games", @"D:\GamesData", false)]
        [InlineData(@"D:\Games", @"D:\Games\ChillHub", true)]
        [InlineData(@"D:\Games", @"D:\Games", true)]
        [InlineData(@"D:\Games\", @"D:\Games", true)]
        [InlineData(@"D:\Games", "", false)]
        [InlineData("", @"D:\Games", false)]
        public void ВложенностьПутейОпределяетсяПоРазделителю(string outer, string inner, bool expected)
            => Assert.Equal(expected, InstalledAppsEntry.IsInside(outer, inner));

        /// <summary>
        /// Байты переводятся в КиБ с округлением ВВЕРХ: непустой каталог обязан
        /// показаться как 1 КиБ, а не как «размер не указан».
        /// </summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(-5, 0)]
        [InlineData(1, 1)]
        [InlineData(1024, 1)]
        [InlineData(1025, 2)]
        public void БайтыОкругляютсяВверхДоКиБ(long bytes, long expected)
            => Assert.Equal(expected, InstalledAppsEntry.ToKib(bytes));

        /// <summary>
        /// Размер обрезается по разрядности DWORD. Значение реестра .NET отдаёт как int,
        /// и каталог больше 2 ТиБ переполнил бы разряд: в списке программ появился бы
        /// отрицательный размер.
        /// </summary>
        [Fact]
        public void ОгромныйКаталогНеПереполняетРазмер()
            => Assert.Equal(int.MaxValue, InstalledAppsEntry.ToKib(long.MaxValue));

        // ---- Когда запись появляется ----

        /// <summary>
        /// Без деинсталлятора рядом запись НЕ создаётся.
        /// <para>
        /// Иначе строка «Chill Hub» появлялась бы в списке установленных программ и от
        /// сборки из bin\Debug, и от распакованного куда-нибудь архива — с кнопкой
        /// «Удалить», которая ничего не удаляет.
        /// </para>
        /// </summary>
        [Fact]
        public void БезДеинсталлятораЗаписьНеСоздаётся() {
            using var dir = new TempDir();

            Assert.False(InstalledAppsEntry.Refresh(dir.Root, dir.Root, "1.2.3"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(this.testKey));
        }

        /// <summary>
        /// У установленной копии запись создаётся заново — даже если её кто-то стёр.
        /// Именно так лаунчер возвращается в список программ после чужого тихого
        /// удаления, которое унесло общий на всю машину ключ вместе с настоящей записью.
        /// </summary>
        [Fact]
        public void УстановленнаяКопияВосстанавливаетСвоюЗапись() {
            using var install = new TempDir();
            using var games = new TempDir();
            install.WriteBytes(InstalledAppsEntry.UninstallerName, new byte[64]);
            games.WriteBytes("game/data.bin", new byte[2048]);

            Assert.True(InstalledAppsEntry.Refresh(install.Root, games.Root, "1.6.10"));

            using var key = Registry.CurrentUser.OpenSubKey(this.testKey);
            Assert.NotNull(key);
            Assert.Equal("Chill Hub", key!.GetValue("DisplayName"));
            Assert.Equal("1.6.10", key.GetValue("DisplayVersion"));
            Assert.Equal(install.Root, key.GetValue("InstallLocation"));
            Assert.Equal(
                "\"" + Path.Combine(install.Root, InstalledAppsEntry.UninstallerName) + "\"",
                key.GetValue("UninstallString"));
            Assert.Equal(1, key.GetValue("NoModify"));
            Assert.Equal(1, key.GetValue("NoRepair"));
        }

        /// <summary>
        /// Версия в записи догоняет самообновление: установщик после него не
        /// запускается, и без этого в списке программ навсегда осталась бы версия,
        /// с которой лаунчер ставили в первый раз.
        /// </summary>
        [Fact]
        public void ПовторныйЗапускПодтягиваетНовуюВерсию() {
            using var install = new TempDir();
            install.WriteBytes(InstalledAppsEntry.UninstallerName, new byte[64]);

            InstalledAppsEntry.Refresh(install.Root, string.Empty, "1.6.10");
            InstalledAppsEntry.Refresh(install.Root, string.Empty, "1.7.0");

            using var key = Registry.CurrentUser.OpenSubKey(this.testKey);
            Assert.Equal("1.7.0", key!.GetValue("DisplayVersion"));
        }

        /// <summary>
        /// Дата установки не переписывается каждым запуском: она про установку, а не
        /// про то, когда лаунчер открывали в последний раз.
        /// </summary>
        [Fact]
        public void ДатаУстановкиНеПереписываетсяПриЗапуске() {
            using var install = new TempDir();
            install.WriteBytes(InstalledAppsEntry.UninstallerName, new byte[64]);
            using (var seed = Registry.CurrentUser.CreateSubKey(this.testKey)) {
                seed!.SetValue("InstallDate", "20200101", RegistryValueKind.String);
            }

            InstalledAppsEntry.Refresh(install.Root, string.Empty, "1.6.10");

            using var key = Registry.CurrentUser.OpenSubKey(this.testKey);
            Assert.Equal("20200101", key!.GetValue("InstallDate"));
        }
    }
}
