// <copyright file="BrokenModPackTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Home;
    using ChillHub.Core.Mods;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Модпак, из которого руками удалили файл.
    /// <para>
    /// ЛАУНЧЕР ЭТОГО НЕ ЗАМЕЧАЛ НИГДЕ. Установленность модпака определял один маркер
    /// версии, а его удаление файла не меняет: список игр показывал «Играть», проверка
    /// файлов ходила только в сборку Chill Hub, а пункт «Steam · с модами» обещал
    /// запуск с полным паком, пока в папке лежала его половина.
    /// </para>
    /// <para>
    /// Проверяется всё три места сразу: быстрая сверка присутствия файлов, вариант
    /// запуска, который она меняет, и проверка файлов, которая теперь доходит до чужой
    /// папки — копии игры из Steam.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]

    public class BrokenModPackTests {
        // ---------- Сверка присутствия ----------

        /// <summary>Все файлы на месте — придраться не к чему.</summary>
        [Fact]
        public void ЦелыйМодпакПоломкойНеСчитается() {
            using var dir = new TempDir();
            InstallPack(dir.Root);

            var state = ModPackFiles.Inspect(dir.Root);

            Assert.True(state.Known);
            Assert.False(state.Broken);
            Assert.Equal(0, state.Missing);
        }

        /// <summary>ГЛАВНАЯ ПРОВЕРКА: игрок удалил надоевший мод — модпак неполон.</summary>
        [Fact]
        public void УдалённыйРукамиМодВидноСразу() {
            using var dir = new TempDir();
            InstallPack(dir.Root);

            File.Delete(Path.Combine(dir.Root, "BepInEx", "plugins", "Mod.dll"));

            var state = ModPackFiles.Inspect(dir.Root);

            Assert.True(state.Broken);
            Assert.Equal(1, state.Missing);
        }

        /// <summary>Удалённая папка целиком — тоже поломка, а не «папки нет, значит и спроса нет».</summary>
        [Fact]
        public void УдалённаяПапкаМодовВидна() {
            using var dir = new TempDir();
            InstallPack(dir.Root);

            Directory.Delete(Path.Combine(dir.Root, "BepInEx", "plugins"), recursive: true);

            var state = ModPackFiles.Inspect(dir.Root);

            Assert.True(state.Broken);
            Assert.Equal(2, state.Missing);
        }

        /// <summary>
        /// РАЗМЕР НЕ СВЕРЯЕТСЯ, И ЭТО НЕ НЕДОСМОТР. Модпак приносит с собой
        /// <c>BepInEx/config/*.cfg</c>, а BepInEx переписывает их при запуске игры,
        /// дописывая настройки новых модов. Сверяй мы размеры — кнопка звала бы
        /// восстанавливать моды после каждой сессии, починяя то, что не ломалось.
        /// Обрезанный файл ловит «Проверить файлы», которая считает хеши.
        /// </summary>
        [Fact]
        public void ПереписанныйИгройКонфигПоломкойНеСчитается() {
            using var dir = new TempDir();
            InstallPack(dir.Root);

            File.WriteAllText(
                Path.Combine(dir.Root, "BepInEx", "config", "BepInEx.cfg"),
                "[Logging]\r\nEnabled = true\r\n; дописано игрой при запуске\r\n");

            Assert.False(ModPackFiles.Broken(dir.Root));
        }

        /// <summary>
        /// <c>doorstop_config.ini</c> правит сам лаунчер при каждом переключении
        /// «с модами / без модов». Считать его порчей значило бы требовать
        /// восстановления модпака после каждой ванильной сессии.
        /// </summary>
        [Fact]
        public void ИзменённыйDoorstopПоломкойНеСчитается() {
            using var dir = new TempDir();
            InstallPack(dir.Root);

            File.WriteAllText(
                Path.Combine(dir.Root, DoorstopConfig.FileName), "[General]\r\nenabled = false\r\n; и ещё строка\r\n");

            Assert.False(ModPackFiles.Broken(dir.Root));
        }

        /// <summary>
        /// Модпака в папке нет — сверять не с чем, и молчание здесь единственный
        /// честный ответ: «сломан» означал бы переустановку у того, кто моды и не ставил.
        /// </summary>
        [Fact]
        public void БезКопииМанифестаОПоломкеНеГоворим() {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Root, "game.exe"), "MZ");

            var state = ModPackFiles.Inspect(dir.Root);

            Assert.False(state.Known);
            Assert.False(state.Broken);
            Assert.False(ModPackFiles.Broken(dir.PathTo("нет-такой-папки")));
            Assert.False(ModPackFiles.Broken(null));
        }

        // ---------- Варианты запуска ----------

        /// <summary>
        /// Пункт «Steam · с модами» после удаления мода обещает не запуск, а
        /// восстановление — и делает его сам.
        /// </summary>
        [Fact]
        public void СломанныйМодпакВSteamМеняетПунктНаВосстановление() {
            using var dir = new TempDir();
            InstallPack(dir.Root);
            File.Delete(Path.Combine(dir.Root, "BepInEx", "plugins", "Mod.dll"));

            var modded = SteamOption(dir.Root);

            Assert.Equal(LaunchAction.RepairMods, modded.Action);
            Assert.Equal("восстановить моды", modded.Note);
        }

        /// <summary>А целый модпак так и остаётся готовым к запуску.</summary>
        [Fact]
        public void ЦелыйМодпакВSteamЗапускаетсяКакРаньше() {
            using var dir = new TempDir();
            InstallPack(dir.Root);

            Assert.Equal(LaunchAction.Play, SteamOption(dir.Root).Action);
        }

        /// <summary>
        /// Без маркера версии сверять нечего, и обходить папку незачем: этот вопрос
        /// задаётся при каждом пересчёте вариантов запуска.
        /// </summary>
        [Fact]
        public void БезУстановленногоМодпакаПапкуНеОбходят() {
            using var dir = new TempDir();
            var asked = new List<string>();

            var options = LaunchPlan.OptionsFor(GameWithPack(), Probes(dir.Root, modsInSteam: string.Empty, asked));

            Assert.Equal(LaunchAction.InstallMods, options.Single(o => o.Target == LaunchTarget.SteamModded).Action);
            Assert.Empty(asked);
        }

        /// <summary>Та же поломка в сборке Chill Hub доводит до «восстановить моды».</summary>
        [Fact]
        public void СломанныйМодпакВСборкеТожеВиден() {
            using var dir = new TempDir();
            InstallPack(dir.Root);
            File.Delete(Path.Combine(dir.Root, "BepInEx", "plugins", "Mod.dll"));

            var probes = new LaunchProbes(
                _ => dir.Root,
                _ => true,
                (_, _) => new SteamGame(SteamLookup.SteamNotInstalled, string.Empty, string.Empty, Array.Empty<string>()),
                _ => "vcMoo-Moo_Modpack-1.9.9",
                null,
                ModPackFiles.Broken);

            var game = GameWithPack();
            game.LatestVersion = "1.0.0";

            var local = LaunchPlan.OptionsFor(game, probes).Single(o => o.Target == LaunchTarget.LocalModded);

            Assert.Equal(LaunchAction.Update, local.Action);
            Assert.Equal("восстановить моды", local.Note);
        }

        // ---------- Проверка файлов ----------

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА ВТОРОЙ ЧАСТИ: «Проверить файлы» доходит до копии игры в
        /// Steam. Модпак лаунчер положил туда сам — значит, и отвечает за него там.
        /// </summary>
        [Fact]
        public async Task ПроверкаФайловДоходитДоКопииSteam() {
            using var local = new TempDir();
            using var steam = new TempDir();
            InstallPack(steam.Root);

            var roots = await RunAsync(SyncKind.Repair, local.Root, steam.Root);

            Assert.Contains(steam.Root, roots);
        }

        /// <summary>
        /// Установка и обновление сборки с сервера в чужую папку не лезут: тащить туда
        /// полтора гигабайта за компанию никто не просил.
        /// </summary>
        /// <param name="update">Обновление вместо установки: обе ведут себя одинаково.</param>
        /// <returns>Задача проверки.</returns>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task УстановкаИОбновлениеЧужуюПапкуНеТрогают(bool update) {
            using var local = new TempDir();
            using var steam = new TempDir();
            InstallPack(steam.Root);

            var roots = await RunAsync(update ? SyncKind.Update : SyncKind.Install, local.Root, steam.Root);

            Assert.DoesNotContain(steam.Root, roots);
        }

        /// <summary>Модпака в копии Steam нет — и ходить туда незачем.</summary>
        [Fact]
        public async Task БезМодпакаВSteamПроверкаТудаНеИдёт() {
            using var local = new TempDir();
            using var steam = new TempDir();

            var roots = await RunAsync(SyncKind.Repair, local.Root, steam.Root);

            Assert.DoesNotContain(steam.Root, roots);
        }

        // ---------- Список игр ----------

        /// <summary>
        /// Игра со сломанным модпаком в списке — не «свежая». Сверка с манифестом
        /// СБОРКИ про моды не знает и молчит: план пуст, а играть нельзя.
        /// </summary>
        [Fact]
        public async Task СписокИгрТребуетВосстановленияСломанногоМодпака() {
            using var games = new GamesPathScope();
            var root = Path.Combine(games.Root, "game");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "game.exe"), "MZ");
            InstallPack(root);
            File.Delete(Path.Combine(root, "BepInEx", "plugins", "Mod.dll"));

            var game = GameWithPack();
            game.LatestVersion = "1.0.0";
            GameLocalState.WriteLocalVersion("game", "1.0.0");

            await new GameStatusVerifier(
                new FakeSyncService(new DiffPlan()), () => "https://example.test", new SpaceHint(), new VerifiedGames())
                .VerifyAsync(game);

            Assert.True(game.IsInstalled);
            Assert.True(game.NeedsUpdate);
        }

        /// <summary>Модпака на сервере нет — восстанавливать не из чего, и звать некуда.</summary>
        [Fact]
        public void БезМодпакаНаСервереИгруНеТревожим() {
            Assert.False(GameStatus.ModsBroken(new GameInfo { GameId = "game" }));
            Assert.False(GameStatus.ModsBroken(null));
        }

        // ---------- Обстановка ----------

        /// <summary>Игра с опубликованным модпаком — та же во всех проверках выше.</summary>
        /// <returns>Игра из каталога.</returns>
        private static GameInfo GameWithPack() => new GameInfo {
            GameId = "game",
            Title = "Игра",
            Mods = new ModsInfo {
                HasLatest = true,
                Version = "vcMoo-Moo_Modpack-1.9.9",
                DisplayName = "Moo Modpack",
                DisplayVersion = "1.9.9",
                SteamAppId = "632360",
                ManifestUrl = "/manifests/_mods/game/v.json",
                ContentBaseUrl = "/content/_mods/game/v/files",
            },
        };

        /// <summary>Пункт «Steam · с модами» для папки, которую подставили вместо копии Steam.</summary>
        /// <param name="steamDir">Папка копии из Steam.</param>
        /// <returns>Вариант запуска.</returns>
        private static LaunchOption SteamOption(string steamDir)
            => LaunchPlan.OptionsFor(GameWithPack(), Probes(steamDir))
                .Single(o => o.Target == LaunchTarget.SteamModded);

        /// <summary>Пробы, у которых копия Steam — подставленная папка.</summary>
        /// <param name="steamDir">Папка копии из Steam.</param>
        /// <param name="modsInSteam">Версия модпака в ней.</param>
        /// <param name="asked">Куда записывать папки, у которых спросили о целости.</param>
        /// <returns>Набор проб.</returns>
        private static LaunchProbes Probes(
            string steamDir, string modsInSteam = "vcMoo-Moo_Modpack-1.9.9", List<string>? asked = null)
            => new LaunchProbes(
                gid => @"C:\games\" + gid,
                _ => false,
                (_, _) => new SteamGame(SteamLookup.Found, steamDir, @"C:\steam\steam.exe", Array.Empty<string>()),
                _ => modsInSteam,
                null,
                root => {
                    asked?.Add(root);
                    return ModPackFiles.Broken(root);
                });

        /// <summary>Кладёт в папку модпак: файлы, маркер версии и копию манифеста.</summary>
        /// <param name="root">Папка игры.</param>
        private static void InstallPack(string root) {
            var manifest = new Manifest { Version = "vcMoo-Moo_Modpack-1.9.9", Files = new List<ManifestFile>() };
            void Add(string rel, string content) {
                var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                manifest.Files.Add(new ManifestFile { Path = rel, Size = new FileInfo(path).Length });
            }

            Add(DoorstopConfig.ProxyDllName, "MZ");
            Add(DoorstopConfig.FileName, "[General]\r\nenabled = true\r\n");
            Add("BepInEx/core/BepInEx.Preloader.dll", "preloader");
            Add("BepInEx/config/BepInEx.cfg", "[Logging]\r\nEnabled = false\r\n");
            Add("BepInEx/plugins/Mod.dll", "мод, который удалят руками");
            Add("BepInEx/plugins/Other.dll", "мод, который останется");

            Assert.True(GameLocalState.WriteInstalledModPackManifest(root, manifest));
            Assert.True(GameLocalState.WriteModsVersionAt(root, manifest.Version));
        }

        /// <summary>
        /// Гоняет операцию и собирает папки, для которых строился план модпака.
        /// </summary>
        /// <param name="kind">Установка, обновление или проверка файлов.</param>
        /// <param name="localRoot">Папка сборки Chill Hub.</param>
        /// <param name="steamDir">Папка копии из Steam.</param>
        /// <returns>Корни, до которых дошла синхронизация модпака.</returns>
        private static async Task<List<string>> RunAsync(SyncKind kind, string localRoot, string steamDir) {
            var sync = new RootRecordingSync();
            var runner = new GameSyncRunner(sync, new GameSyncUi()) {
                Maintenance = () => new MaintenanceStateView(false, false, string.Empty),
                FreeSpaceFor = _ => long.MaxValue,
                WriteLocalVersion = (_, _) => { },
                SaveFingerprint = _ => { },
                ReportOutcome = _ => { },
                LocateSteam = (_, _) => new SteamGame(
                    SteamLookup.Found, steamDir, @"C:\steam\steam.exe", Array.Empty<string>()),
            };

            var request = new GameSyncRequest(
                "game", "1.0.0", "https://example.test", localRoot, null, false, kind, GameWithPack());

            await runner.RunAsync(request, CancellationToken.None);
            return sync.Roots;
        }

        /// <summary>Служба синхронизации, которая только запоминает, куда её позвали.</summary>
        private sealed class RootRecordingSync : ISyncService {
            /// <summary>Корни, для которых строился план.</summary>
            internal List<string> Roots { get; } = new List<string>();

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct)
                => Task.FromResult(new Manifest());

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => this.PlanAsync(manifest, localRoot, contentBaseUrl, PlanOptions.ForGame(localRoot), ct);

            public Task<DiffPlan> PlanAsync(
                Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct) {
                lock (this.Roots) {
                    this.Roots.Add(localRoot);
                }

                return Task.FromResult(new DiffPlan { LocalRoot = localRoot });
            }

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct)
                => Task.CompletedTask;
        }
    }
}
