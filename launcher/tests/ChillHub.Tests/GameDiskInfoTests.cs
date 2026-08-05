// <copyright file="GameDiskInfoTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.IO;

    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Размер папки игры в сводке на странице игры.
    /// <para>
    /// Цифра рядом со свободным местом — то, по чему пользователь решает, влезет ли
    /// сборка. Обход папки идёт по живой файловой системе: файл могли удалить прямо
    /// во время подсчёта, а до самой папки может не быть доступа. Ни то, ни другое не
    /// имеет права уронить открытие страницы, поэтому нижняя граница здесь — ноль,
    /// а не исключение.
    /// </para>
    /// </summary>
    public class GameDiskInfoTests {
        /// <summary>Размер считается по всей папке, включая вложенные каталоги.</summary>
        [Fact]
        public void РазмерСчитаетсяПоВсемВложеннымПапкам() {
            using var dir = new TempDir();
            dir.WriteBytes("a.pak", new byte[100]);
            dir.WriteBytes("data/b.pak", new byte[50]);
            dir.WriteBytes("data/deep/c.pak", new byte[7]);

            Assert.Equal(157, GameDiskInfo.GetDirectorySize(dir.Root));
        }

        /// <summary>Пустая папка — ноль, а не «—» и не исключение.</summary>
        [Fact]
        public void ПустаяПапкаДаётНоль() {
            using var dir = new TempDir();

            Assert.Equal(0, GameDiskInfo.GetDirectorySize(dir.Root));
        }

        /// <summary>Игра не установлена: папки нет, размер ноль.</summary>
        [Fact]
        public void ОтсутствующаяПапкаДаётНоль() {
            using var dir = new TempDir();

            Assert.Equal(0, GameDiskInfo.GetDirectorySize(Path.Combine(dir.Root, "нет-такой-папки")));
        }

        /// <summary>Пустой путь не роняет сводку: игра могла не иметь идентификатора.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойПутьДаётНоль(string root) {
            Assert.Equal(0, GameDiskInfo.GetDirectorySize(root));
        }
    }
}
