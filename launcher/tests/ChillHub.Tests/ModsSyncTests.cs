// <copyright file="ModsSyncTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Два манифеста в ОДНОМ корне: сборка игры и модпак.
    /// <para>
    /// Моды ставятся прямо в папку игры — иначе BepInEx не работает: <c>winhttp.dll</c>
    /// перехватывает загрузку процесса и ищет <c>BepInEx\core</c> относительно exe. Значит
    /// в одной папке лежат файлы двух независимых владельцев, и каждая синхронизация
    /// обязана видеть только своё.
    /// </para>
    /// <para>
    /// Цена ошибки здесь несимметрична и очень велика: старое правило «удалить всё, чего
    /// нет в манифесте» при синхронизации модпака сносит десять гигабайт игры, а при
    /// синхронизации игры — полторы тысячи файлов модов. Поэтому тесты проверяют прежде
    /// всего то, что НЕ удаляется и НЕ качается.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class ModsSyncTests {
        private const string ModDll = "BepInEx/plugins/Author-Mod/Mod.dll";
        private const string Preloader = "BepInEx/core/BepInEx.Preloader.dll";
        private const string Winhttp = "winhttp.dll";
        private const string DoorstopIni = "doorstop_config.ini";

        // ---------------------------------------------------------------
        // 1. Ограниченное удаление для модпака
        // ---------------------------------------------------------------

        /// <summary>
        /// Модпак удаляет ровно разницу «было в прошлой версии — нет в новой»
        /// и не трогает ничего другого в корне.
        /// </summary>
        [Fact]
        public async Task МодпакУдаляетТолькоСвоиПропавшиеФайлы() {
            using var dir = new TempDir();
            dir.WriteFile("Lethal Company.exe", "игра");
            dir.WriteFile("Lethal Company_Data/data.pak", "игра");
            dir.WriteFile(ModDll, "мод, который остаётся");
            dir.WriteFile("BepInEx/plugins/Old-Mod/Old.dll", "мод, который выпилили");

            var options = new PlanOptions {
                Scope = ManifestScope.OwnFilesOnly,
                PreviousOwnedPaths = new[] { ModDll, "BepInEx/plugins/Old-Mod/Old.dll", Winhttp },
            };

            var keep = dir.PathTo(ModDll);
            var manifest = PlanTestData.Manifest(
                PlanTestData.File(ModDll, new FileInfo(keep).Length, TestHash.Sha256OfFile(keep)));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, options);

            // Пропал из новой версии модпака — удаляем.
            Assert.Equal(new[] { "BepInEx/plugins/Old-Mod/Old.dll" }, plan.ToDelete);

            // Игра модпаку не принадлежит: её файлов в списке нет ни при каких условиях.
            Assert.DoesNotContain("Lethal Company.exe", plan.ToDelete);
            Assert.DoesNotContain("Lethal Company_Data/data.pak", plan.ToDelete);

            // winhttp.dll числился за прошлой версией, но на диске его нет — удалять нечего.
            Assert.DoesNotContain(Winhttp, plan.ToDelete);
        }

        /// <summary>
        /// Первая установка модпака не удаляет НИЧЕГО: списка прошлой установки нет,
        /// а всё остальное в корне принадлежит игре.
        /// </summary>
        [Fact]
        public async Task ПерваяУстановкаМодпакаНичегоНеУдаляет() {
            using var dir = new TempDir();
            dir.WriteFile("Lethal Company.exe", "игра");
            dir.WriteFile("Lethal Company_Data/data.pak", "игра");
            dir.WriteFile("случайный-мусор.tmp", "мусор");

            var options = new PlanOptions { Scope = ManifestScope.OwnFilesOnly };
            var manifest = PlanTestData.Manifest(PlanTestData.File(ModDll, 10, "aa"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, options);

            Assert.Empty(plan.ToDelete);
            Assert.Single(plan.Downloads);
        }

        /// <summary>Файл, оставшийся в новой версии модпака, из прошлой не «выпиливается».</summary>
        [Fact]
        public async Task ФайлОстающийсяВНовойВерсииМодпакаНеУдаляется() {
            using var dir = new TempDir();
            var keep = dir.WriteFile(ModDll, "мод");

            var options = new PlanOptions {
                Scope = ManifestScope.OwnFilesOnly,
                PreviousOwnedPaths = new[] { ModDll },
            };
            var manifest = PlanTestData.Manifest(
                PlanTestData.File(ModDll, new FileInfo(keep).Length, TestHash.Sha256OfFile(keep)));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, options);

            Assert.Empty(plan.ToDelete);
            Assert.Empty(plan.Downloads);
        }

        /// <summary>
        /// Разделитель в сохранённом списке путей может приехать любым: список пишет
        /// клиент, читает клиент, но между записью и чтением стоит JSON и чужая ОС.
        /// «BepInEx\plugins\x.dll» и «BepInEx/plugins/x.dll» — один файл.
        /// </summary>
        [Fact]
        public async Task ПутиПрошлойУстановкиСравниваютсяБезОглядкиНаРазделительИРегистр() {
            using var dir = new TempDir();
            dir.WriteFile("BepInEx/plugins/Old-Mod/Old.dll", "мод");

            var options = new PlanOptions {
                Scope = ManifestScope.OwnFilesOnly,
                PreviousOwnedPaths = new[] { @"BepInEx\plugins\Old-Mod\OLD.DLL" },
            };

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(), dir.Root, options);

            Assert.Single(plan.ToDelete);
        }

        /// <summary>
        /// Сборка игры продолжает владеть всем корнем: без явного режима поведение
        /// остаётся прежним, иначе мусор от старых версий копился бы вечно.
        /// </summary>
        [Fact]
        public async Task СборкаИгрыПоПрежнемуУдаляетВсёЛишнее() {
            using var dir = new TempDir();
            dir.WriteFile("старьё.dll", "мусор");

            var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(), dir.Root);

            Assert.Equal(new[] { "старьё.dll" }, plan.ToDelete);
        }

        // ---------------------------------------------------------------
        // 2. Защита файлов модпака от синхронизации игры
        // ---------------------------------------------------------------

        /// <summary>
        /// Синхронизация игры не считает файлы модпака лишними.
        /// <para>
        /// Это главный тест задачи: без него первое же обновление игры удаляет
        /// установленные лаунчером моды целиком.
        /// </para>
        /// </summary>
        [Fact]
        public async Task СинхронизацияИгрыНеУдаляетФайлыМодпака() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("Lethal Company.exe", "игра");
            dir.WriteFile(Winhttp, "загрузчик модов");
            dir.WriteFile(Preloader, "BepInEx");
            dir.WriteFile(ModDll, "мод");
            dir.WriteFile("остатки-старой-версии.dll", "мусор");

            WriteModPackState(dir.Root, "1.0.0", Winhttp, Preloader, ModDll);

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("Lethal Company.exe", new FileInfo(exe).Length, TestHash.Sha256OfFile(exe)));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, PlanOptions.ForGame(dir.Root));

            // Файлы модпака не тронуты...
            Assert.DoesNotContain(Winhttp, plan.ToDelete);
            Assert.DoesNotContain(Preloader, plan.ToDelete);
            Assert.DoesNotContain(ModDll, plan.ToDelete);

            // ...а обычный мусор по-прежнему убирается: иначе тест был бы зелёным
            // просто оттого, что удалять перестали вообще всё.
            Assert.Equal(new[] { "остатки-старой-версии.dll" }, plan.ToDelete);
        }

        /// <summary>
        /// Служебные файлы модпака в корне игры не «лишние»: без
        /// <c>.mods.manifest.json</c> синхронизация игры перестаёт понимать,
        /// какие файлы принадлежат модам.
        /// </summary>
        [Fact]
        public async Task СлужебныеФайлыМодпакаНеПопадаютВУдаление() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "игра");
            WriteModPackState(dir.Root, "1.0.0", ModDll);

            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 1, "cafebabe"));
            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.DoesNotContain(IntegrityChecker.ModsVersionMarkerFileName, plan.ToDelete);
            Assert.DoesNotContain(IntegrityChecker.ModsManifestFileName, plan.ToDelete);
            Assert.Empty(plan.ToDelete);
        }

        /// <summary>
        /// Служебные файлы модпака опознаются как служебные в любой форме записи пути.
        /// Проверка отдельным тестом: именно она защищает оба файла от ОБЕИХ синхронизаций.
        /// </summary>
        [Theory]
        [InlineData(".mods.version")]
        [InlineData(".MODS.VERSION")]
        [InlineData(".mods.manifest.json")]
        [InlineData("/.mods.manifest.json")]
        public void ФайлыСостоянияМодпакаСчитаютсяСлужебными(string rel) {
            Assert.True(SimpleSyncService.IsServiceRelFile(rel));
        }

        /// <summary>
        /// Тот же путь есть и в манифесте игры (так выглядит миграция со сборок,
        /// где BepInEx зашит внутрь): владелец один, и это модпак. Файл не качается —
        /// иначе сборка затёрла бы мод своей версией.
        /// </summary>
        [Fact]
        public async Task ФайлМодпакаНеКачаетсяДажеЕслиОнЕстьВМанифестеИгры() {
            using var dir = new TempDir();
            dir.WriteFile(Winhttp, "загрузчик модов от модпака");
            WriteModPackState(dir.Root, "1.0.0", Winhttp);

            // В манифесте игры тот же путь с ДРУГИМ содержимым: без защиты он приехал бы
            // на диск поверх файла модпака.
            var manifest = PlanTestData.Manifest(
                PlanTestData.File(Winhttp, 999, "aabbccdd"),
                PlanTestData.File("game.exe", 10, "cafebabe"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, PlanOptions.ForGame(dir.Root));

            Assert.DoesNotContain(plan.Downloads, d => d.RelativePath == Winhttp);
            Assert.Contains(plan.Downloads, d => d.RelativePath == "game.exe");
            Assert.Empty(plan.ToDelete);
        }

        /// <summary>Список чужих путей уезжает в план — им пользуется фаза завершения.</summary>
        [Fact]
        public async Task ПланНесётСписокЧужихПутей() {
            using var dir = new TempDir();
            dir.WriteFile(ModDll, "мод");
            WriteModPackState(dir.Root, "1.0.0", ModDll);

            var plan = await PlanTestData.PlanAsync(
                PlanTestData.Manifest(), dir.Root, PlanOptions.ForGame(dir.Root));

            Assert.Equal(new[] { ModDll }, plan.ForeignPaths);
        }

        /// <summary>
        /// Второй рубеж: даже если чужой путь каким-то образом оказался в ToDelete,
        /// фаза завершения его не удаляет. FinishPlan статичен и настроек плана не
        /// видит, поэтому список едет внутри самого плана.
        /// </summary>
        [Fact]
        public void ФазаЗавершенияНеУдаляетФайлЧужогоМанифеста() {
            using var dir = new TempDir();
            dir.WriteFile(ModDll, "мод");
            dir.WriteFile("мусор.tmp", "мусор");

            var plan = new DiffPlan {
                GameId = "lethal-company",
                Version = "1.0.0",
                LocalRoot = dir.Root,
                ToDelete = new List<string> { ModDll, "мусор.tmp" },
                ForeignPaths = new List<string> { ModDll },
            };

            SimpleSyncService.FinishPlan(plan, new ConcurrentBag<string>(), CancellationToken.None);

            Assert.True(File.Exists(dir.PathTo(ModDll)), "файл модпака удалён фазой завершения");
            Assert.False(File.Exists(dir.PathTo("мусор.tmp")), "обычный лишний файл не удалён");
        }

        /// <summary>
        /// Служебные файлы лаунчера фаза завершения не удаляет, даже получив их в плане:
        /// потеря `.mods.manifest.json` превращает моды в «лишние файлы» на следующем же
        /// обновлении игры.
        /// </summary>
        [Theory]
        [InlineData(".mods.version")]
        [InlineData(".mods.manifest.json")]
        [InlineData(".version")]
        public void ФазаЗавершенияНеУдаляетСлужебныеФайлы(string rel) {
            using var dir = new TempDir();
            dir.WriteFile(rel, "состояние");

            var plan = new DiffPlan {
                GameId = "lethal-company",
                Version = "1.0.0",
                LocalRoot = dir.Root,
                ToDelete = new List<string> { rel },
            };

            SimpleSyncService.FinishPlan(plan, new ConcurrentBag<string>(), CancellationToken.None);

            Assert.True(File.Exists(dir.PathTo(rel)), $"служебный файл {rel} удалён фазой завершения");
        }

        // ---------------------------------------------------------------
        // 3. Второй маркер версии и копия манифеста модпака
        // ---------------------------------------------------------------

        /// <summary>Версия модпака пишется и читается отдельно от версии сборки игры.</summary>
        [Fact]
        public void ВерсияМодпакаЖивётРядомСВерсиейИгрыИНеПутаетсяСНей() {
            using var games = new GamesPathScope();

            Assert.True(GameLocalState.WriteLocalVersion("lethal-company", "1.0.7"));
            Assert.True(GameLocalState.WriteLocalModsVersion("lethal-company", "2.2.12"));

            Assert.Equal("1.0.7", GameLocalState.ReadLocalVersion("lethal-company"));
            Assert.Equal("2.2.12", GameLocalState.ReadLocalModsVersion("lethal-company"));

            var root = Path.Combine(games.Root, "lethal-company");
            Assert.True(File.Exists(Path.Combine(root, IntegrityChecker.VersionMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(root, IntegrityChecker.ModsVersionMarkerFileName)));
        }

        /// <summary>Без маркера модпак считается неустановленным, а не «версия пустая».</summary>
        [Fact]
        public void БезМаркераВерсияМодпакаПустая() {
            using var dir = new TempDir();

            Assert.Equal(string.Empty, GameLocalState.ReadModsVersionAt(dir.Root));
        }

        /// <summary>
        /// Маркер версии модпака адресуется и корнем, а не только идентификатором игры:
        /// моды ставятся и в Steam-копию, у которой gameId в пути нет.
        /// </summary>
        [Fact]
        public void ВерсияМодпакаПишетсяПоКорнюПапки() {
            using var dir = new TempDir();
            var root = Path.Combine(dir.Root, "steamapps", "common", "Lethal Company");

            Assert.True(GameLocalState.WriteModsVersionAt(root, " 2.2.12 \r\n"));

            // Краевые пробелы отбрасываются: файл правят руками, а «2.2.12\r\n»
            // не совпало бы со строкой версии с сервера.
            Assert.Equal("2.2.12", GameLocalState.ReadModsVersionAt(root));
        }

        /// <summary>Копия манифеста модпака читается обратно как список путей.</summary>
        [Fact]
        public void КопияМанифестаМодпакаДаётСписокЕгоПутей() {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(
                PlanTestData.File(Winhttp, 1, "aa"),
                PlanTestData.File(@"BepInEx\core\BepInEx.Preloader.dll", 2, "bb"));

            Assert.True(GameLocalState.WriteInstalledModPackManifest(dir.Root, manifest));

            var paths = GameLocalState.ReadInstalledModPackPaths(dir.Root);

            // Пути нормализованы к форме манифеста — иначе списки владения не сойдутся.
            Assert.Equal(new[] { Winhttp, Preloader }, paths);
            Assert.Equal(manifest.Version, GameLocalState.ReadInstalledModPackManifest(dir.Root)!.Version);
        }

        /// <summary>Модпака нет — пустой список, а не исключение.</summary>
        [Fact]
        public void БезКопииМанифестаСписокПутейПуст() {
            using var dir = new TempDir();

            Assert.Empty(GameLocalState.ReadInstalledModPackPaths(dir.Root));
            Assert.Null(GameLocalState.ReadInstalledModPackManifest(dir.Root));
        }

        /// <summary>
        /// Битая копия манифеста трактуется как «модпака нет».
        /// <para>
        /// Это осознанно безопасный отказ в сторону сохранности: без списка путей
        /// синхронизация игры сочтёт моды лишними — но она СПРОСИТ перед удалением,
        /// тогда как падение здесь оставило бы игру вообще без обновлений.
        /// </para>
        /// </summary>
        [Fact]
        public void БитаяКопияМанифестаНеРоняетЧтение() {
            using var dir = new TempDir();
            dir.WriteFile(IntegrityChecker.ModsManifestFileName, "{ это вообще не json ");

            Assert.Empty(GameLocalState.ReadInstalledModPackPaths(dir.Root));
        }

        /// <summary>
        /// Настройки плана собираются из того, что лежит на диске: игре — чужие пути,
        /// модпаку — его же прошлая установка. Один и тот же файл, две разные роли.
        /// </summary>
        [Fact]
        public void НастройкиПланаБерутПутиИзУстановленногоМанифестаМодпака() {
            using var dir = new TempDir();
            WriteModPackState(dir.Root, "2.2.12", Winhttp, ModDll);

            var forGame = PlanOptions.ForGame(dir.Root);
            Assert.Equal(ManifestScope.WholeRoot, forGame.Scope);
            Assert.Equal(new[] { Winhttp, ModDll }, forGame.ForeignPaths);

            var forMods = PlanOptions.ForModPack(dir.Root);
            Assert.Equal(ManifestScope.OwnFilesOnly, forMods.Scope);
            Assert.Equal(new[] { Winhttp, ModDll }, forMods.PreviousOwnedPaths);
        }

        // ---------------------------------------------------------------
        // 4. «Установлена ли игра» при установленном модпаке
        // ---------------------------------------------------------------

        /// <summary>
        /// В корне только моды и служебные файлы — игра НЕ установлена.
        /// <para>
        /// Иначе полторы тысячи файлов BepInEx выдают игру за установленную: на кнопке
        /// пишется «Играть», а запускать нечего, и проверка целостности идёт сверять
        /// с манифестом папку, в которой файлов игры нет вовсе.
        /// </para>
        /// </summary>
        [Fact]
        public void ТолькоФайлыМодпакаНеДелаютИгруУстановленной() {
            using var dir = new TempDir();
            dir.WriteFile(Winhttp, "загрузчик модов");
            dir.WriteFile(Preloader, "BepInEx");
            dir.WriteFile(ModDll, "мод");
            dir.WriteFile(IntegrityChecker.VersionMarkerFileName, string.Empty);
            WriteModPackState(dir.Root, "2.2.12", Winhttp, Preloader, ModDll);

            Assert.False(IntegrityChecker.HasAnyLocalGameFiles(dir.Root));
        }

        /// <summary>Появился хоть один файл игры — игра установлена, моды тут ни при чём.</summary>
        [Fact]
        public void ФайлИгрыРядомСМодамиДелаетИгруУстановленной() {
            using var dir = new TempDir();
            dir.WriteFile(Winhttp, "загрузчик модов");
            dir.WriteFile(ModDll, "мод");
            WriteModPackState(dir.Root, "2.2.12", Winhttp, ModDll);
            dir.WriteFile("Lethal Company.exe", "игра");

            Assert.True(IntegrityChecker.HasAnyLocalGameFiles(dir.Root));
        }

        /// <summary>Без модпака ответ прежний: любой файл в папке — признак установки.</summary>
        [Fact]
        public void БезМодпакаПризнакУстановкиНеИзменился() {
            using var dir = new TempDir();
            dir.WriteFile(IntegrityChecker.VersionMarkerFileName, "1.0.0");
            Assert.False(IntegrityChecker.HasAnyLocalGameFiles(dir.Root));

            dir.WriteFile("game.exe", "игра");
            Assert.True(IntegrityChecker.HasAnyLocalGameFiles(dir.Root));
        }

        // ---------------------------------------------------------------
        // 5. Кеш хешей, общий на корень
        // ---------------------------------------------------------------

        /// <summary>
        /// Прополка кеша не выбрасывает записи ЧУЖОГО манифеста.
        /// <para>
        /// Кеш ключуется относительным путём и лежит один на игру, то есть записи обоих
        /// манифестов в нём вперемешку. Синхронизация игры файлов модпака «не видит» — и
        /// если объявить их исчезнувшими, прополка снесёт их записи, а следующая
        /// синхронизация модов пересчитает с диска все свои гигабайты заново.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПрополкаКешаНеТрогаетЗаписиЧужогоМанифеста() {
            using var dir = new TempDir();
            using var scope = new HashCacheScope("mods");

            var exe = dir.WriteFile("game.exe", "игра");
            var mod = dir.WriteFile(ModDll, "мод");
            WriteModPackState(dir.Root, "2.2.12", ModDll);

            var modInfo = new FileInfo(mod);
            var modSha = TestHash.Sha256OfFile(mod);
            var modB3 = TestHash.Blake3OfFile(mod);

            // Так выглядит кеш после синхронизации МОДПАКА: в нём есть запись о его файле.
            var before = FileHashCache.Load(scope.GameId, dir.Root);
            before.Set(ModDll, modInfo.Length, modInfo.LastWriteTimeUtc.Ticks, modSha, modB3);
            before.PruneAndSave(new List<string> { ModDll });

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(exe).Length, TestHash.Sha256OfFile(exe)));
            manifest.GameId = scope.GameId;

            await PlanTestData.PlanAsync(manifest, dir.Root, PlanOptions.ForGame(dir.Root), keepCache: true);

            var after = FileHashCache.Load(scope.GameId, dir.Root);
            Assert.True(
                after.TryGet(ModDll, modInfo.Length, modInfo.LastWriteTimeUtc.Ticks, out var sha, out _),
                "синхронизация игры выбросила из кеша записи модпака");
            Assert.Equal(modSha, sha);
        }

        /// <summary>
        /// Запись о файле, которого больше нет НИ У КОГО, по-прежнему уходит:
        /// защита чужих записей не должна превращать кеш в свалку.
        /// </summary>
        [Fact]
        public async Task ПрополкаКешаПоПрежнемуУбираетЗаписиИсчезнувшихФайлов() {
            using var dir = new TempDir();
            using var scope = new HashCacheScope("mods");

            var exe = dir.WriteFile("game.exe", "игра");
            WriteModPackState(dir.Root, "2.2.12", ModDll);

            var stale = FileHashCache.Load(scope.GameId, dir.Root);
            stale.Set("исчез.dll", 10, 20, new string('a', 64), new string('b', 64));
            stale.PruneAndSave(new List<string> { "исчез.dll" });

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(exe).Length, TestHash.Sha256OfFile(exe)));
            manifest.GameId = scope.GameId;

            await PlanTestData.PlanAsync(manifest, dir.Root, PlanOptions.ForGame(dir.Root), keepCache: true);

            Assert.False(FileHashCache.Load(scope.GameId, dir.Root).TryGet("исчез.dll", 10, 20, out _, out _));
        }

        /// <summary>
        /// Кладёт в корень состояние установленного модпака: маркер версии и копию
        /// манифеста — ровно то, что после установки модов пишет лаунчер.
        /// </summary>
        /// <param name="root">Корень папки игры.</param>
        /// <param name="version">Версия модпака.</param>
        /// <param name="paths">Пути, которыми модпак владеет.</param>
        private static void WriteModPackState(string root, string version, params string[] paths) {
            var manifest = PlanTestData.Manifest(
                paths.Select((p, i) => PlanTestData.File(p, i + 1, new string('a', 64))).ToArray());
            manifest.Version = version;

            GameLocalState.WriteModsVersionAt(root, version);
            GameLocalState.WriteInstalledModPackManifest(root, manifest);
        }

        // ---------------------------------------------------------------
        // Файлы, которые правит сам лаунчер
        // ---------------------------------------------------------------

        /// <summary>
        /// Файл из preserve-списка, который уже лежит на диске, не перекачивается —
        /// даже когда его содержимое разошлось с манифестом.
        /// <para>
        /// Это ровно случай <c>doorstop_config.ini</c>. Он перечислен в манифесте
        /// модпака, но значение ключа в нём меняет сам лаунчер, переключая
        /// «с модами / без модов». Без исключения каждая ванильная сессия оставляла
        /// бы после себя «повреждённый» файл: проверка целостности предлагала бы
        /// починку исправной установки, а очередное обновление возвращало бы моды
        /// во включённое состояние молча, за спиной у игрока.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ФайлИзPreserveСпискаНеПерекачиваетсяПриРасхождении() {
            using var dir = new TempDir();
            dir.WriteFile(DoorstopIni, "enabled = false");

            // Манифест описывает ДРУГОЕ содержимое того же пути.
            var manifest = PlanTestData.Manifest(
                PlanTestData.File(DoorstopIni, 64, HashOf(dir, "эталонный ini")));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, PlanOptions.ForModPack(dir.Root));

            Assert.Empty(plan.Downloads);
            Assert.Empty(plan.ToDelete);
        }

        /// <summary>
        /// Отсутствующий preserve-файл всё-таки ставится: без doorstop_config.ini
        /// моды не запустятся вовсе, и «не трогать» тут значило бы «не установить».
        /// </summary>
        [Fact]
        public async Task ОтсутствующийФайлИзPreserveСпискаВсёЖеСтавится() {
            using var dir = new TempDir();
            dir.WriteFile(ModDll, "мод");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File(DoorstopIni, 64, HashOf(dir, "эталонный ini")));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, PlanOptions.ForModPack(dir.Root));

            Assert.Single(plan.Downloads);
            Assert.Equal(DoorstopIni, plan.Downloads[0].RelativePath);
        }

        /// <summary>
        /// Для синхронизации ИГРЫ preserve-списка нет: обычный файл сборки, разошедшийся
        /// с манифестом, обязан скачаться заново, как и раньше.
        /// </summary>
        [Fact]
        public async Task ДляСборкиИгрыPreserveСписокНеДействует() {
            using var dir = new TempDir();
            dir.WriteFile(DoorstopIni, "испорчено");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File(DoorstopIni, 64, HashOf(dir, "эталон сборки")));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root, PlanOptions.ForGame(dir.Root));

            Assert.Single(plan.Downloads);
        }


        /// <summary>Хеш содержимого, которого на диске нет: эталон для расхождения.</summary>
        /// <param name="dir">Временный каталог теста.</param>
        /// <param name="content">Эталонное содержимое.</param>
        /// <returns>SHA-256 в шестнадцатеричном виде.</returns>
        private static string HashOf(TempDir dir, string content) {
            var probe = System.IO.Path.Combine(dir.Root, ".probe");
            System.IO.File.WriteAllText(probe, content);
            var hash = TestHash.Sha256OfFile(probe);
            System.IO.File.Delete(probe);
            return hash;
        }

    }
}
