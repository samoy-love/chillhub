// <copyright file="GameLocalStateShortcutTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Ярлык игры на рабочем столе.
    /// <para>
    /// Ярлык — приятная мелочь в конце установки, а не её часть: игра уже распакована и
    /// готова к запуску. Поэтому единственное жёсткое требование — что бы ни случилось
    /// с оболочкой Windows, именем игры или самим exe, установка обязана дойти до конца.
    /// Исключение отсюда превращало бы успешную закачку десятков гигабайт в «ошибку
    /// установки».
    /// </para>
    /// <para>
    /// Все тесты уводят каталог ярлыков в подставной: настоящий рабочий стол
    /// пользователя прогон тестов засорять не имеет права.
    /// </para>
    /// </summary>
    public class GameLocalStateShortcutTests {
        /// <summary>Несуществующий exe — не повод создавать ярлык в никуда.</summary>
        [Fact]
        public void ЯрлыкНеСоздаётсяДляНесуществующегоExe() {
            using var desktop = new TempDir();
            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", Path.Combine(desktop.Root, "нет-такого.exe"));

                Assert.Empty(Directory.GetFiles(desktop.Root));
            }
        }

        /// <summary>
        /// Пустой путь к exe — это «запускать нечем»: такой ярлык только вводил бы
        /// пользователя в заблуждение.
        /// </summary>
        /// <param name="exePath">Путь к исполняемому файлу.</param>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ЯрлыкНеСоздаётсяДляПустогоПути(string exePath) {
            using var desktop = new TempDir();
            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", exePath);

                Assert.Empty(Directory.GetFiles(desktop.Root));
            }
        }

        /// <summary>
        /// Недоступная оболочка Windows не роняет установку.
        /// <para>
        /// WScript.Shell есть не везде: политика может запретить скриптовый хост, а на
        /// урезанных сборках Windows его просто нет. Раньше это был бы отказ установки
        /// на ровном месте — игра установлена, но пользователю показана ошибка.
        /// </para>
        /// </summary>
        [Fact]
        public void НедоступнаяОболочкаНеРоняетУстановку() {
            using var desktop = new TempDir();
            var exe = desktop.WriteFile("game.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root, "ChillHub.НетТакогоProgID")) {
                GameLocalState.TryCreateDesktopShortcut("Игра", exe);
            }

            Assert.False(File.Exists(Path.Combine(desktop.Root, "Игра.lnk")));
        }

        /// <summary>
        /// Запрещённые в имени файла символы не роняют создание ярлыка.
        /// <para>
        /// Названия игр приходят с сервера и содержат что угодно — двоеточия
        /// («Half-Life: Alyx»), слеши, звёздочки. Без очистки <c>Path.Combine</c> собрал бы
        /// недопустимый путь, и установка падала бы на играх с «неудобным» названием.
        /// </para>
        /// </summary>
        /// <param name="title">Название игры.</param>
        /// <param name="expectedFile">Ожидаемое имя файла ярлыка.</param>
        [Theory]
        [InlineData("Half-Life: Alyx", "Half-Life_ Alyx.lnk")]
        [InlineData("Игра/2", "Игра_2.lnk")]
        [InlineData(@"Игра\3", "Игра_3.lnk")]
        [InlineData("Игра?*|", "Игра___.lnk")]
        public void ИмяЯрлыкаОчищаетсяОтЗапрещённыхСимволов(string title, string expectedFile) {
            using var desktop = new TempDir();
            var exe = desktop.WriteFile("game.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.TryCreateDesktopShortcut(title, exe);
            }

            if (!ShellAvailable) {
                // Оболочки нет — проверять нечего, но упасть тест не должен был и здесь.
                return;
            }

            Assert.True(
                File.Exists(Path.Combine(desktop.Root, expectedFile)),
                $"ярлык '{expectedFile}' не создан; в каталоге: {string.Join(", ", Directory.GetFiles(desktop.Root))}");
        }

        /// <summary>
        /// Без названия ярлык называется по имени exe: безымянный ярлык на рабочем столе
        /// пользователь просто не найдёт.
        /// </summary>
        [Fact]
        public void БезНазванияЯрлыкБерётИмяОтExe() {
            using var desktop = new TempDir();
            var exe = desktop.WriteFile("lethal-company.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.TryCreateDesktopShortcut("   ", exe);
            }

            if (!ShellAvailable) {
                return;
            }

            Assert.True(File.Exists(Path.Combine(desktop.Root, "lethal-company.lnk")));
        }

        /// <summary>Ярлык кладётся именно в каталог рабочего стола, а не рядом с игрой.</summary>
        [Fact]
        public void ЯрлыкЛожитсяВКаталогРабочегоСтола() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var exe = games.WriteFile("game/game.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", exe);
            }

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(exe)!, "*.lnk"));

            if (!ShellAvailable) {
                return;
            }

            Assert.True(File.Exists(Path.Combine(desktop.Root, "Игра.lnk")));
        }

        /// <summary>
        /// Недоступный каталог для ярлыка не роняет установку: рабочий стол может быть
        /// перенаправлен на отвалившийся сетевой диск.
        /// </summary>
        [Fact]
        public void НедоступныйКаталогЯрлыковНеРоняетУстановку() {
            using var desktop = new TempDir();
            var exe = desktop.WriteFile("game.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(@"Q:\нет-такого-диска\desktop")) {
                GameLocalState.TryCreateDesktopShortcut("Игра", exe);
            }
        }

        /// <summary>
        /// Умеет ли окружение ДЕЙСТВИТЕЛЬНО создать ярлык.
        /// <para>
        /// Раньше здесь проверялся только резолв ProgID — и этого оказалось мало. На
        /// сборочном агенте `WScript.Shell` резолвится, а `Save()` падает: скриптовый
        /// хост отключён политикой. Проверки «ярлык создан» валили прогон на ровном
        /// месте, хотя проверяемый код вёл себя ровно так, как задумано, — молча не
        /// создавал ярлык и не ронял установку.
        /// </para>
        /// <para>
        /// Поэтому проба настоящая: создаём ярлык во временном каталоге и смотрим, лёг
        /// ли он на диск. Результат считается один раз на прогон.
        /// </para>
        /// </summary>
        internal static bool ShellAvailable => ShellCanCreateShortcuts.Value;

        private static readonly Lazy<bool> ShellCanCreateShortcuts = new Lazy<bool>(ProbeShortcutCreation);

        private static bool ProbeShortcutCreation() {
            try {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) {
                    return false;
                }

                using var probe = new TempDir();
                var link = Path.Combine(probe.Root, "проба.lnk");
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(link);
                shortcut.TargetPath = Path.Combine(probe.Root, "проба.exe");
                shortcut.Save();
                return File.Exists(link);
            }
            catch {
                // Оболочка есть, но ярлыки не создаёт: политика, урезанная система, агент сборки.
                return false;
            }
        }
    }
}
