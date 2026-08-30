// <copyright file="ShortcutLaunchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Diagnostics;
    using System.IO;

    using ChillHub.Core.Home;
    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Куда на самом деле ведёт созданный ярлык и что происходит, когда лаунчеру нечего
    /// с игрой делать.
    /// <para>
    /// Ярлык ведёт в лаунчер, а не в игру: игра из ярлыка обычно требует внимания — вышло
    /// обновление, есть модпак, разъехались файлы. Прямой запуск exe всё это обходил
    /// молча. Проверяется именно цель ярлыка: сломается она — и человек снова окажется
    /// в игре старой версии, ничего об этом не узнав.
    /// </para>
    /// </summary>
    public class ShortcutLaunchTests {
        /// <summary>Ярлык ведёт в лаунчер и несёт в аргументах игру, путь к ней и название.</summary>
        [Fact]
        public void ЯрлыкВедётВЛаунчерСАргументамиИгры() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var exe = games.WriteFile("gid/game.exe", "MZ");
            var launcher = games.WriteFile("launcher/ChillHub.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root, null, launcher)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", "gid", exe);
            }

            var link = Path.Combine(desktop.Root, "Игра.lnk");
            if (!GameLocalStateShortcutTests.ShellAvailable || !File.Exists(link)) {
                // Оболочка ярлыков не создаёт (политика, урезанная система, агент сборки).
                return;
            }

            var (target, arguments, icon) = ReadLink(link);
            Assert.Equal(launcher, target, ignoreCase: true);
            Assert.Equal("gid", ShortcutTarget.Parse(SplitArgs(arguments))!.GameId);
            Assert.Equal(exe, ShortcutTarget.Parse(SplitArgs(arguments))!.ExePath, ignoreCase: true);

            // Значок — от игры: ярлык обязан выглядеть игрой, а не ещё одной копией лаунчера.
            Assert.StartsWith(exe, icon, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Лаунчер не нашёлся (запуск из-под отладчика, необычная установка) — ярлык всё
        /// равно создаётся, но ведёт прямо в игру: работающий ярлык мимо лаунчера лучше,
        /// чем ярлык в никуда.
        /// </summary>
        [Fact]
        public void БезПутиКЛаунчеруЯрлыкВедётПрямоВИгру() {
            using var desktop = new TempDir();
            using var games = new TempDir();
            var exe = games.WriteFile("gid/game.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root, null, string.Empty)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", "gid", exe);
            }

            var link = Path.Combine(desktop.Root, "Игра.lnk");
            if (!GameLocalStateShortcutTests.ShellAvailable || !File.Exists(link)) {
                return;
            }

            var (target, arguments, _) = ReadLink(link);
            Assert.Equal(exe, target, ignoreCase: true);
            Assert.Equal(string.Empty, arguments);
        }

        /// <summary>
        /// Запуск мимо лаунчера идёт из папки игры: игры ищут свои файлы относительно
        /// рабочего каталога, и из чужого они не стартуют.
        /// </summary>
        [Fact]
        public void ЗапускМимоЛаунчераИдётИзПапкиИгры() {
            using var games = new TempDir();
            var exe = games.WriteFile("gid/game.exe", "MZ");
            ProcessStartInfo? started = null;

            try {
                ShortcutFallbackLaunch.StartProcess = psi => started = psi;
                Assert.True(ShortcutFallbackLaunch.TryStart(exe));
            }
            finally {
                ShortcutFallbackLaunch.ResetForTests();
            }

            Assert.Equal(exe, started!.FileName);
            Assert.Equal(Path.GetDirectoryName(exe), started.WorkingDirectory);
        }

        /// <summary>
        /// Файл мог исчезнуть между показом окна и нажатием кнопки: тогда запуска нет, а
        /// окно обязано об этом сказать — но не упасть.
        /// </summary>
        [Fact]
        public void ПропавшийФайлНеЗапускаетсяИНеПадает() {
            using var games = new TempDir();
            var started = false;

            try {
                ShortcutFallbackLaunch.StartProcess = _ => started = true;

                Assert.False(ShortcutFallbackLaunch.TryStart(Path.Combine(games.Root, "нет-такого.exe")));
                Assert.False(ShortcutFallbackLaunch.TryStart(null));
                Assert.False(ShortcutFallbackLaunch.TryStart("   "));
            }
            finally {
                ShortcutFallbackLaunch.ResetForTests();
            }

            Assert.False(started);
        }

        /// <summary>Читает цель, аргументы и значок ярлыка через ту же оболочку, что их писала.</summary>
        /// <param name="linkPath">Путь к файлу ярлыка.</param>
        /// <returns>Цель, аргументы и значок.</returns>
        private static (string Target, string Arguments, string Icon) ReadLink(string linkPath) {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            return ((string)link.TargetPath, (string)link.Arguments, (string)link.IconLocation);
        }

        /// <summary>Разбивает строку аргументов так же, как это делает Windows.</summary>
        /// <param name="commandLine">Строка аргументов ярлыка.</param>
        /// <returns>Аргументы по одному.</returns>
        private static string[] SplitArgs(string commandLine) {
            var parts = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;
            foreach (var c in commandLine) {
                if (c == '"') {
                    inQuotes = !inQuotes;
                }
                else if (c == ' ' && !inQuotes) {
                    if (current.Length > 0) {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                }
                else {
                    current.Append(c);
                }
            }

            if (current.Length > 0) {
                parts.Add(current.ToString());
            }

            return parts.ToArray();
        }
    }
}
