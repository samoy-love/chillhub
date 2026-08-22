// <copyright file="GameLocalStateShortcutCreationTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Ярлык в конце установки: путь к exe и запуск создания.
    /// <para>
    /// Установка заканчивается в потоке пула, а оболочка Windows — COM и требует STA,
    /// поэтому создание ярлыка уходит в отдельный поток. Проверяем и склейку пути к exe
    /// (её раньше собирал вручную обработчик страницы), и то, что ярлык действительно
    /// доезжает до рабочего стола.
    /// </para>
    /// </summary>
    public class GameLocalStateShortcutCreationTests {
        /// <summary>Сколько ждём фоновый поток ярлыка, прежде чем считать, что он не справился.</summary>
        private static readonly TimeSpan ShortcutWait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Разделитель в пути к exe приходит с сервера любой: карточка игры заполняется
        /// руками в админке, и `bin/game.exe` там не реже, чем `bin\game.exe`.
        /// </summary>
        /// <param name="relative">Путь к exe относительно папки игры.</param>
        [Theory]
        [InlineData("bin/game.exe")]
        [InlineData(@"bin\game.exe")]
        public void ПутьКExeСклеиваетсяСПапкойИгрыПриЛюбомРазделителе(string relative) {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            ConfigService.Current.GamesPath = games.Root;

            var expected = Path.Combine(GameLocalState.GameLocalRoot("gid"), "bin", "game.exe");
            Assert.Equal(expected, GameLocalState.GameExePath("gid", relative));
        }

        /// <summary>
        /// Ведущий разделитель не уводит ярлык из папки игры: `Path.Combine` считает такой
        /// путь абсолютным и выкинул бы папку игры целиком, оставив цель в корне диска.
        /// </summary>
        [Fact]
        public void ВедущийРазделительНеУводитИзПапкиИгры() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            ConfigService.Current.GamesPath = games.Root;

            var root = GameLocalState.GameLocalRoot("gid");
            Assert.Equal(Path.Combine(root, "game.exe"), GameLocalState.GameExePath("gid", @"\game.exe"));
        }

        /// <summary>Не задан путь к exe или игра — запускать нечего, и путь пустой.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="relative">Путь к exe относительно папки игры.</param>
        [Theory]
        [InlineData("", "game.exe")]
        [InlineData("gid", "")]
        [InlineData("gid", "   ")]
        public void БезИгрыИлиПутиКExeПутьПустой(string gameId, string relative)
            => Assert.Equal(string.Empty, GameLocalState.GameExePath(gameId, relative));

        /// <summary>
        /// Установка кладёт ярлык на рабочий стол. Ради этого вся связка и существует:
        /// вызов после установки пропал при рефакторинге, и ярлыки перестали появляться.
        /// </summary>
        [Fact]
        public void ПослеУстановкиЯрлыкПоявляетсяНаРабочемСтоле() {
            using var scope = new ConfigDirsScope();
            using var desktop = new TempDir();
            using var games = new TempDir();
            ConfigService.Current.GamesPath = games.Root;

            var root = GameLocalState.GameLocalRoot("gid");
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "game.exe"), "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.StartDesktopShortcutCreation("Игра", "gid", "bin/game.exe");

                if (!GameLocalStateShortcutTests.ShellAvailable) {
                    // Оболочка ярлыков не создаёт (политика, урезанная система, агент сборки) —
                    // проверять нечего, но и упасть здесь ничего не должно.
                    return;
                }

                Assert.True(
                    WaitForFile(Path.Combine(desktop.Root, "Игра.lnk")),
                    $"ярлык не создан; в каталоге: {string.Join(", ", Directory.GetFiles(desktop.Root))}");
            }
        }

        /// <summary>
        /// Без названия ярлык берёт имя игры: безымянный ярлык пользователь не найдёт,
        /// а название в карточке заполнено не всегда.
        /// </summary>
        [Fact]
        public void БезНазванияЯрлыкБерётИдентификаторИгры() {
            using var scope = new ConfigDirsScope();
            using var desktop = new TempDir();
            using var games = new TempDir();
            ConfigService.Current.GamesPath = games.Root;

            var root = GameLocalState.GameLocalRoot("lethal-company");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "game.exe"), "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.StartDesktopShortcutCreation(null, "lethal-company", "game.exe");

                if (!GameLocalStateShortcutTests.ShellAvailable) {
                    return;
                }

                Assert.True(WaitForFile(Path.Combine(desktop.Root, "lethal-company.lnk")));
            }
        }

        /// <summary>
        /// Установка без exe на диске ярлык не создаёт и не падает: путь к exe в карточке
        /// может быть указан неверно, а установка обязана дойти до конца.
        /// </summary>
        [Fact]
        public void БезФайлаНаДискеЯрлыкНеСоздаётся() {
            using var scope = new ConfigDirsScope();
            using var desktop = new TempDir();
            using var games = new TempDir();
            ConfigService.Current.GamesPath = games.Root;

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.StartDesktopShortcutCreation("Игра", "gid", "нет-такого.exe");
                GameLocalState.StartDesktopShortcutCreation("Игра", null, "game.exe");
                GameLocalState.StartDesktopShortcutCreation("Игра", "gid", null);
            }

            Assert.Empty(Directory.GetFiles(desktop.Root));
        }

        /// <summary>Ждёт появления файла: ярлык создаётся в фоновом потоке.</summary>
        /// <param name="path">Путь к ожидаемому файлу.</param>
        /// <returns>true, если файл появился до истечения ожидания.</returns>
        private static bool WaitForFile(string path) {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < ShortcutWait) {
                if (File.Exists(path)) {
                    return true;
                }

                Thread.Sleep(50);
            }

            return File.Exists(path);
        }
    }
}
