// <copyright file="ShortcutLaunchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Dynamic;
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
            var recorder = new ShortcutRecorder();
            using var desktop = new TempDir();
            using var games = new TempDir();
            var exe = games.WriteFile("gid/game.exe", "MZ");
            var launcher = games.WriteFile("launcher/ChillHub.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root, null, launcher, recorder.Create)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", "gid", exe);
            }

            var link = Path.Combine(desktop.Root, GameLocalState.ShortcutFileName("Игра"));
            var saved = Assert.Single(recorder.Saved);
            Assert.Equal(link, saved.Path);
            Assert.Equal(launcher, saved.Target, ignoreCase: true);
            Assert.Equal("gid", ShortcutTarget.Parse(SplitArgs(saved.Arguments))!.GameId);
            Assert.Equal(exe, ShortcutTarget.Parse(SplitArgs(saved.Arguments))!.ExePath, ignoreCase: true);

            // Значок — от игры: ярлык обязан выглядеть игрой, а не ещё одной копией лаунчера.
            Assert.Equal(exe + ",0", saved.Icon, ignoreCase: true);
        }

        /// <summary>
        /// Лаунчер не нашёлся (запуск из-под отладчика, необычная установка) — ярлык всё
        /// равно создаётся, но ведёт прямо в игру: работающий ярлык мимо лаунчера лучше,
        /// чем ярлык в никуда.
        /// </summary>
        [Fact]
        public void БезПутиКЛаунчеруЯрлыкВедётПрямоВИгру() {
            var recorder = new ShortcutRecorder();
            using var desktop = new TempDir();
            using var games = new TempDir();
            var exe = games.WriteFile("gid/game.exe", "MZ");

            using (GameLocalState.OverrideShortcutEnvironmentForTests(desktop.Root, null, string.Empty, recorder.Create)) {
                GameLocalState.TryCreateDesktopShortcut("Игра", "gid", exe);
            }

            var link = Path.Combine(desktop.Root, GameLocalState.ShortcutFileName("Игра"));
            var saved = Assert.Single(recorder.Saved);
            Assert.Equal(link, saved.Path);
            Assert.Equal(exe, saved.Target, ignoreCase: true);
            Assert.Equal(string.Empty, saved.Arguments);
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
                ShortcutExePath.GamesRoot = () => games.Root;
                ShortcutFallbackLaunch.StartProcess = psi => started = psi;
                Assert.True(ShortcutFallbackLaunch.TryStart(exe));
            }
            finally {
                ShortcutFallbackLaunch.ResetForTests();
                ShortcutExePath.ResetForTests();
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
                ShortcutExePath.GamesRoot = () => games.Root;
                ShortcutFallbackLaunch.StartProcess = _ => started = true;

                Assert.False(ShortcutFallbackLaunch.TryStart(Path.Combine(games.Root, "нет-такого.exe")));
                Assert.False(ShortcutFallbackLaunch.TryStart(null));
                Assert.False(ShortcutFallbackLaunch.TryStart("   "));
            }
            finally {
                ShortcutFallbackLaunch.ResetForTests();
                ShortcutExePath.ResetForTests();
            }

            Assert.False(started);
        }

        /// <summary>
        /// ПУТЬ ИЗ ЯРЛЫКА — НЕ НАШ ПУТЬ. Ключ <c>--exe</c> пишет ярлык, но ярлык — обычный
        /// файл на рабочем столе, а аргументы лаунчеру может передать кто угодно. Пока путь
        /// не проверялся, Chill Hub работал переходником «запусти вот этот exe»: человек
        /// видел окно от знакомого лаунчера, нажимал одну кнопку — и стартовало что угодно
        /// с любого диска.
        /// </summary>
        [Fact]
        public void ПутьВнеПапкиИгрНеЗапускается() {
            using var games = new TempDir();
            using var elsewhere = new TempDir();
            var stranger = elsewhere.WriteFile("evil.exe", "MZ");
            var started = false;

            try {
                ShortcutExePath.GamesRoot = () => games.Root;
                ShortcutFallbackLaunch.StartProcess = _ => started = true;

                Assert.False(ShortcutFallbackLaunch.TryStart(stranger));

                // И тот же путь, поданный «изнутри» папки игр через «..».
                Assert.False(ShortcutFallbackLaunch.TryStart(
                    Path.Combine(games.Root, "gid", "..", "..", Path.GetFileName(elsewhere.Root), "evil.exe")));
            }
            finally {
                ShortcutFallbackLaunch.ResetForTests();
                ShortcutExePath.ResetForTests();
            }

            Assert.False(started);
        }

        /// <summary>
        /// Не exe мы не запускаем даже из папки игр: ярлык на .bat или .cmd — это уже не
        /// «запустить установленную копию», а выполнение чужого сценария.
        /// </summary>
        [Fact]
        public void НеИсполняемыйФайлНеЗапускается() {
            using var games = new TempDir();
            var script = games.WriteFile("gid/run.bat", "@echo off");
            var started = false;

            try {
                ShortcutExePath.GamesRoot = () => games.Root;
                ShortcutFallbackLaunch.StartProcess = _ => started = true;

                Assert.False(ShortcutFallbackLaunch.TryStart(script));
            }
            finally {
                ShortcutFallbackLaunch.ResetForTests();
                ShortcutExePath.ResetForTests();
            }

            Assert.False(started);
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

        /// <summary>
        /// Запоминает параметры только в момент Save: создание объекта без сохранения
        /// не считается созданным ярлыком. Не зависит от политики WScript.Shell на агенте.
        /// </summary>
        private sealed class ShortcutRecorder {
            internal List<(string Path, string Target, string Arguments, string Icon)> Saved { get; } = new();

            internal object Create(string path) {
                dynamic shortcut = new ExpandoObject();
                shortcut.Save = (Action)(() => this.Saved.Add((
                    path, (string)shortcut.TargetPath, (string)shortcut.Arguments, (string)shortcut.IconLocation)));
                return shortcut;
            }
        }
    }
}
