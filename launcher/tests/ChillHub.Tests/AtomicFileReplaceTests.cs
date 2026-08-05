// <copyright file="AtomicFileReplaceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.IO;
    using System.Text;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Подмена файла «всё или ничего» — основа и обновления, и отката.
    /// <para>
    /// Отдельно проверяется случай ЗАНЯТОЙ цели: ради него метод и написан. Апдейтер
    /// кладёт файлы поверх лаунчера, который в этот момент ещё не успел закрыться, и
    /// обычная замена там падает. Windows при этом разрешает ПЕРЕИМЕНОВАТЬ открытый
    /// файл — на этом и держится запасной путь.
    /// </para>
    /// </summary>
    public class AtomicFileReplaceTests {
        /// <summary>Цели нет — файл просто встаёт на её место, источник исчезает.</summary>
        [Fact]
        public void ОтсутствующаяЦельПростоЗанимается() {
            using var dir = new TempDir();
            var src = dir.WriteFile("new.dll", "новое");
            var dst = dir.PathTo("app.dll");

            AtomicFile.Replace(src, dst, backup: null);

            Assert.Equal("новое", File.ReadAllText(dst));
            Assert.False(File.Exists(src));
        }

        /// <summary>Старое содержимое уезжает в бэкап — без него откат невозможен.</summary>
        [Fact]
        public void СтароеСодержимоеУезжаетВБэкап() {
            using var dir = new TempDir();
            var src = dir.WriteFile("new.dll", "новое");
            var dst = dir.WriteFile("app.dll", "старое");
            var backup = dst + AtomicFile.BackupSuffix;

            AtomicFile.Replace(src, dst, backup);

            Assert.Equal("новое", File.ReadAllText(dst));
            Assert.Equal("старое", File.ReadAllText(backup));
        }

        /// <summary>Бэкап от прошлой попытки не мешает: он перезаписывается, а не копится.</summary>
        [Fact]
        public void СтарыйБэкапПерезаписывается() {
            using var dir = new TempDir();
            var backup = dir.WriteFile("app.dll" + AtomicFile.BackupSuffix, "позапрошлое");
            var src = dir.WriteFile("new.dll", "новое");
            var dst = dir.WriteFile("app.dll", "старое");

            AtomicFile.Replace(src, dst, backup);

            Assert.Equal("старое", File.ReadAllText(backup));
        }

        /// <summary>
        /// Read-only цель заменяется. Атрибут переживает распаковку архивов и копирование
        /// с сетевых дисков, а обновление из-за него вставать не должно.
        /// </summary>
        [Fact]
        public void ЦельТолькоДляЧтенияВсёРавноЗаменяется() {
            using var dir = new TempDir();
            var src = dir.WriteFile("new.dll", "новое");
            var dst = dir.WriteFile("app.dll", "старое");
            File.SetAttributes(dst, FileAttributes.ReadOnly);

            AtomicFile.Replace(src, dst, dst + AtomicFile.BackupSuffix);

            Assert.Equal("новое", File.ReadAllText(dst));
        }

        /// <summary>
        /// Занятая другим процессом цель уводится в бэкап, а новый файл встаёт на её имя.
        /// Именно так апдейтер кладёт файлы поверх ещё не закрывшегося лаунчера.
        /// </summary>
        [Fact]
        public void ЗанятаяЦельУводитсяВБэкап() {
            using var dir = new TempDir();
            var src = dir.WriteFile("new.dll", "новое");
            var dst = dir.WriteFile("app.dll", "старое");
            var backup = dst + AtomicFile.BackupSuffix;

            // FileShare.Delete — то самое разрешение, из-за которого переименование
            // открытого файла срабатывает, а замена на месте нет.
            using (var held = new FileStream(
                dst, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                AtomicFile.Replace(src, dst, backup);
            }

            Assert.Equal("новое", File.ReadAllText(dst));
            Assert.Equal("старое", File.ReadAllText(backup));
            Assert.False(File.Exists(src));
        }

        /// <summary>Запись маркера версии переживает перезапись: пустого файла посередине не бывает.</summary>
        [Fact]
        public void ПовторнаяЗаписьМаркераНеОставляетВременныхФайлов() {
            using var dir = new TempDir();
            var path = dir.PathTo("launcher.version");

            AtomicFile.WriteAllText(path, "1.2.3", new UTF8Encoding(false));
            AtomicFile.WriteAllText(path, "1.2.4", new UTF8Encoding(false));

            Assert.Equal("1.2.4", File.ReadAllText(path));
            Assert.False(File.Exists(path + AtomicFile.TempSuffix));
        }

        /// <summary>Маркер пишется без BOM: его читают и сравнивают как обычный текст.</summary>
        [Fact]
        public void МаркерПишетсяБезBom() {
            using var dir = new TempDir();
            var path = dir.PathTo("launcher.version");

            AtomicFile.WriteAllText(path, "1.2.3", new UTF8Encoding(false));

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x31, 0x2E, 0x32, 0x2E, 0x33 }, bytes);
        }

        /// <summary>Отсутствующий каталог создаётся: маркер может писаться в ещё пустую установку.</summary>
        [Fact]
        public void ОтсутствующийКаталогСоздаётся() {
            using var dir = new TempDir();
            var path = dir.PathTo("глубоко/внутри/launcher.version");

            AtomicFile.WriteAllText(path, "1.2.3", new UTF8Encoding(false));

            Assert.Equal("1.2.3", File.ReadAllText(path));
        }

        /// <summary>Удаление несуществующего файла — успех: цель «файла нет» уже достигнута.</summary>
        [Fact]
        public void УдалениеНесуществующегоЭтоУспех() {
            using var dir = new TempDir();

            Assert.True(AtomicFile.TryDelete(dir.PathTo("нет-такого")));
        }

        /// <summary>Read-only файл всё равно удаляется: атрибут не должен оставлять мусор от обновления.</summary>
        [Fact]
        public void ФайлТолькоДляЧтенияУдаляется() {
            using var dir = new TempDir();
            var path = dir.WriteFile("old.chbak", "мусор");
            File.SetAttributes(path, FileAttributes.ReadOnly);

            Assert.True(AtomicFile.TryDelete(path));
            Assert.False(File.Exists(path));
        }

        /// <summary>Занятый файл удалить нельзя — TryDelete об этом честно сообщает, а не бросает.</summary>
        [Fact]
        public void ЗанятыйФайлДаётFalseАНеИсключение() {
            using var dir = new TempDir();
            var path = dir.WriteFile("locked.dll", "занято");

            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            Assert.False(AtomicFile.TryDelete(path));
        }
    }
}
