// <copyright file="InstallFingerprintTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Слепок папки игры — то, чем проверка статуса отвечает «файлы не трогали», не
    /// строя полный план различий.
    /// <para>
    /// Цена ошибки несимметрична. Ложное «совпало» означает, что лаунчер покажет
    /// «Играть» над испорченной сборкой; ложное «разошлось» стоит всего лишь длинного
    /// пути, то есть того, что было раньше. Поэтому проверяется в первую очередь, что
    /// слепок расходится от любой практической порчи.
    /// </para>
    /// </summary>
    public class InstallFingerprintTests {
        /// <summary>Нетронутая папка совпадает сама с собой.</summary>
        [Fact]
        public void НетронутаяПапкаСовпадает() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "MZ");
            dir.WriteFile("data/pack.bin", "данные");

            Assert.True(InstallFingerprint.Save(dir.Root));
            Assert.True(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>Удалённый файл расходится по счётчику.</summary>
        [Fact]
        public void УдалённыйФайлРасходится() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "MZ");
            dir.WriteFile("data/pack.bin", "данные");
            InstallFingerprint.Save(dir.Root);

            File.Delete(Path.Combine(dir.Root, "data", "pack.bin"));

            Assert.False(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>Добавленный файл — тоже расхождение: сборка больше не та, что проверяли.</summary>
        [Fact]
        public void ДобавленныйФайлРасходится() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "MZ");
            InstallFingerprint.Save(dir.Root);

            dir.WriteFile("data/extra.bin", "лишнее");

            Assert.False(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>Подменённое содержимое расходится по размеру.</summary>
        [Fact]
        public void ПодменённоеСодержимоеРасходится() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "MZ");
            InstallFingerprint.Save(dir.Root);

            dir.WriteFile("game.exe", "MZ и ещё немного");

            Assert.False(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>
        /// Служебные файлы лаунчера в слепок не входят: иначе запись маркера версии или
        /// самого слепка тут же расходилась бы с тем, что слепок только что зафиксировал.
        /// </summary>
        [Fact]
        public void СлужебныеФайлыВСлепокНеВходят() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "MZ");
            InstallFingerprint.Save(dir.Root);

            dir.WriteFile(IntegrityChecker.VersionMarkerFileName, "1.0.1");
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.1");
            dir.WriteFile(".staging/partial.bin", "недокачано");

            Assert.True(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>
        /// Слепка нет — это НЕ совпадение. У игры, поставленной до его появления, файла
        /// просто не будет, и такую надо проверять полным путём, а не объявлять целой.
        /// </summary>
        [Fact]
        public void БезСлепкаСовпаденияНет() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "MZ");

            Assert.Null(InstallFingerprint.Read(dir.Root));
            Assert.False(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>Пустую папку запоминать нечего: игры в ней нет.</summary>
        [Fact]
        public void ПустаяПапкаСлепкаНеПолучает() {
            using var dir = new TempDir();

            Assert.False(InstallFingerprint.Save(dir.Root));
            Assert.False(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>Битый файл слепка равнозначен его отсутствию, а не падению.</summary>
        [Fact]
        public void БитыйСлепокРавнозначенЕгоОтсутствию() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "MZ");
            dir.WriteFile(InstallFingerprint.FileName, "{это не json");

            Assert.Null(InstallFingerprint.Read(dir.Root));
            Assert.False(InstallFingerprint.Matches(dir.Root));
        }

        /// <summary>Несуществующая папка ничего не ломает.</summary>
        [Fact]
        public void ОтсутствующаяПапкаНеРоняет() {
            Assert.False(InstallFingerprint.Save(Path.Combine(Path.GetTempPath(), "chillhub-нет-" + Guid.NewGuid().ToString("N"))));
            Assert.False(InstallFingerprint.Matches(null));
            Assert.Null(InstallFingerprint.Read(string.Empty));
            Assert.Equal(0, InstallFingerprint.Compute(null).Files);
        }
    }
}
