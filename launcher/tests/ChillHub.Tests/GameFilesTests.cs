// <copyright file="GameFilesTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Удаление локальных файлов игры.
    /// <para>
    /// Обычный <c>Directory.Delete(recursive)</c> обрывается на первом занятом файле, когда
    /// остальное уже снесено: пользователь видел «не удалось удалить», игра числилась
    /// установленной, а на диске лежали её нерабочие остатки. Проход обязан доходить
    /// до конца и честно называть то, что удалить не вышло.
    /// </para>
    /// </summary>
    public class GameFilesTests {
        /// <summary>Папка игры сносится целиком, вместе с вложенными каталогами.</summary>
        [Fact]
        public void ПапкаИгрыСносится() {
            using var dir = new TempDir();
            dir.WriteFile("game/game.exe", "MZ");
            dir.WriteFile("game/data/assets/pack.bin", "данные");
            var root = dir.PathTo("game");

            var blocked = GameFiles.DeleteGameFiles(root);

            Assert.Empty(blocked);
            Assert.False(Directory.Exists(root));
        }

        /// <summary>Файл «только для чтения» тоже удаляется: игры кладут такие в свои папки.</summary>
        [Fact]
        public void ФайлТолькоДляЧтенияУдаляется() {
            using var dir = new TempDir();
            var file = dir.WriteFile("game/readonly.dat", "данные");
            File.SetAttributes(file, FileAttributes.ReadOnly);

            var blocked = GameFiles.DeleteGameFiles(dir.PathTo("game"));

            Assert.Empty(blocked);
            Assert.False(File.Exists(file));
        }

        /// <summary>
        /// Занятый файл не прерывает проход: остальное удаляется, а он попадает в список.
        /// Именно ради этого удаление написано вручную, а не одной строкой.
        /// </summary>
        [Fact]
        public void ЗанятыйФайлНеПрерываетУдаление() {
            using var dir = new TempDir();
            dir.WriteFile("game/free.dat", "данные");
            var locked = dir.WriteFile("game/locked.dat", "данные");

            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
                var blocked = GameFiles.DeleteGameFiles(dir.PathTo("game"));

                Assert.Single(blocked);
                Assert.Equal(locked, blocked[0]);
                Assert.False(File.Exists(dir.PathTo("game/free.dat")));
            }
        }

        /// <summary>
        /// Корень остаётся на месте, пока в нём есть занятые файлы: удалять папку,
        /// часть содержимого которой цела, нельзя.
        /// </summary>
        [Fact]
        public void КореньОстаётсяПокаЕстьЗанятыеФайлы() {
            using var dir = new TempDir();
            var locked = dir.WriteFile("game/locked.dat", "данные");

            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
                GameFiles.DeleteGameFiles(dir.PathTo("game"));

                Assert.True(Directory.Exists(dir.PathTo("game")));
            }
        }

        /// <summary>Папки игры и нет — это не ошибка: удалять просто нечего.</summary>
        [Fact]
        public void ОтсутствующаяПапкаНеОшибка() {
            using var dir = new TempDir();

            Assert.Empty(GameFiles.DeleteGameFiles(dir.PathTo("никогда-не-ставили")));
        }

        /// <summary>
        /// Пользователю называют занятые файлы поимённо: без имён ему нечего закрывать,
        /// а до этого игра работать не будет.
        /// </summary>
        [Fact]
        public void ЗанятыеФайлыНазываютсяПоимённо() {
            var text = GameFiles.BuildBlockedFilesMessage(new List<string> { @"C:\games\a.dat", @"C:\games\b.dat" });

            Assert.Contains("2 шт.", text);
            Assert.Contains("a.dat", text);
            Assert.Contains("b.dat", text);
            Assert.DoesNotContain("и ещё", text);
        }

        /// <summary>Длинный список сворачивается: три имени и «и ещё N», иначе сообщение не прочитать.</summary>
        [Fact]
        public void ДлинныйСписокСворачивается() {
            var blocked = new List<string>();
            for (var i = 0; i < 7; i++) {
                blocked.Add($@"C:\games\file{i}.dat");
            }

            var text = GameFiles.BuildBlockedFilesMessage(blocked);

            Assert.Contains("7 шт.", text);
            Assert.Contains("file0.dat", text);
            Assert.Contains("file2.dat", text);
            Assert.DoesNotContain("file3.dat", text);
            Assert.Contains("и ещё 4", text);
        }

        /// <summary>В сообщении есть путь к действию: пользователь должен знать, что делать дальше.</summary>
        [Fact]
        public void СообщениеОбъясняетЧтоДелать() {
            var text = GameFiles.BuildBlockedFilesMessage(new List<string> { @"C:\games\a.dat" });

            Assert.Contains("Закройте игру", text);
            Assert.Contains("удалите ещё раз", text);
        }
    }
}
