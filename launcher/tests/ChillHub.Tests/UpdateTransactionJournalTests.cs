// <copyright file="UpdateTransactionJournalTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Журнал транзакции обновления: что именно откат считает «исходным состоянием».
    /// <para>
    /// Откат обязан вернуть установку в состояние ДО транзакции, а не в промежуточное.
    /// Смесь старых и новых файлов — это неработающий лаунчер, который уже не может
    /// обновиться сам, и чинится только переустановкой.
    /// </para>
    /// </summary>
    public class UpdateTransactionJournalTests {
        /// <summary>
        /// Повторная запись в тот же путь не затирает бэкап ИСХОДНОГО содержимого.
        /// <para>
        /// Имя бэкапа выводится из имени цели, а замена сносит старый бэкап перед
        /// подменой. Значит второе копирование того же файла (починка после расхождения
        /// хешей или дубль строки в filelist) оставило бы в бэкапе содержимое первой
        /// копии — то есть уже НОВОЕ. Откат после этого «успешно восстанавливал» новый
        /// файл и давал ровно ту смесь сборок, ради которой транзакция и заведена.
        /// </para>
        /// </summary>
        [Fact]
        public void ПовторнаяЗаписьНеЗатираетИсходныйБэкап() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "ИСХОДНОЕ");
            var first = dir.WriteFile("src/first.dll", "первое новое");
            var second = dir.WriteFile("src/second.dll", "второе новое");

            var tx = new UpdateTransaction(_ => { });
            tx.CopyFile(first, dst);
            tx.CopyFile(second, dst);

            Assert.Equal("второе новое", File.ReadAllText(dst));
            Assert.Equal(1, tx.Count);

            tx.Rollback();

            Assert.Equal("ИСХОДНОЕ", File.ReadAllText(dst));
        }

        /// <summary>
        /// Пропавший бэкап — это провал отката, и молчать о нём нельзя: строка
        /// «rollback: failed=0» читается как «установка вернулась в прежнее состояние»,
        /// а она в этот момент смешанная.
        /// </summary>
        [Fact]
        public void ПропавшийБэкапПризнаётсяПроваломОтката() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "исходное");
            var src = dir.WriteFile("src/app.dll", "новое");

            var lines = new List<string>();
            var tx = new UpdateTransaction(lines.Add);
            tx.CopyFile(src, dst);

            // Бэкап пропал: антивирус, уборка диска, ручное вмешательство.
            File.Delete(dst + AtomicFile.BackupSuffix);

            tx.Rollback();

            Assert.Contains(lines, l => l.Contains("ROLLBACK FAILED", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("failed=1", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("failed=0", StringComparison.Ordinal));
        }

        /// <summary>Успешный откат отчитывается честно: восстановлено столько, сколько меняли.</summary>
        [Fact]
        public void УспешныйОткатОтчитываетсяБезПровалов() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "исходное");
            var src = dir.WriteFile("src/app.dll", "новое");

            var lines = new List<string>();
            var tx = new UpdateTransaction(lines.Add);
            tx.CopyFile(src, dst);
            tx.Rollback();

            Assert.Contains(lines, l => l.Contains("restored=1 failed=0", StringComparison.Ordinal));
        }

        /// <summary>
        /// Откат идёт в обратном порядке: последний применённый файл откатывается первым.
        /// Так восстановление зеркалит применение и не наступает само на себя.
        /// </summary>
        [Fact]
        public void ОткатВозвращаетВсеФайлыПачкиКИсходному() {
            using var dir = new TempDir();
            var a = dir.WriteFile("a.dll", "старое A");
            var b = dir.WriteFile("b.dll", "старое B");
            var c = dir.PathTo("c.dll");     // этого файла в установке не было
            var src = dir.WriteFile("src/new.dll", "новое");

            var tx = new UpdateTransaction(_ => { });
            tx.CopyFile(src, a);
            tx.CopyFile(src, b);
            tx.CopyFile(src, c);
            Assert.Equal(3, tx.Count);

            tx.Rollback();

            Assert.Equal("старое A", File.ReadAllText(a));
            Assert.Equal("старое B", File.ReadAllText(b));
            Assert.False(File.Exists(c), "созданный транзакцией файл не убран откатом");
        }

        /// <summary>Откат опустошает журнал: повторный вызов уже ничего не «восстанавливает».</summary>
        [Fact]
        public void ОткатОпустошаетЖурнал() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "исходное");
            var src = dir.WriteFile("src/app.dll", "новое");

            var tx = new UpdateTransaction(_ => { });
            tx.CopyFile(src, dst);
            tx.Rollback();

            Assert.Equal(0, tx.Count);

            tx.Rollback();
            Assert.Equal("исходное", File.ReadAllText(dst));
        }

        /// <summary>Подтверждение тоже опустошает журнал — откатывать после него нечего.</summary>
        [Fact]
        public void ПодтверждениеОпустошаетЖурнал() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "исходное");
            var src = dir.WriteFile("src/app.dll", "новое");

            var tx = new UpdateTransaction(_ => { });
            tx.CopyFile(src, dst);
            tx.Commit();

            Assert.Equal(0, tx.Count);

            tx.Rollback();
            Assert.Equal("новое", File.ReadAllText(dst));
        }

        /// <summary>
        /// Занятый бэкап не даёт удалить его при подтверждении — это не ошибка обновления,
        /// а безобидный хвост, который уберёт следующий прогон. Но сказать об этом надо.
        /// </summary>
        [Fact]
        public void НеудалённыйБэкапПриПодтвержденииТолькоЛогируется() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "исходное");
            var src = dir.WriteFile("src/app.dll", "новое");

            var lines = new List<string>();
            var tx = new UpdateTransaction(lines.Add);
            tx.CopyFile(src, dst);

            var backup = dst + AtomicFile.BackupSuffix;
            using (var held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                tx.Commit();
            }

            Assert.Contains(lines, l => l.Contains("backup file(s) could not be removed", StringComparison.Ordinal));
            Assert.Equal("новое", File.ReadAllText(dst));
        }

        /// <summary>Логгер не задан — транзакция всё равно работает: диагностика не обязательна.</summary>
        [Fact]
        public void ОтсутствующийЛоггерНеРоняетТранзакцию() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "исходное");
            var src = dir.WriteFile("src/app.dll", "новое");

            var tx = new UpdateTransaction(null!);
            tx.CopyFile(src, dst);
            tx.Rollback();

            Assert.Equal("исходное", File.ReadAllText(dst));
        }

        /// <summary>Уборка хвостов не заходит в несуществующую папку и не бросает.</summary>
        [Fact]
        public void УборкаХвостовВНесуществующейПапкеБезопасна() {
            using var dir = new TempDir();

            UpdateTransaction.CleanupLeftovers(dir.PathTo("нет-такой"), _ => { });
        }

        /// <summary>Уборка хвостов достаёт до вложенных папок: файлы обновления лежат деревом.</summary>
        [Fact]
        public void УборкаХвостовДостаётДоВложенныхПапок() {
            using var dir = new TempDir();
            dir.WriteFile("sub/deep/a.dll" + AtomicFile.TempSuffix, "обрывок");
            dir.WriteFile("sub/deep/b.dll" + AtomicFile.BackupSuffix, "старое");
            var keep = dir.WriteFile("sub/deep/c.dll", "живое");

            UpdateTransaction.CleanupLeftovers(dir.Root, _ => { });

            Assert.False(File.Exists(dir.PathTo("sub/deep/a.dll" + AtomicFile.TempSuffix)));
            Assert.False(File.Exists(dir.PathTo("sub/deep/b.dll" + AtomicFile.BackupSuffix)));
            Assert.True(File.Exists(keep), "уборка снесла настоящий файл установки");
        }
    }
}
