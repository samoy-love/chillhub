// <copyright file="GameLocalStateShortcutRemovalTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Удаление ярлыка вместе с игрой.
    /// <para>
    /// Ярлык переживал удаление игры: файлы снесены, а иконка на рабочем столе осталась
    /// и по клику ругалась «не найден элемент». При этом рабочий стол — чужая
    /// территория: удалять оттуда можно только то, что действительно ведёт в папку
    /// удалённой игры, и ни файлом больше.
    /// </para>
    /// </summary>
    public class GameLocalStateShortcutRemovalTests {
        /// <summary>Ярлык, ведущий в папку игры, уходит вместе с ней.</summary>
        [Fact]
        public void ЯрлыкИгрыУдаляетсяВместеСИгрой() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var root = Path.Combine(games.Root, "lethal-company");
            var link = WriteFakeLink(desktop, "Lethal Company.lnk", Path.Combine(root, "game.exe"));

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                Assert.Equal(1, GameLocalState.TryRemoveDesktopShortcuts(root));
            }

            Assert.False(File.Exists(link));
        }

        /// <summary>
        /// Ярлыков может быть несколько: пользователь копировал иконку, а установка
        /// после переименования игры клала вторую под новым названием.
        /// </summary>
        [Fact]
        public void УдаляютсяВсеЯрлыкиИгрыСразу() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var root = Path.Combine(games.Root, "gid");
            var first = WriteFakeLink(desktop, "Игра.lnk", Path.Combine(root, "game.exe"));
            var second = WriteFakeLink(desktop, "Игра (копия).lnk", Path.Combine(root, "bin", "game.exe"));

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                Assert.Equal(2, GameLocalState.TryRemoveDesktopShortcuts(root));
            }

            Assert.False(File.Exists(first));
            Assert.False(File.Exists(second));
        }

        /// <summary>
        /// Ярлык опознаётся по цели, а не по названию.
        /// <para>
        /// Название игры на сервере могло поменяться после установки, а на рабочем столе
        /// у пользователя вполне лежит чужой ярлык с таким же именем. Стереть чужое
        /// хуже, чем оставить лишнее.
        /// </para>
        /// </summary>
        [Fact]
        public void ЧужойЯрлыкСТемЖеИменемОстаётсяНаМесте() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var root = Path.Combine(games.Root, "gid");
            var alien = WriteFakeLink(desktop, "Игра.lnk", Path.Combine(games.Root, "другая-игра", "game.exe"));

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                Assert.Equal(0, GameLocalState.TryRemoveDesktopShortcuts(root));
            }

            Assert.True(File.Exists(alien));
        }

        /// <summary>
        /// Папка игры соседа не задевается: `…\game` не должен считаться началом
        /// `…\game-2`, иначе удаление одной игры уносило бы ярлык другой.
        /// </summary>
        [Fact]
        public void ЯрлыкСоседнейПапкиСПохожимИменемОстаётся() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var root = Path.Combine(games.Root, "game");
            var neighbour = WriteFakeLink(desktop, "Игра 2.lnk", Path.Combine(games.Root, "game-2", "game.exe"));

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                Assert.Equal(0, GameLocalState.TryRemoveDesktopShortcuts(root));
            }

            Assert.True(File.Exists(neighbour));
        }

        /// <summary>
        /// Регистр в записанном пути не мешает опознать игру: Windows пути регистром
        /// не различает, а ярлык мог быть создан из строки другого регистра.
        /// </summary>
        [Fact]
        public void РегистрПутиВЯрлыкеНеМешает() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var root = Path.Combine(games.Root, "Game");
            var link = WriteFakeLink(desktop, "Игра.lnk", Path.Combine(games.Root, "GAME", "game.exe"));

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                Assert.Equal(1, GameLocalState.TryRemoveDesktopShortcuts(root));
            }

            Assert.False(File.Exists(link));
        }

        /// <summary>Не-ярлыки на рабочем столе не трогаем, даже если внутри упомянута папка игры.</summary>
        [Fact]
        public void ПостороннийФайлНаРабочемСтолеНеУдаляется() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var root = Path.Combine(games.Root, "gid");
            var note = desktop.WriteFile("заметка.txt", Path.Combine(root, "game.exe"));

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                Assert.Equal(0, GameLocalState.TryRemoveDesktopShortcuts(root));
            }

            Assert.True(File.Exists(note));
        }

        /// <summary>
        /// Пропавший рабочий стол не роняет удаление: он может быть перенаправлен
        /// на отвалившийся сетевой диск, а файлы игры к этому моменту уже снесены.
        /// </summary>
        [Fact]
        public void НедоступныйРабочийСтолНеРоняетУдаление() {
            using var games = new TempDir();

            using (GameLocalState.OverrideShortcutEnvironmentForTests(Path.Combine(games.Root, "нет-такого-стола"))) {
                Assert.Equal(0, GameLocalState.TryRemoveDesktopShortcuts(Path.Combine(games.Root, "gid")));
            }
        }

        /// <summary>Пустой путь к игре — не повод обходить рабочий стол.</summary>
        /// <param name="localRoot">Корень папки игры.</param>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойПутьКИгреНичегоНеУдаляет(string localRoot) {
            using var desktop = new TempDir();
            var link = WriteFakeLink(desktop, "Игра.lnk", Path.Combine(desktop.Root, "game.exe"));

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                Assert.Equal(0, GameLocalState.TryRemoveDesktopShortcuts(localRoot));
            }

            Assert.True(File.Exists(link));
        }

        /// <summary>
        /// Настоящий ярлык, созданный оболочкой Windows, тоже опознаётся: разбор
        /// `.lnk` идёт по байтам, и проверять его на самодельных файлах мало.
        /// </summary>
        [Fact]
        public void НастоящийЯрлыкОболочкиТожеУдаляется() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var exe = games.WriteFile("gid/game.exe", "MZ");
            var root = Path.GetDirectoryName(exe)!;

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", exe);
                if (!File.Exists(Path.Combine(desktop.Root, "Игра.lnk"))) {
                    // Оболочка ярлыков не создаёт (политика, урезанная система, агент сборки) —
                    // удалять нечего, и это не дефект проверяемого кода.
                    return;
                }

                Assert.Equal(1, GameLocalState.TryRemoveDesktopShortcuts(root));
            }

            Assert.Empty(Directory.GetFiles(desktop.Root, "*.lnk"));
        }

        /// <summary>
        /// Кладёт на «рабочий стол» файл ярлыка с путём к цели внутри.
        /// <para>
        /// Настоящий `.lnk` создаёт только оболочка Windows, которой на сборочном агенте
        /// может не быть. Здесь важен не формат, а то, как путь лежит в файле: обе записи —
        /// однобайтовая и UTF-16 — те же самые, что пишет оболочка.
        /// </para>
        /// </summary>
        /// <param name="desktop">Каталог, играющий роль рабочего стола.</param>
        /// <param name="fileName">Имя файла ярлыка.</param>
        /// <param name="targetPath">Путь к цели ярлыка.</param>
        /// <returns>Полный путь к созданному файлу.</returns>
        private static string WriteFakeLink(TempDir desktop, string fileName, string targetPath) {
            var bytes = new List<byte>();
            bytes.AddRange(new byte[] { 0x4C, 0x00, 0x00, 0x00 }); // сигнатура заголовка ярлыка
            bytes.AddRange(Encoding.UTF8.GetBytes(targetPath));
            bytes.Add(0);
            bytes.AddRange(Encoding.Unicode.GetBytes(targetPath));
            return desktop.WriteBytes(fileName, bytes.ToArray());
        }
    }
}
