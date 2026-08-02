// <copyright file="UpdaterFileHelpersTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Хеш файла, которым апдейтер подтверждает, что скопированное совпадает с источником.
    /// <para>
    /// Это последняя проверка перед записью маркера версии. Маркеру лаунчер верит
    /// безоговорочно: увидев «на диске уже версия N», он выходит из проверки ДО сверки
    /// хешей. Значит ошибочно совпавший хеш навсегда закрепляет установку из смеси
    /// старых и новых сборок.
    /// </para>
    /// </summary>
    public class UpdaterSha256Tests {
        /// <summary>Контрольный вектор: пустой файл. Своя реализация обязана совпадать с общепринятой.</summary>
        [Fact]
        public void ПустойФайлДаётИзвестныйВектор() {
            using var dir = new TempDir();
            var path = dir.WriteBytes("empty.bin", Array.Empty<byte>());

            Assert.Equal(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                global::Program.Sha256Hex(path));
        }

        /// <summary>Контрольный вектор «abc» — тот же, что в спецификации SHA-256.</summary>
        [Fact]
        public void КороткийФайлДаётИзвестныйВектор() {
            using var dir = new TempDir();
            var path = dir.WriteBytes("abc.bin", Encoding.ASCII.GetBytes("abc"));

            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                global::Program.Sha256Hex(path));
        }

        /// <summary>Регистр фиксирован: результат сравнивается со строками из манифеста, записанными в нижнем регистре.</summary>
        [Fact]
        public void РезультатВНижнемРегистре() {
            using var dir = new TempDir();
            var path = dir.WriteFile("a.txt", "ChillHub");
            var hash = global::Program.Sha256Hex(path);

            Assert.Equal(hash.ToLowerInvariant(), hash);
            Assert.Equal(64, hash.Length);
        }

        /// <summary>
        /// Файл читается блоками по 256 КиБ. Ошибка в склейке блоков не заметна на
        /// мелких файлах, но ChillHub.dll и нативные библиотеки заведомо крупнее —
        /// именно они и перестали бы проверяться.
        /// </summary>
        [Theory]
        [InlineData(262144)]
        [InlineData(262145)]
        [InlineData(700000)]
        public void ФайлБольшеБуфераХешируетсяЦеликом(int size) {
            using var dir = new TempDir();
            var bytes = new byte[size];
            for (var i = 0; i < size; i++) {
                bytes[i] = (byte)(i * 31 % 251);
            }

            var path = dir.WriteBytes("big.bin", bytes);

            Assert.Equal(TestHash.Sha256OfFile(path), global::Program.Sha256Hex(path));
        }

        /// <summary>Разное содержимое — разный хеш, иначе проверка бессмысленна.</summary>
        [Fact]
        public void РазноеСодержимоеДаётРазныйХеш() {
            using var dir = new TempDir();
            var a = dir.WriteBytes("a.bin", new byte[] { 1, 2, 3 });
            var b = dir.WriteBytes("b.bin", new byte[] { 1, 2, 4 });

            Assert.NotEqual(global::Program.Sha256Hex(a), global::Program.Sha256Hex(b));
        }

        /// <summary>
        /// Файлы одного размера с разным содержимым обязаны различаться: пропуск
        /// «по совпадению размера» — штатная оптимизация копирования, и именно
        /// сверка хешей её страхует.
        /// </summary>
        [Fact]
        public void ОдинаковыйРазмерНеОзначаетОдинаковыйХеш() {
            using var dir = new TempDir();
            var a = dir.WriteFile("a.txt", "версия 1.1.7");
            var b = dir.WriteFile("b.txt", "версия 1.1.8");

            Assert.NotEqual(global::Program.Sha256Hex(a), global::Program.Sha256Hex(b));
        }

        /// <summary>
        /// Недоступный файл даёт пустую строку, а не исключение. Пустая строка не
        /// совпадёт ни с одним хешем, поэтому такой файл считается расхождением —
        /// то есть проверка проваливается в безопасную сторону.
        /// </summary>
        [Fact]
        public void НедоступныйФайлДаётПустуюСтроку() {
            using var dir = new TempDir();

            Assert.Equal(string.Empty, global::Program.Sha256Hex(dir.PathTo("нет-такого.bin")));
            Assert.Equal(string.Empty, global::Program.Sha256Hex(dir.Root));
        }

        /// <summary>Файл, занятый на запись другим процессом (антивирус, установщик), тоже даёт пустую строку, а не падение.</summary>
        [Fact]
        public void ЗанятыйФайлНеРоняетСверку() {
            using var dir = new TempDir();
            var path = dir.WriteFile("locked.bin", "x");

            using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(string.Empty, global::Program.Sha256Hex(path));
        }

        /// <summary>Чтение чужого хеша не мешает: файл открывается на чтение и не блокирует параллельных читателей.</summary>
        [Fact]
        public void ХешированиеНеБлокируетЧтениеФайла() {
            using var dir = new TempDir();
            var path = dir.WriteFile("shared.bin", "ChillHub");

            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(TestHash.Sha256OfFile(path), global::Program.Sha256Hex(path));
        }
    }

    /// <summary>
    /// Уборка служебных файлов апдейтера из папки установки.
    /// <para>
    /// Старые версии апдейтера зеркалировали в установку собственные списки и лог
    /// (filelist.txt, deletelist.txt, apply-update.log, папку updater\). Пока они там
    /// лежат, они попадают в сверку с сервером как «лишние файлы», и часть проверок
    /// целостности не сходится никогда.
    /// </para>
    /// <para>
    /// Обратная опасность важнее: уборка идёт по папке УСТАНОВКИ, где рядом лежат
    /// config.json и сохранения пользователя. Слишком широкое правило удалило бы их.
    /// </para>
    /// </summary>
    public class UpdaterCleanupArtifactsTests {
        /// <summary>Все известные артефакты прошлых версий убираются из корня установки.</summary>
        [Fact]
        public void ИзвестныеАртефактыУдаляются() {
            using var dir = new TempDir();
            foreach (var name in PreserveMatcher.UpdaterArtifactFiles) {
                dir.WriteFile(name, "мусор");
            }

            global::Program.CleanupUpdaterArtifacts(dir.Root, _ => { });

            foreach (var name in PreserveMatcher.UpdaterArtifactFiles) {
                Assert.False(File.Exists(dir.PathTo(name)), $"'{name}' остался в папке установки");
            }
        }

        /// <summary>Подпапка updater\ убирается целиком, вместе со вложенным содержимым.</summary>
        [Fact]
        public void ПодпапкаАпдейтераУдаляетсяРекурсивно() {
            using var dir = new TempDir();
            dir.WriteFile(PreserveMatcher.UpdaterArtifactDir + "/YourLauncher.Updater.exe", "x");
            dir.WriteFile(PreserveMatcher.UpdaterArtifactDir + "/sub/deep.dll", "y");

            global::Program.CleanupUpdaterArtifacts(dir.Root, _ => { });

            Assert.False(Directory.Exists(dir.PathTo(PreserveMatcher.UpdaterArtifactDir)));
        }

        /// <summary>
        /// Чужие файлы не трогаются. Отдельно проверяются config.json и launcher.version:
        /// потеря первого стирает настройки пользователя, потеря второго возвращает
        /// лаунчер в бесконечное «обновись ещё раз».
        /// </summary>
        [Fact]
        public void ЧужиеФайлыНеТрогаются() {
            using var dir = new TempDir();
            var keep = new[] {
                "config.json",
                "launcher.version",
                "ChillHub.exe",
                "Uninstall.exe",
                "filelist.txt.bak",
                "myfilelist.txt",
                "logs/apply-update.log",
                "data/filelist.txt",
                "updater-заметки.txt",
            };

            foreach (var name in keep) {
                dir.WriteFile(name, "важное");
            }

            global::Program.CleanupUpdaterArtifacts(dir.Root, _ => { });

            foreach (var name in keep) {
                Assert.True(File.Exists(dir.PathTo(name)), $"уборка удалила чужой файл '{name}'");
            }
        }

        /// <summary>Артефакт с атрибутом «только чтение» тоже удаляется: иначе он останется навсегда.</summary>
        [Fact]
        public void АртефактТолькоДляЧтенияУдаляется() {
            using var dir = new TempDir();
            var path = dir.WriteFile("filelist.txt", "мусор");
            File.SetAttributes(path, FileAttributes.ReadOnly);

            global::Program.CleanupUpdaterArtifacts(dir.Root, _ => { });

            Assert.False(File.Exists(path));
        }

        /// <summary>Чистая установка — обычный случай: уборка ничего не находит и молчит.</summary>
        [Fact]
        public void ЧистаяУстановкаНеДаётСообщений() {
            using var dir = new TempDir();
            dir.WriteFile("ChillHub.exe", "x");
            var log = new List<string>();

            global::Program.CleanupUpdaterArtifacts(dir.Root, log.Add);

            Assert.Empty(log);
        }

        /// <summary>Удаление объясняется в логе: без этого следы прошлых версий не отследить.</summary>
        [Fact]
        public void УдалениеОбъясняетсяВЛоге() {
            using var dir = new TempDir();
            dir.WriteFile("deletelist.txt", "мусор");
            var log = new List<string>();

            global::Program.CleanupUpdaterArtifacts(dir.Root, log.Add);

            Assert.Contains(log, l => l.Contains("deletelist.txt", StringComparison.Ordinal));
        }

        /// <summary>
        /// Неудача уборки не прерывает обновление: она идёт уже ПОСЛЕ копирования,
        /// и падение здесь отменило бы запись маркера у полностью применённого обновления.
        /// </summary>
        [Fact]
        public void ЗанятыйАртефактНеПрерываетОбновление() {
            using var dir = new TempDir();
            var path = dir.WriteFile("apply-update.log", "занят");
            var log = new List<string>();

            using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                global::Program.CleanupUpdaterArtifacts(dir.Root, log.Add);
            }

            Assert.Contains(log, l => l.Contains("apply-update.log", StringComparison.Ordinal));
        }

        /// <summary>Несуществующая папка установки не должна ронять уборку.</summary>
        [Fact]
        public void НесуществующаяПапкаНеРоняет() {
            using var dir = new TempDir();
            global::Program.CleanupUpdaterArtifacts(dir.PathTo("нет-такой"), _ => { });
        }
    }

    /// <summary>
    /// Предварительная проверка прав на запись в папку установки.
    /// <para>
    /// Отказ в доступе — не временная помеха, а приговор всему прогону. Раньше его
    /// ретраили с бэкоффом на КАЖДЫЙ файл (около 26 секунд), и на сотне файлов это
    /// превращалось в десятки минут тишины, после которых обновление всё равно
    /// не применялось. Проверка обязана отработать один раз и до первой записи.
    /// </para>
    /// </summary>
    public class UpdaterWriteAccessTests {
        /// <summary>Обычная папка с правами на запись проблем не даёт.</summary>
        [Fact]
        public void ЗаписываемаяПапкаПроблемНеДаёт() {
            using var dir = new TempDir();
            Assert.Null(global::Program.DescribeWriteAccess(dir.Root));
        }

        /// <summary>Папки установки может ещё не быть (первый запуск после переноса) — её создают и проверяют.</summary>
        [Fact]
        public void ОтсутствующаяПапкаСоздаётся() {
            using var dir = new TempDir();
            var target = dir.PathTo("новая/вложенная");

            Assert.Null(global::Program.DescribeWriteAccess(target));
            Assert.True(Directory.Exists(target));
        }

        /// <summary>
        /// Пробный файл не остаётся на диске. Иначе он попал бы в папку установки,
        /// а оттуда — в сверку с сервером как лишний файл.
        /// </summary>
        [Fact]
        public void ПробныйФайлНеОстаётся() {
            using var dir = new TempDir();

            Assert.Null(global::Program.DescribeWriteAccess(dir.Root));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Root));
        }

        /// <summary>Повторная проверка не спотыкается об остатки предыдущей.</summary>
        [Fact]
        public void ПовторнаяПроверкаПроходит() {
            using var dir = new TempDir();

            Assert.Null(global::Program.DescribeWriteAccess(dir.Root));
            Assert.Null(global::Program.DescribeWriteAccess(dir.Root));
        }

        /// <summary>
        /// Непригодный путь описывается текстом, а не исключением: этот текст уходит
        /// пользователю как объяснение, почему обновление не применялось.
        /// </summary>
        [Fact]
        public void ФайлВместоПапкиОписываетсяТекстом() {
            using var dir = new TempDir();
            var file = dir.WriteFile("ChillHub.exe", "x");

            var problem = global::Program.DescribeWriteAccess(file);

            Assert.False(string.IsNullOrWhiteSpace(problem));
            Assert.Contains("Exception", problem!, StringComparison.Ordinal);
        }

        /// <summary>Пустой и заведомо битый путь тоже должны дать описание, а не падение в блоке подготовки.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("C:\\недопустимый|путь")]
        public void БитыйПутьОписываетсяТекстом(string path) {
            var problem = global::Program.DescribeWriteAccess(path);

            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        /// <summary>Описание содержит тип и текст ошибки — по нему различают отказ в доступе и отсутствие диска.</summary>
        [Fact]
        public void ОписаниеСодержитТипОшибки() {
            var problem = global::Program.DescribeWriteAccess(string.Empty);

            Assert.NotNull(problem);
            Assert.Contains(": ", problem!, StringComparison.Ordinal);
        }
    }
}
