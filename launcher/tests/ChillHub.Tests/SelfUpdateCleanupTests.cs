// <copyright file="SelfUpdateCleanupTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Уборка следов самообновления.
    /// <para>
    /// Каждое обновление оставляет в %TEMP% копию пакета — это гигабайты, которые
    /// раньше не удалялись никогда. Но убирать надо ОСТОРОЖНО: в самом свежем
    /// каталоге апдейтер может ещё дописывать журнал, а снесённый каталог занятой
    /// сессии — это оборванное обновление.
    /// </para>
    /// </summary>
    public class SelfUpdateCleanupTests {
        /// <summary>Старые сессии удаляются, две самых свежих остаются.</summary>
        [Fact]
        public void СтарыеСессииУдаляютсяАДвеСвежихОстаются() {
            using var root = new TempDir();
            var old1 = Session(root, "1.0.0", DateTime.UtcNow.AddDays(-3));
            var old2 = Session(root, "1.1.0", DateTime.UtcNow.AddDays(-2));
            var fresh1 = Session(root, "1.2.0", DateTime.UtcNow.AddHours(-2));
            var fresh2 = Session(root, "1.3.0", DateTime.UtcNow);

            SelfUpdateCleanup.TryCleanupTempSelfUpdateDirs(root.Root);

            Assert.True(Directory.Exists(fresh1));
            Assert.True(Directory.Exists(fresh2));
            Assert.False(Directory.Exists(old1));
            Assert.False(Directory.Exists(old2));
        }

        /// <summary>
        /// Из оставленных сессий копия апдейтера всё равно выносится: держать в %TEMP%
        /// исполняемые файлы дольше, чем нужно, незачем. Проверяются обе раскладки —
        /// старая (updater в корне версии) и новая (work\updater).
        /// </summary>
        [Fact]
        public void КопияАпдейтераВыноситсяДажеИзСвежейСессии() {
            using var root = new TempDir();
            var fresh = Session(root, "1.3.0", DateTime.UtcNow);
            var oldLayout = Path.Combine(fresh, PreserveMatcher.UpdaterArtifactDir);
            var newLayout = Path.Combine(fresh, "work", PreserveMatcher.UpdaterArtifactDir);
            Directory.CreateDirectory(oldLayout);
            Directory.CreateDirectory(newLayout);
            File.WriteAllText(Path.Combine(newLayout, "ChillHub.Updater.exe"), "apphost");

            SelfUpdateCleanup.TryCleanupTempSelfUpdateDirs(root.Root);

            Assert.True(Directory.Exists(fresh));
            Assert.False(Directory.Exists(oldLayout));
            Assert.False(Directory.Exists(newLayout));
        }

        /// <summary>
        /// A14. Свежесть по позиции в списке недостаточна: у пользователя, который давно
        /// не обновлялся, пара каталогов-ветеранов лежала бы в %TEMP% вечно.
        /// </summary>
        [Fact]
        public void ДавнийКаталогУдаляетсяДажеБудучиСамымСвежим() {
            using var root = new TempDir();
            var veteran = Session(root, "1.0.0", DateTime.UtcNow.AddDays(-SelfUpdateCleanup.StaleSessionDays - 1));

            SelfUpdateCleanup.TryCleanupTempSelfUpdateDirs(root.Root);

            Assert.False(Directory.Exists(veteran));
        }

        /// <summary>Хвосты прошлых уборок (*.trash-*) добиваются, а не копятся.</summary>
        [Fact]
        public void ХвостыПрошлыхУборокДобиваются() {
            using var root = new TempDir();
            var trash = Path.Combine(root.Root, "1.0.0" + SelfUpdateCleanup.TrashSuffix + "deadbeef");
            Directory.CreateDirectory(trash);
            File.WriteAllText(Path.Combine(trash, "payload.bin"), "остатки");

            SelfUpdateCleanup.TryCleanupTempSelfUpdateDirs(root.Root);

            Assert.False(Directory.Exists(trash));
        }

        /// <summary>Несуществующий корень уборку не роняет: она не должна мешать запуску.</summary>
        [Fact]
        public void ОтсутствующийКореньУборкуНеРоняет() {
            using var root = new TempDir();
            SelfUpdateCleanup.TryCleanupTempSelfUpdateDirs(Path.Combine(root.Root, "нет-такого"));
        }

        /// <summary>Файл с атрибутом «только чтение» уборке не помеха.</summary>
        [Fact]
        public void ФайлТолькоДляЧтенияУдаляется() {
            using var root = new TempDir();
            var dir = Path.Combine(root.Root, "сессия");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "readonly.bin");
            File.WriteAllText(file, "данные");
            File.SetAttributes(file, FileAttributes.ReadOnly);

            SelfUpdateCleanup.TryDeleteDirectoryBestEffort(dir);

            Assert.False(Directory.Exists(dir));
        }

        /// <summary>
        /// Занятый файл уборку не роняет и сам не портится: в самой свежей сессии
        /// апдейтер может ещё дописывать журнал, и его данные важнее места на диске.
        /// </summary>
        [Fact]
        public void ЗанятыйФайлПереживаетУборку() {
            using var root = new TempDir();
            var dir = Path.Combine(root.Root, "1.2.4");
            Directory.CreateDirectory(dir);
            var busyPath = Path.Combine(dir, "busy.bin");
            using (var busy = new FileStream(busyPath, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                busy.Write(new byte[] { 1, 2, 3 }, 0, 3);
                busy.Flush();

                SelfUpdateCleanup.TryDeleteDirectoryBestEffort(dir);

                Assert.Equal(3, new FileInfo(busyPath).Length);
            }
        }

        /// <summary>
        /// A14. Каталог, который не удалился из-за занятого файла, добивается при
        /// следующем запуске — иначе он оставался бы в %TEMP% навсегда, а следующая
        /// сессия той же версии создавалась бы поверх чужих остатков.
        /// </summary>
        [Fact]
        public void КаталогДобиваетсяКогдаФайлОсвободился() {
            using var root = new TempDir();
            var dir = Path.Combine(root.Root, "1.2.4");
            Directory.CreateDirectory(dir);
            using (var busy = new FileStream(Path.Combine(dir, "busy.bin"), FileMode.Create, FileAccess.Write, FileShare.Read)) {
                SelfUpdateCleanup.TryDeleteDirectoryBestEffort(dir);
            }

            SelfUpdateCleanup.TryDeleteDirectoryBestEffort(dir);

            Assert.False(Directory.Exists(dir));
        }

        /// <summary>
        /// A6. Служебные файлы апдейтера, которые старые версии «зеркалили» в папку
        /// установки, вычищаются: в пакет лаунчера они не входят и сверку целостности
        /// только путают.
        /// </summary>
        [Fact]
        public void СлужебныйМусорАпдейтераВычищаетсяИзУстановки() {
            using var install = new TempDir();
            foreach (var name in PreserveMatcher.UpdaterArtifactFiles) {
                install.WriteFile(name, "мусор");
            }

            install.WriteFile(PreserveMatcher.UpdaterArtifactDir + "/ChillHub.Updater.exe", "apphost");
            install.WriteFile("ChillHub.exe", "лаунчер");

            SelfUpdateCleanup.TryCleanupInstalledUpdaterArtifacts(install.Root);

            foreach (var name in PreserveMatcher.UpdaterArtifactFiles) {
                Assert.False(File.Exists(install.PathTo(name)));
            }

            Assert.False(Directory.Exists(install.PathTo(PreserveMatcher.UpdaterArtifactDir)));

            // Файлы самого лаунчера уборка не трогает.
            Assert.True(File.Exists(install.PathTo("ChillHub.exe")));
        }

        private static string Session(TempDir root, string version, DateTime stampUtc) {
            var dir = Path.Combine(root.Root, version);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "payload.bin"), version);
            Directory.SetLastWriteTimeUtc(dir, stampUtc);
            Directory.SetCreationTimeUtc(dir, stampUtc);
            return dir;
        }
    }

    /// <summary>
    /// A12. Показ исхода ПРОШЛОГО обновления.
    /// <para>
    /// Апдейтер завершается уже после перезапуска лаунчера, поэтому его код возврата
    /// не читает никто. Без этого сообщения неудавшееся обновление выглядит как
    /// «ничего не произошло», и пользователь жмёт «Обновить» снова и снова.
    /// </para>
    /// </summary>
    public class PreviousUpdateOutcomeTests {
        /// <summary>Файла нет — говорить не о чем.</summary>
        [Fact]
        public void БезФайлаСостоянияСообщенияНет() {
            using var dir = new TempDir();
            Assert.Null(PreviousUpdateOutcome.Describe(dir.Root));
        }

        /// <summary>Успешное обновление пользователя не беспокоит, но файл снимается.</summary>
        [Fact]
        public void УспешноеОбновлениеНичегоНеПоказывает() {
            using var dir = new TempDir();
            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "ok", Version = "1.2.4" });

            Assert.Null(PreviousUpdateOutcome.Describe(dir.Root));
            Assert.Null(UpdateStatus.TryRead(dir.Root));
        }

        /// <summary>Провал показывается вместе с причиной и путём к журналу.</summary>
        [Fact]
        public void ПровалПоказываетсяСПричинойИЖурналом() {
            using var dir = new TempDir();
            UpdateStatus.Write(dir.Root, new UpdateStatus {
                Outcome = "copy-errors",
                ExitCode = 2,
                Version = "1.2.4",
                Message = "не удалось заменить ChillHub.dll",
                LogPath = @"C:\Temp\apply-update.log",
            });

            var text = PreviousUpdateOutcome.Describe(dir.Root);

            Assert.NotNull(text);
            Assert.Contains("Предыдущее обновление не было применено", text, StringComparison.Ordinal);
            Assert.Contains("не удалось заменить ChillHub.dll", text, StringComparison.Ordinal);
            Assert.Contains(@"C:\Temp\apply-update.log", text, StringComparison.Ordinal);
        }

        /// <summary>Без пояснения показывается хотя бы код исхода — молчать нельзя.</summary>
        [Fact]
        public void БезПоясненияПоказываетсяКодИсхода() {
            using var dir = new TempDir();
            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "fatal", ExitCode = 3 });

            var text = PreviousUpdateOutcome.Describe(dir.Root);

            Assert.NotNull(text);
            Assert.Contains("fatal", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Сообщение показывается ОДИН раз: файл перезапишет следующий запуск апдейтера,
        /// иначе старая ошибка висела бы в окне после успешного обновления.
        /// </summary>
        [Fact]
        public void СообщениеПоказываетсяОдинРаз() {
            using var dir = new TempDir();
            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "fatal", Message = "диск отвалился" });

            Assert.NotNull(PreviousUpdateOutcome.Describe(dir.Root));
            Assert.Null(PreviousUpdateOutcome.Describe(dir.Root));
        }
    }
}
