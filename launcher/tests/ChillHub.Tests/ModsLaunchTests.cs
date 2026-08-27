// <copyright file="ModsLaunchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;

    using ChillHub.Core;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Слой запуска игры с модами: разбор VDF, поиск копии в Steam, переключение
    /// <c>doorstop_config.ini</c> и четыре варианта запуска.
    /// <para>
    /// Всё это работает на чужих данных — файлах Steam и файлах, которые кладёт пакет
    /// BepInEx, — и ошибка здесь выглядит для игрока одинаково: «не запускается» либо
    /// «моды не грузятся». Поэтому фикстуры повторяют настоящие файлы, включая
    /// вложенную папку How to Fish и два разных написания ключа <c>enabled</c>,
    /// снятые с реальных установок Lethal Company и Risk of Rain 2.
    /// </para>
    /// </summary>
    public class ModsLaunchTests : IDisposable {
        private readonly string root;

        /// <summary>Инициализирует временный каталог под фикстуры.</summary>
        public ModsLaunchTests() {
            this.root = Path.Combine(Path.GetTempPath(), "ChillHubModsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.root);
        }

        /// <inheritdoc/>
        public void Dispose() {
            SteamLocator.ResetForTests();
            ModsLaunch.ResetForTests();
            try {
                Directory.Delete(this.root, true);
            }
            catch (IOException) {
                // Временный каталог не удалился — на прогон тестов это не влияет.
            }

            GC.SuppressFinalize(this);
        }

        // ---------- VDF ----------

        /// <summary>Современный libraryfolders.vdf: вложенные блоки с ключом path.</summary>
        [Fact]
        public void VdfЧитаетВложенныеБиблиотеки() {
            const string Text = """
                "libraryfolders"
                {
                    "0"
                    {
                        "path"        "C:\\Program Files (x86)\\Steam"
                        "apps"
                        {
                            "1966720"    "10000"
                        }
                    }
                    "1"
                    {
                        "path"        "D:\\SteamLibrary"
                    }
                    "contentstatsid"    "-123"
                }
                """;

            var root = VdfParser.Parse(Text);
            var folders = root.Child("libraryfolders");

            Assert.NotNull(folders);
            Assert.Equal(@"C:\Program Files (x86)\Steam", folders!.Child("0")!.String("path"));
            Assert.Equal(@"D:\SteamLibrary", folders.Child("1")!.String("path"));
        }

        /// <summary>Экранированные слеши в путях Windows должны разэкранироваться.</summary>
        [Fact]
        public void VdfРазэкранируетПутиWindows() {
            var root = VdfParser.Parse("\"AppState\" { \"installdir\" \"How to Fish\" \"StateFlags\" \"4\" }");
            var state = root.Child("AppState");

            Assert.NotNull(state);
            Assert.Equal("How to Fish", state!.String("installdir"));
            Assert.Equal("4", state.String("StateFlags"));
        }

        /// <summary>Обрезанный файл не должен ронять разбор — читаем, что успели.</summary>
        [Fact]
        public void VdfПереживаетОбрезанныйФайл() {
            var root = VdfParser.Parse("\"AppState\" { \"installdir\" \"Game\" \"name\"");

            Assert.Equal("Game", root.Child("AppState")!.String("installdir"));
        }

        /// <summary>Пустой и мусорный ввод дают пустой узел, а не исключение.</summary>
        [Fact]
        public void VdfПереживаетМусор() {
            Assert.Empty(VdfParser.Parse(null).Children);
            Assert.Empty(VdfParser.Parse(string.Empty).Children);
            Assert.Empty(VdfParser.Parse("}}}{{{").Children);
        }

        // ---------- Поиск Steam ----------

        /// <summary>Собирает подставную установку Steam с одной игрой.</summary>
        private string MakeSteam(string appId, string installDir, string? extraNested = null) {
            var steam = Path.Combine(this.root, "Steam");
            var apps = Path.Combine(steam, "steamapps");
            Directory.CreateDirectory(apps);
            File.WriteAllText(Path.Combine(steam, "steam.exe"), "not really");
            File.WriteAllText(
                Path.Combine(apps, "libraryfolders.vdf"),
                "\"libraryfolders\" { \"0\" { \"path\" \"" + steam.Replace("\\", "\\\\") + "\" } }");
            File.WriteAllText(
                Path.Combine(apps, $"appmanifest_{appId}.acf"),
                "\"AppState\" { \"installdir\" \"" + installDir + "\" \"StateFlags\" \"4\" }");

            var gameDir = Path.Combine(apps, "common", installDir);
            if (extraNested != null) {
                gameDir = Path.Combine(gameDir, extraNested);
            }

            Directory.CreateDirectory(gameDir);
            SteamLocator.SteamPathOverride = () => steam;
            return gameDir;
        }

        /// <summary>Обычный случай: папка игры совпадает с installdir.</summary>
        [Fact]
        public void SteamНаходитОбычнуюПапку() {
            var expected = this.MakeSteam("1966720", "Lethal Company");

            var found = SteamLocator.Locate("1966720", "Lethal Company");

            Assert.Equal(SteamLookup.Found, found.Outcome);
            Assert.Equal(expected, found.GameDir);
            Assert.True(found.Ok);
        }

        /// <summary>
        /// Вложенный случай How to Fish: installdir «How to Fish», а игра лежит на уровень
        /// глубже. Наивный путь ведёт в каталог без exe, и игра «не находится» при том,
        /// что она установлена.
        /// </summary>
        [Fact]
        public void SteamНаходитВложеннуюПапку() {
            var expected = this.MakeSteam("4001890", "How to Fish", "How to Fish");

            var found = SteamLocator.Locate("4001890", "How to Fish/How to Fish");

            Assert.Equal(SteamLookup.Found, found.Outcome);
            Assert.Equal(expected, found.GameDir);
        }

        /// <summary>
        /// Схема обещает вложенную папку, а на диске её нет — откатываемся на обычную,
        /// вместо того чтобы объявить установленную игру ненайденной.
        /// </summary>
        [Fact]
        public void SteamОткатываетсяНаОбычнуюПапкуЕслиВложеннойНет() {
            var plain = this.MakeSteam("4001890", "How to Fish");

            var found = SteamLocator.Locate("4001890", "How to Fish/How to Fish");

            Assert.Equal(SteamLookup.Found, found.Outcome);
            Assert.Equal(plain, found.GameDir);
        }

        /// <summary>Игра без манифеста в библиотеке считается неустановленной.</summary>
        [Fact]
        public void SteamСообщаетЧтоИграНеУстановлена() {
            this.MakeSteam("1966720", "Lethal Company");

            var found = SteamLocator.Locate("999999", "Whatever");

            Assert.Equal(SteamLookup.GameNotInstalled, found.Outcome);
            Assert.False(found.Ok);
        }

        /// <summary>Без AppID искать нечего, и это отдельная причина, а не «не нашли».</summary>
        [Fact]
        public void SteamТребуетAppId() {
            Assert.Equal(SteamLookup.NoAppId, SteamLocator.Locate(null, "X").Outcome);
            Assert.Equal(SteamLookup.NoAppId, SteamLocator.Locate("  ", "X").Outcome);
        }

        /// <summary>
        /// Каждая ступень поиска попадает в след. Пользователь пришлёт «не находит игру»,
        /// и по журналу должно быть видно, на какой именно ступени оборвалось.
        /// </summary>
        [Fact]
        public void SteamПишетСледПоиска() {
            this.MakeSteam("1966720", "Lethal Company");

            var found = SteamLocator.Locate("1966720", "Lethal Company");

            Assert.NotEmpty(found.Trace);
            Assert.Contains(found.Trace, t => t.Contains("библиотек найдено", StringComparison.Ordinal));
            Assert.Contains(found.Trace, t => t.Contains("installdir", StringComparison.Ordinal));
            Assert.Contains(found.Trace, t => t.Contains("папка игры", StringComparison.Ordinal));
        }

        // ---------- doorstop_config.ini ----------

        /// <summary>Создаёт папку игры с файлом настроек Doorstop.</summary>
        private string MakeGameDir(string ini, bool withProxy = true) {
            var dir = Path.Combine(this.root, "Game" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, DoorstopConfig.FileName), ini);
            if (withProxy) {
                File.WriteAllText(Path.Combine(dir, DoorstopConfig.ProxyDllName), "proxy");
            }

            return dir;
        }

        /// <summary>
        /// Оформление файла у разных сборок разное — с пробелами вокруг «=» у Lethal
        /// Company и без у Risk of Rain 2. Переписывать надо только значение: иначе при
        /// каждом запуске лаунчер считает файл изменившимся.
        /// </summary>
        /// <param name="ini">Исходное содержимое файла.</param>
        /// <param name="expected">Ожидаемая строка после выключения.</param>
        [Theory]
        [InlineData("[General]\nenabled = true\ntarget_assembly=BepInEx\\core\\X.dll\n", "enabled = false")]
        [InlineData("[General]\nenabled=true\ntarget_assembly=BepInEx/core/X.dll\n", "enabled=false")]
        [InlineData("[UnityDoorstop]\nenabled   =   true\ntargetAssembly=X\n", "enabled   =   false")]
        public void DoorstopМеняетТолькоЗначениеСохраняяОформление(string ini, string expected) {
            var dir = this.MakeGameDir(ini);

            Assert.True(DoorstopConfig.SetEnabled(dir, false));

            var lines = File.ReadAllLines(Path.Combine(dir, DoorstopConfig.FileName));
            Assert.Contains(expected, lines);
            Assert.False(DoorstopConfig.ReadEnabled(dir));
        }

        /// <summary>Включение и выключение — обратимые операции над одним ключом.</summary>
        [Fact]
        public void DoorstopПереключаетсяТудаИОбратно() {
            var dir = this.MakeGameDir("[General]\nenabled = true\n");

            Assert.True(DoorstopConfig.SetEnabled(dir, false));
            Assert.False(DoorstopConfig.ReadEnabled(dir));

            Assert.True(DoorstopConfig.SetEnabled(dir, true));
            Assert.True(DoorstopConfig.ReadEnabled(dir));
        }

        /// <summary>Закомментированный ключ — не ключ.</summary>
        [Fact]
        public void DoorstopНеПутаетКомментарийСНастройкой() {
            var dir = this.MakeGameDir("[General]\n# enabled = true\n; enabled = true\nenabled = false\n");

            Assert.False(DoorstopConfig.ReadEnabled(dir));
            Assert.True(DoorstopConfig.SetEnabled(dir, true));

            var text = File.ReadAllText(Path.Combine(dir, DoorstopConfig.FileName));
            Assert.Contains("# enabled = true", text, StringComparison.Ordinal);
            Assert.Contains("\nenabled = true", text, StringComparison.Ordinal);
        }

        /// <summary>Файла нет — переключать нечего, но и падать не за что.</summary>
        [Fact]
        public void DoorstopБезФайлаНеПадает() {
            var dir = Path.Combine(this.root, "Empty");
            Directory.CreateDirectory(dir);

            Assert.Null(DoorstopConfig.ReadEnabled(dir));
            Assert.False(DoorstopConfig.SetEnabled(dir, true));
            Assert.False(DoorstopConfig.IsInstalled(dir));
        }

        /// <summary>Файл без ключа enabled трогать нельзя — это не файл Doorstop.</summary>
        [Fact]
        public void DoorstopБезКлючаНичегоНеПишет() {
            var dir = this.MakeGameDir("[General]\ntarget_assembly=X\n");

            Assert.False(DoorstopConfig.SetEnabled(dir, false));
            Assert.Equal("[General]\ntarget_assembly=X\n", File.ReadAllText(Path.Combine(dir, DoorstopConfig.FileName)));
        }

        /// <summary>Мажорная версия Doorstop читается из .doorstop_version.</summary>
        [Fact]
        public void DoorstopЧитаетВерсию() {
            var dir = this.MakeGameDir("[General]\nenabled = true\n");
            File.WriteAllText(Path.Combine(dir, DoorstopConfig.VersionFileName), "4.5.0");

            Assert.Equal(4, DoorstopConfig.ReadMajorVersion(dir));
            Assert.Equal(0, DoorstopConfig.ReadMajorVersion(Path.Combine(this.root, "nope")));
        }

        /// <summary>Установленными моды считаются только когда есть и перехватчик, и настройки.</summary>
        [Fact]
        public void DoorstopУстановленТолькоСОбоимиФайлами() {
            Assert.True(DoorstopConfig.IsInstalled(this.MakeGameDir("[General]\nenabled = true\n")));
            Assert.False(DoorstopConfig.IsInstalled(this.MakeGameDir("[General]\nenabled = true\n", withProxy: false)));
        }

        // ---------- Варианты запуска ----------

        private static ModsInfo Pack() => new() {
            HasLatest = true,
            Version = "ASTeam-LethalReloaded-2.2.12",
            DisplayName = "Lethal Reloaded",
            DisplayVersion = "2.2.12",
            SteamAppId = "1966720",
        };

        private static SteamGame NoSteam()
            => new(SteamLookup.SteamNotInstalled, string.Empty, string.Empty, Array.Empty<string>());

        /// <summary>Все четыре варианта доступны, когда есть и Steam-копия, и сборка, и модпак.</summary>
        [Fact]
        public void ВсеЧетыреВариантаДоступныПриПолномНаборе() {
            var steamDir = this.MakeGameDir("[General]\nenabled = true\n");
            var localDir = this.MakeGameDir("[General]\nenabled = true\n");
            var steam = new SteamGame(SteamLookup.Found, steamDir, "steam.exe", Array.Empty<string>());

            var options = ModsLaunch.Options(Pack(), localDir, localInstalled: true, steam);

            Assert.Equal(4, options.Count);
            Assert.All(options, o => Assert.True(o.Available, $"{o.Target}: {o.Reason}"));
            Assert.Contains(options, o => o.Title.Contains("Lethal Reloaded 2.2.12", StringComparison.Ordinal));
        }

        /// <summary>
        /// Недоступный вариант остаётся в списке с причиной. Исчезнувший пункт ничего
        /// не объясняет игроку, а «Steam не установлен» — объясняет.
        /// </summary>
        [Fact]
        public void БезSteamОстаютсяТолькоЛокальныеВарианты() {
            var localDir = this.MakeGameDir("[General]\nenabled = true\n");

            var options = ModsLaunch.Options(Pack(), localDir, localInstalled: true, NoSteam());

            var steamOptions = options.Where(o => o.ViaSteam).ToList();
            Assert.Equal(2, steamOptions.Count);
            Assert.All(steamOptions, o => Assert.False(o.Available));
            Assert.All(steamOptions, o => Assert.Equal("Steam не установлен", o.Reason));
            Assert.All(options.Where(o => !o.ViaSteam), o => Assert.True(o.Available));
        }

        /// <summary>Без опубликованного модпака остаются только ванильные варианты.</summary>
        [Fact]
        public void БезМодпакаОстаютсяТолькоВанильныеВарианты() {
            var localDir = this.MakeGameDir("[General]\nenabled = true\n");

            var options = ModsLaunch.Options(null, localDir, localInstalled: true, NoSteam());

            Assert.False(options.Single(o => o.Target == LaunchTarget.LocalModded).Available);
            Assert.True(options.Single(o => o.Target == LaunchTarget.LocalVanilla).Available);
        }

        /// <summary>
        /// Модпак опубликован, но в папку ещё не установлен — вариант «с модами» недоступен
        /// и прямо говорит, что делать.
        /// </summary>
        [Fact]
        public void БезФайловМодовВариантСМодамиНедоступен() {
            var localDir = Path.Combine(this.root, "NoMods");
            Directory.CreateDirectory(localDir);

            var options = ModsLaunch.Options(Pack(), localDir, localInstalled: true, NoSteam());

            var modded = options.Single(o => o.Target == LaunchTarget.LocalModded);
            Assert.False(modded.Available);
            Assert.Contains("Обновить", modded.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Запуск «с модами» включает Doorstop, запуск «без модов» — выключает. Это и есть
        /// единственная разница между двумя режимами.
        /// </summary>
        [Fact]
        public void ЗапускПереключаетDoorstop() {
            var localDir = this.MakeGameDir("[General]\nenabled = false\n");
            File.WriteAllText(Path.Combine(localDir, "Game.exe"), "exe");
            ProcessStartInfo? captured = null;
            ModsLaunch.StartProcess = psi => {
                captured = psi;
                return null;
            };

            var options = ModsLaunch.Options(Pack(), localDir, localInstalled: true, NoSteam());

            ModsLaunch.Start(options.Single(o => o.Target == LaunchTarget.LocalModded), Pack(), "Game.exe", NoSteam());
            Assert.True(DoorstopConfig.ReadEnabled(localDir));
            Assert.Equal(Path.Combine(localDir, "Game.exe"), captured!.FileName);

            ModsLaunch.Start(options.Single(o => o.Target == LaunchTarget.LocalVanilla), Pack(), "Game.exe", NoSteam());
            Assert.False(DoorstopConfig.ReadEnabled(localDir));
        }

        /// <summary>Steam-вариант стартует steam.exe с -applaunch и нужным AppID.</summary>
        [Fact]
        public void SteamВариантЗапускаетSteamСAppId() {
            var steamDir = this.MakeGameDir("[General]\nenabled = false\n");
            var steamExe = Path.Combine(this.root, "steam.exe");
            File.WriteAllText(steamExe, "steam");
            var steam = new SteamGame(SteamLookup.Found, steamDir, steamExe, Array.Empty<string>());
            ProcessStartInfo? captured = null;
            ModsLaunch.StartProcess = psi => {
                captured = psi;
                return null;
            };

            var options = ModsLaunch.Options(Pack(), steamDir, localInstalled: false, steam);
            ModsLaunch.Start(options.Single(o => o.Target == LaunchTarget.SteamModded), Pack(), string.Empty, steam);

            Assert.Equal(steamExe, captured!.FileName);
            Assert.Equal(new[] { "-applaunch", "1966720" }, captured.ArgumentList.ToArray());
            Assert.True(DoorstopConfig.ReadEnabled(steamDir));
        }

        /// <summary>Недоступный вариант не запускается вообще.</summary>
        [Fact]
        public void НедоступныйВариантНеЗапускается() {
            var called = false;
            ModsLaunch.StartProcess = _ => {
                called = true;
                return null;
            };

            var blocked = new LaunchOption(LaunchTarget.SteamModded, "x", this.root, true, false, "нет");
            Assert.Null(ModsLaunch.Start(blocked, Pack(), string.Empty, NoSteam()));
            Assert.False(called);
        }

        /// <summary>
        /// Исполняемый файл ищется по пути из реестра, а если его нет — по единственному
        /// exe в корне, не считая UnityCrashHandler, который лежит рядом у любой игры на Unity.
        /// </summary>
        [Fact]
        public void ExeИщетсяПоРееструИПоЕдинственномуКандидату() {
            var dir = Path.Combine(this.root, "ExeSearch");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Lethal Company.exe"), "game");
            File.WriteAllText(Path.Combine(dir, "UnityCrashHandler64.exe"), "handler");

            Assert.Equal(
                Path.Combine(dir, "Lethal Company.exe"),
                ModsLaunch.ResolveExe(dir, "Lethal Company.exe"));

            // Путь из реестра неверен — остаётся единственный настоящий кандидат.
            Assert.Equal(
                Path.Combine(dir, "Lethal Company.exe"),
                ModsLaunch.ResolveExe(dir, "Missing.exe"));

            File.WriteAllText(Path.Combine(dir, "Second.exe"), "another");
            Assert.Null(ModsLaunch.ResolveExe(dir, "Missing.exe"));
        }

        /// <summary>Пустой модпак не даёт подписи, а заполненный даёт читаемую строку.</summary>
        [Fact]
        public void ОписаниеМодпакаЧитаемо() {
            Assert.Equal("Lethal Reloaded 2.2.12", Pack().Describe());
            Assert.Equal(string.Empty, new ModsInfo().Describe());
            Assert.Equal(
                "ASTeam-LethalReloaded-2.2.12",
                new ModsInfo { HasLatest = true, Version = "ASTeam-LethalReloaded-2.2.12" }.Describe());
        }
    }
}
