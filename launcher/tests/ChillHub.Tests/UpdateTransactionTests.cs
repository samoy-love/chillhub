// <copyright file="UpdateTransactionTests.cs" company="PlaceholderCompany">
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
    /// Транзакция применения обновления: подмена файлов, подтверждение, откат.
    /// <para>
    /// Это самый дорогой по последствиям код в проекте: он переписывает файлы РАБОТАЮЩЕЙ
    /// установки. Раньше здесь стоял <c>FileMode.Create</c> — цель усекалась и писалась
    /// поверх, без бэкапа и без возможности отката, поэтому обрыв посреди записи оставлял
    /// пользователя с нулевым ChillHub.dll и неспособным запуститься лаунчером.
    /// </para>
    /// <para>
    /// Покрытия у этого кода не было вовсе: он проверялся только ручными прогонами.
    /// </para>
    /// </summary>
    public class UpdateTransactionTests {
        private static readonly List<string> Sink = new List<string>();

        /// <summary>Подтверждённая транзакция оставляет новое содержимое и убирает бэкапы.</summary>
        [Fact]
        public void ПодтверждениеОставляетНовоеСодержимоеИЧиститБэкапы() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "СТАРОЕ");
            var src = dir.WriteFile("new-app.dll", "НОВОЕ");

            var tx = new UpdateTransaction(Log);
            tx.CopyFile(src, dst);
            Assert.Equal(1, tx.Count);
            tx.Commit();

            Assert.Equal("НОВОЕ", File.ReadAllText(dst));
            Assert.Empty(LeftoverArtifacts(dir.Root));
        }

        /// <summary>
        /// Откат возвращает ИСХОДНОЕ содержимое. Это главное свойство: без него неудачное
        /// обновление оставляет установку в смеси версий.
        /// </summary>
        [Fact]
        public void ОткатВосстанавливаетИсходноеСодержимое() {
            using var dir = new TempDir();
            var a = dir.WriteFile("a.dll", "A-СТАРОЕ");
            var b = dir.WriteFile("b.dll", "B-СТАРОЕ");
            var srcA = dir.WriteFile("src-a", "A-НОВОЕ");
            var srcB = dir.WriteFile("src-b", "B-НОВОЕ");

            var tx = new UpdateTransaction(Log);
            tx.CopyFile(srcA, a);
            tx.CopyFile(srcB, b);
            Assert.Equal("A-НОВОЕ", File.ReadAllText(a));

            tx.Rollback();

            Assert.Equal("A-СТАРОЕ", File.ReadAllText(a));
            Assert.Equal("B-СТАРОЕ", File.ReadAllText(b));
            Assert.Empty(LeftoverArtifacts(dir.Root));
        }

        /// <summary>
        /// Файл, которого до обновления не было, при откате должен ИСЧЕЗНУТЬ, а не остаться
        /// сиротой: иначе следующая сверка увидит лишний файл и предложит его удалить.
        /// </summary>
        [Fact]
        public void ОткатУдаляетСозданныеФайлы() {
            using var dir = new TempDir();
            var src = dir.WriteFile("src-new", "СОДЕРЖИМОЕ");
            var dst = Path.Combine(dir.Root, "sub", "created.dll");

            var tx = new UpdateTransaction(Log);
            tx.CopyFile(src, dst);
            Assert.True(File.Exists(dst));

            tx.Rollback();

            Assert.False(File.Exists(dst), "созданный файл должен быть удалён при откате");
            Assert.Empty(LeftoverArtifacts(dir.Root));
        }

        /// <summary>Откат идёт в обратном порядке и восстанавливает ВСЕ затронутые файлы.</summary>
        [Fact]
        public void ОткатВосстанавливаетВсеФайлыПачки() {
            using var dir = new TempDir();
            var targets = new List<string>();
            for (var i = 0; i < 10; i++) {
                targets.Add(dir.WriteFile($"f{i}.dat", $"СТАРОЕ-{i}"));
            }

            var tx = new UpdateTransaction(Log);
            for (var i = 0; i < targets.Count; i++) {
                var src = dir.WriteFile($"src{i}", $"НОВОЕ-{i}");
                tx.CopyFile(src, targets[i]);
            }

            tx.Rollback();

            for (var i = 0; i < targets.Count; i++) {
                Assert.Equal($"СТАРОЕ-{i}", File.ReadAllText(targets[i]));
            }
        }

        /// <summary>
        /// Сбой на источнике не должен ни портить цель, ни оставлять временный файл:
        /// половина скопированного хуже, чем нетронутый файл.
        /// </summary>
        [Fact]
        public void СбойКопированияНеТрогаетЦель() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "СТАРОЕ");
            var missing = Path.Combine(dir.Root, "нет-такого-файла");

            var tx = new UpdateTransaction(Log);
            Assert.ThrowsAny<Exception>(() => tx.CopyFile(missing, dst));

            Assert.Equal("СТАРОЕ", File.ReadAllText(dst));
            Assert.Equal(0, tx.Count);
            Assert.Empty(LeftoverArtifacts(dir.Root));
        }

        /// <summary>
        /// Хвосты от прерванного прогона (питание пропало посреди подмены) должны
        /// вычищаться на старте, иначе они копятся в папке установки навсегда.
        /// </summary>
        [Fact]
        public void УборкаСноситХвостыПрерванногоПрогона() {
            using var dir = new TempDir();
            dir.WriteFile("app.dll", "живой файл");
            dir.WriteFile("app.dll" + AtomicFile.TempSuffix, "обрывок");
            dir.WriteFile("app.dll" + AtomicFile.BackupSuffix, "старое");
            Directory.CreateDirectory(Path.Combine(dir.Root, "sub"));
            dir.WriteFile(Path.Combine("sub", "b.dll" + AtomicFile.TempSuffix), "обрывок");

            UpdateTransaction.CleanupLeftovers(dir.Root, Log);

            Assert.Empty(LeftoverArtifacts(dir.Root));
            Assert.True(File.Exists(Path.Combine(dir.Root, "app.dll")), "живой файл трогать нельзя");
        }

        /// <summary>Замена несуществующей цели — это простое создание файла.</summary>
        [Fact]
        public void ЗаменаНесуществующейЦелиСоздаётФайл() {
            using var dir = new TempDir();
            var src = dir.WriteFile("src", "СОДЕРЖИМОЕ");
            var dst = Path.Combine(dir.Root, "new.dat");

            AtomicFile.Replace(src, dst, backup: null);

            Assert.Equal("СОДЕРЖИМОЕ", File.ReadAllText(dst));
            Assert.False(File.Exists(src), "источник после замены исчезает");
        }

        /// <summary>Замена с бэкапом сохраняет прежнее содержимое — на этом стоит откат.</summary>
        [Fact]
        public void ЗаменаСБэкапомСохраняетПрежнееСодержимое() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("app.dll", "СТАРОЕ");
            var src = dir.WriteFile("src", "НОВОЕ");
            var backup = dst + AtomicFile.BackupSuffix;

            AtomicFile.Replace(src, dst, backup);

            Assert.Equal("НОВОЕ", File.ReadAllText(dst));
            Assert.True(File.Exists(backup));
            Assert.Equal("СТАРОЕ", File.ReadAllText(backup));
        }

        /// <summary>Файл только для чтения не должен останавливать обновление.</summary>
        [Fact]
        public void ЗаменаСнимаетПризнакТолькоДляЧтения() {
            using var dir = new TempDir();
            var dst = dir.WriteFile("ro.dll", "СТАРОЕ");
            new FileInfo(dst) { IsReadOnly = true }.Refresh();
            var src = dir.WriteFile("src", "НОВОЕ");

            AtomicFile.Replace(src, dst, dst + AtomicFile.BackupSuffix);

            Assert.Equal("НОВОЕ", File.ReadAllText(dst));
        }

        /// <summary>Атомарная запись текста не оставляет временных файлов.</summary>
        [Fact]
        public void АтомарнаяЗаписьНеОставляетВременных() {
            using var dir = new TempDir();
            var path = Path.Combine(dir.Root, "launcher.version");

            AtomicFile.WriteAllText(path, "1.2.3", new UTF8Encoding(false));
            AtomicFile.WriteAllText(path, "1.2.4", new UTF8Encoding(false));

            Assert.Equal("1.2.4", File.ReadAllText(path));
            Assert.Empty(LeftoverArtifacts(dir.Root));
        }

        /// <summary>Исход обновления переживает запись и чтение — по нему лаунчер объясняет неудачу.</summary>
        [Fact]
        public void ИсходОбновленияЗаписываетсяИЧитается() {
            using var dir = new TempDir();
            var status = new UpdateStatus {
                Outcome = "copy-errors",
                ExitCode = 2,
                Version = "1.2.3",
                Message = "файл занят",
            };

            UpdateStatus.Write(dir.Root, status);
            var read = UpdateStatus.TryRead(dir.Root);

            Assert.NotNull(read);
            Assert.Equal("copy-errors", read!.Outcome);
            Assert.Equal(2, read.ExitCode);
            Assert.Equal("1.2.3", read.Version);
            Assert.False(read.IsSuccess);
        }

        /// <summary>Успешный исход опознаётся как успешный.</summary>
        [Fact]
        public void УспешныйИсходОпознаётся() {
            using var dir = new TempDir();
            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "ok", ExitCode = 0 });
            Assert.True(UpdateStatus.TryRead(dir.Root)!.IsSuccess);
        }

        /// <summary>Отсутствие файла состояния — не ошибка: обновления просто не было.</summary>
        [Fact]
        public void ОтсутствиеФайлаСостоянияНеОшибка() {
            using var dir = new TempDir();
            Assert.Null(UpdateStatus.TryRead(dir.Root));
        }

        /// <summary>Битый файл состояния не должен ронять лаунчер при старте.</summary>
        [Fact]
        public void БитыйФайлСостоянияНеРоняет() {
            using var dir = new TempDir();
            File.WriteAllText(UpdateStatus.PathIn(dir.Root), "{ это не json");
            Assert.Null(UpdateStatus.TryRead(dir.Root));
        }

        private static void Log(string message) {
            lock (Sink) {
                Sink.Add(message);
            }
        }

        /// <summary>Временные файлы и бэкапы, оставшиеся в дереве.</summary>
        private static List<string> LeftoverArtifacts(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(AtomicFile.TempSuffix, StringComparison.OrdinalIgnoreCase)
                              || p.EndsWith(AtomicFile.BackupSuffix, StringComparison.OrdinalIgnoreCase))
                     .ToList();
    }
}
