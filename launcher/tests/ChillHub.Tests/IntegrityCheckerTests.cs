// <copyright file="IntegrityCheckerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Проверка целостности — то, чем пользователь чинит игру, когда та не запускается.
    /// <para>
    /// Ошибка здесь стоит дорого в обе стороны: если проверка считает целую игру битой,
    /// человек качает гигабайты заново; если считает битую целой — он остаётся с
    /// неработающей игрой и без объяснения. Сеть не задействована: манифест подставляется
    /// заглушкой, план строит настоящий планировщик по локальным файлам.
    /// </para>
    /// </summary>
    public class IntegrityCheckerTests {
        /// <summary>URL манифеста собирается из базы, идентификатора и версии.</summary>
        [Fact]
        public void СсылкаНаМанифестСобираетсяИзБазыИВерсии() {
            Assert.Equal(
                "https://launcher.samoy.love/manifests/lethal-company/1.2.3.json",
                IntegrityChecker.ManifestUrl("https://launcher.samoy.love", "lethal-company", "1.2.3"));
        }

        /// <summary>
        /// Лишний слеш в базе не должен давать «//manifests»: адрес уходит в HTTP как есть,
        /// а nginx на такой путь отвечает 404 — проверка падала бы «манифест недоступен».
        /// </summary>
        [Theory]
        [InlineData("https://launcher.samoy.love/")]
        [InlineData("https://launcher.samoy.love///")]
        public void ЗавершающийСлешВБазеНеУдваивается(string apiBase) {
            Assert.DoesNotContain("//manifests", IntegrityChecker.ManifestUrl(apiBase, "g", "1.0.0"), StringComparison.Ordinal);
            Assert.DoesNotContain("//content", IntegrityChecker.ContentBaseUrl(apiBase, "g", "1.0.0"), StringComparison.Ordinal);
        }

        /// <summary>База контента указывает на файлы конкретной версии.</summary>
        [Fact]
        public void БазаКонтентаУказываетНаФайлыВерсии() {
            Assert.Equal(
                "https://x.invalid/content/g/1.0.0/files",
                IntegrityChecker.ContentBaseUrl("https://x.invalid", "g", "1.0.0"));
        }

        /// <summary>Пустые аргументы не должны ронять построение пути — путь просто вырождается.</summary>
        [Fact]
        public void ПустыеАргументыПутиНеРоняют() {
            Assert.NotNull(IntegrityChecker.GameLocalRoot(null, null));
            Assert.NotNull(IntegrityChecker.ManifestUrl(null!, string.Empty, string.Empty));
        }

        /// <summary>Корень игры — это подпапка внутри общей папки игр.</summary>
        [Fact]
        public void КореньИгрыЛежитВнутриПапкиИгр() {
            var root = IntegrityChecker.GameLocalRoot(@"D:\Games\ChillHub", "lethal-company");
            Assert.Equal(Path.Combine(@"D:\Games\ChillHub", "lethal-company"), root);
        }

        /// <summary>Несуществующая папка — игра не установлена, а не исключение.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойКореньСчитаетсяНеустановленнойИгрой(string root) {
            Assert.False(IntegrityChecker.HasAnyLocalGameFiles(root));
        }

        /// <summary>Отсутствующий каталог тоже означает «не установлено».</summary>
        [Fact]
        public void ОтсутствующийКаталогСчитаетсяНеустановленнойИгрой() {
            using var dir = new TempDir();
            Assert.False(IntegrityChecker.HasAnyLocalGameFiles(dir.PathTo("нет-такой-папки")));
        }

        /// <summary>Пустая папка — не установленная игра.</summary>
        [Fact]
        public void ПустаяПапкаСчитаетсяНеустановленнойИгрой() {
            using var dir = new TempDir();
            Assert.False(IntegrityChecker.HasAnyLocalGameFiles(dir.Root));
        }

        /// <summary>
        /// Служебные файлы не делают игру установленной. Иначе после прерванной установки
        /// (осталась .staging и маркер) проверка бы уверяла, что игра на месте, а запускать
        /// было бы нечего.
        /// </summary>
        [Fact]
        public void ТолькоСлужебныеФайлыНеДелаютИгруУстановленной() {
            using var dir = new TempDir();
            dir.WriteFile(IntegrityChecker.VersionMarkerFileName, "1.0.0");
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "{}");
            dir.WriteFile(".staging/partial.bin", "недокачано");

            Assert.False(IntegrityChecker.HasAnyLocalGameFiles(dir.Root));
        }

        /// <summary>Один настоящий файл — уже установленная игра, в том числе в подкаталоге.</summary>
        [Theory]
        [InlineData("game.exe")]
        [InlineData("data/pack.bin")]
        [InlineData("very/deep/nested/file.dat")]
        public void ОдинНастоящийФайлДелаетИгруУстановленной(string rel) {
            using var dir = new TempDir();
            dir.WriteFile(IntegrityChecker.VersionMarkerFileName, "1.0.0");
            dir.WriteFile(rel, "данные");

            Assert.True(IntegrityChecker.HasAnyLocalGameFiles(dir.Root));
        }

        /// <summary>Игра без идентификатора — понятное сообщение, а не NullReference.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task ПроверкаБезИдентификатораИгрыДаётПонятнуюОшибку(string? gameId) {
            var ex = await Assert.ThrowsAsync<IntegrityCheckException>(() => IntegrityChecker.CheckAsync(
                new StubSync(), "https://x.invalid", gameId!, "1.0.0", @"C:\Games", null, CancellationToken.None));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        /// <summary>
        /// У игры нет опубликованной версии — сверять не с чем. Это отдельное сообщение:
        /// «не установлена» тут ввело бы в заблуждение, файлы-то на месте.
        /// </summary>
        [Fact]
        public async Task ПроверкаБезВерсииДаётОтдельноеСообщение() {
            var ex = await Assert.ThrowsAsync<IntegrityCheckException>(() => IntegrityChecker.CheckAsync(
                new StubSync(), "https://x.invalid", "g", string.Empty, @"C:\Games", null, CancellationToken.None));
            Assert.Contains("версии", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Путь к папке игры не должен попадать в текст, который увидит пользователь:
        /// он уезжает в скриншоты и в автоотчёты вместе с именем пользователя Windows.
        /// </summary>
        [Fact]
        public async Task СообщениеОНеустановленнойИгреНеСодержитПуть() {
            using var dir = new TempDir();
            var ex = await Assert.ThrowsAsync<IntegrityCheckException>(() => IntegrityChecker.CheckAsync(
                new StubSync(), "https://x.invalid", "g", "1.0.0", dir.Root, null, CancellationToken.None));

            Assert.DoesNotContain(dir.Root, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Недоступный манифест — ошибка с причиной, а не голое исключение сети.</summary>
        [Fact]
        public async Task НедоступныйМанифестОборачиваетсяВПонятнуюОшибку() {
            using var dir = new TempDir();
            dir.WriteFile("g/game.exe", "данные");

            var sync = new StubSync { ManifestError = new HttpRequestException("сервер недоступен") };
            var ex = await Assert.ThrowsAsync<IntegrityCheckException>(() => IntegrityChecker.CheckAsync(
                sync, "https://x.invalid", "g", "1.0.0", dir.Root, null, CancellationToken.None));

            Assert.Contains("сервер недоступен", ex.Message, StringComparison.Ordinal);
            Assert.IsType<HttpRequestException>(ex.InnerException);
        }

        /// <summary>Отмена остаётся отменой и не превращается в ошибку проверки.</summary>
        [Fact]
        public async Task ОтменаНеПревращаетсяВОшибкуПроверки() {
            using var dir = new TempDir();
            dir.WriteFile("g/game.exe", "данные");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var sync = new StubSync { ManifestError = new OperationCanceledException() };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IntegrityChecker.CheckAsync(
                sync, "https://x.invalid", "g", "1.0.0", dir.Root, null, cts.Token));
        }

        /// <summary>Целая установка: ничего не отсутствует, ничего не повреждено.</summary>
        [Fact]
        public async Task ЦелаяУстановкаПризнаётсяИсправной() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path)));

            var report = await CheckAsync(manifest, dir.Root);

            Assert.True(report.IsOk);
            Assert.False(report.NeedsRepair);
            Assert.Equal(0, report.MissingFiles);
            Assert.Equal(0, report.CorruptedFiles);
            Assert.Equal(1, report.TotalFiles);
        }

        /// <summary>
        /// Отсутствующий и испорченный файлы считаются РАЗДЕЛЬНО. Различие видно человеку:
        /// «повреждено 300» после отключения питания — совсем не то же самое, что
        /// «отсутствует 300» после того, как антивирус вычистил папку.
        /// </summary>
        [Fact]
        public async Task ОтсутствующиеИИспорченныеФайлыСчитаютсяРаздельно() {
            using var dir = new TempDir();
            var good = dir.WriteFile("ok.dat", "цел");
            var broken = dir.WriteFile("broken.dat", "оригинал");
            var expectedBroken = TestHash.Sha256OfFile(broken);
            dir.WriteFile("broken.dat", "испорчено");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("ok.dat", new FileInfo(good).Length, TestHash.Sha256OfFile(good)),
                PlanTestData.File("broken.dat", 8, expectedBroken),
                PlanTestData.File("gone.dat", 100, "00"));

            var report = await CheckAsync(manifest, dir.Root);

            Assert.Equal(1, report.CorruptedFiles);
            Assert.Equal(1, report.MissingFiles);
            Assert.Equal(3, report.TotalFiles);
            Assert.False(report.IsOk);
            Assert.True(report.NeedsRepair);
        }

        /// <summary>
        /// Лишние файлы не делают установку «неисправной»: пользователь мог положить туда
        /// сейв или мод, и пугать его красным статусом из-за этого нельзя. Но в отчёте они видны.
        /// </summary>
        [Fact]
        public async Task ЛишниеФайлыВидныНоНеДелаютУстановкуНеисправной() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое");
            dir.WriteFile("моя-заметка.txt", "лишний файл");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path)));

            var report = await CheckAsync(manifest, dir.Root);

            Assert.True(report.IsOk, "лишний файл — не повреждение");
            Assert.True(report.ExtraFiles > 0);
        }

        /// <summary>
        /// Прерванное обновление делает установку неисправной даже при совпавших хешах:
        /// маркер означает, что часть файлов могла не доехать из staging.
        /// </summary>
        [Fact]
        public async Task ПрерванноеОбновлениеДелаетУстановкуНеисправной() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое");
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "{}");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path)));

            var report = await CheckAsync(manifest, dir.Root);

            Assert.True(report.HasUnfinishedUpdate);
            Assert.False(report.IsOk);
        }

        /// <summary>
        /// Служебные записи манифеста в счёт файлов не идут: иначе «проверено 2801»
        /// не сойдётся с числом файлов, которое человек видит в проводнике.
        /// </summary>
        [Fact]
        public async Task СлужебныеЗаписиМанифестаНеПопадаютВСчёт() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое");

            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(path).Length, TestHash.Sha256OfFile(path)),
                PlanTestData.File(SimpleSyncService.UpdateMarkerFileName, 2, "00"),
                PlanTestData.File("freetp/.hash", 2, "00"));

            var report = await CheckAsync(manifest, dir.Root);

            Assert.Equal(1, report.TotalFiles);
        }

        /// <summary>Описание исправной установки не пугает пользователя словом «проблемы».</summary>
        [Fact]
        public void ОписаниеИсправнойУстановкиГоворитЧтоВсёВПорядке() {
            var text = IntegrityChecker.Describe(new IntegrityReport { TotalFiles = 10, Version = "1.0.0" });
            Assert.Contains("Всё в порядке", text, StringComparison.Ordinal);
            Assert.Contains("1.0.0", text, StringComparison.Ordinal);
        }

        /// <summary>В описании перечислено то, что действительно нашли, и ничего лишнего.</summary>
        [Fact]
        public void ОписаниеПеречисляетТолькоНайденныеПроблемы() {
            var text = IntegrityChecker.Describe(new IntegrityReport {
                TotalFiles = 10,
                Version = "1.0.0",
                MissingFiles = 2,
                CorruptedFiles = 0,
                ExtraFiles = 3,
            });

            Assert.Contains("отсутствует — 2", text, StringComparison.Ordinal);
            Assert.Contains("лишних — 3", text, StringComparison.Ordinal);
            Assert.DoesNotContain("повреждено", text, StringComparison.Ordinal);
        }

        /// <summary>Про прерванное обновление сказано отдельно — это другая причина и другое лечение.</summary>
        [Fact]
        public void ОписаниеУпоминаетПрерванноеОбновление() {
            var text = IntegrityChecker.Describe(new IntegrityReport { TotalFiles = 1, HasUnfinishedUpdate = true });
            Assert.Contains("прервано", text, StringComparison.Ordinal);
        }

        /// <summary>Отчёта нет — пустая строка, а не падение диалога.</summary>
        [Fact]
        public void ОписаниеОтсутствующегоОтчётаПусто() {
            Assert.Equal(string.Empty, IntegrityChecker.Describe(null!));
        }

        /// <summary>
        /// У игры нет модпака — второго прохода не происходит, и отчёт остаётся ровно
        /// тем же, что был до модов: ни лишнего поля, ни лишней строки в тексте.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ИграБезМодпакаПроверяетсяКакРаньше(bool emptyCard) {
            using var dir = new TempDir();
            var sync = new StubSync();
            var before = new IntegrityReport { TotalFiles = 10, Version = "1.0.0" };

            var after = await IntegrityChecker.CheckModsAsync(
                sync, "https://x.invalid", emptyCard ? new ModsInfo() : null, dir.Root, before, null, CancellationToken.None);

            Assert.Same(before, after);
            Assert.False(after.HasMods);
            Assert.Null(after.ModsPlan);
            Assert.Equal(0, sync.ManifestRequests);
            Assert.Contains("Всё в порядке", IntegrityChecker.Describe(after), StringComparison.Ordinal);
            Assert.DoesNotContain("Моды", IntegrityChecker.Describe(after), StringComparison.Ordinal);
        }

        /// <summary>
        /// Модпак объявлен, но на диск не ставился: второго прохода нет и манифест модов
        /// даже не запрашивается. Это не ошибка — моды ставятся по желанию.
        /// </summary>
        [Fact]
        public async Task НеустановленныйМодпакВторогоПроходаНеДелает() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "содержимое");

            var sync = new StubSync { ModsManifest = PlanTestData.Manifest() };
            var before = new IntegrityReport { TotalFiles = 1, Version = "1.0.0" };

            var after = await IntegrityChecker.CheckModsAsync(
                sync, "https://x.invalid", ModsCard(), dir.Root, before, null, CancellationToken.None);

            Assert.Same(before, after);
            Assert.False(after.HasMods);
            Assert.Equal(0, sync.ManifestRequests);
        }

        /// <summary>
        /// Игра с целыми модами: обе части исправны, а файлы модов не попадают в «лишние»
        /// у игры — иначе «Проверить файлы» предлагало бы удалить пару тысяч файлов BepInEx.
        /// </summary>
        [Fact]
        public async Task ЦелыеФайлыМодовПризнаютсяИсправными() {
            using var dir = new TempDir();
            var game = dir.WriteFile("game.exe", "содержимое игры");
            var mod = dir.WriteFile("BepInEx/core/BepInEx.Preloader.dll", "тело мода");

            var modsManifest = PlanTestData.Manifest(
                PlanTestData.File("BepInEx/core/BepInEx.Preloader.dll", new FileInfo(mod).Length, TestHash.Sha256OfFile(mod)));
            InstallMods(dir.Root, modsManifest);

            var gameManifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(game).Length, TestHash.Sha256OfFile(game)));

            var report = await CheckWithModsAsync(gameManifest, modsManifest, dir.Root);

            Assert.True(report.HasMods);
            Assert.Equal(1, report.ModsTotalFiles);
            Assert.Equal(0, report.ModsMissingFiles);
            Assert.Equal(0, report.ModsCorruptedFiles);
            Assert.Equal(1, report.TotalFiles);
            Assert.Equal(0, report.ExtraFiles);
            Assert.True(report.IsOk);
            Assert.False(report.NeedsRepair);
        }

        /// <summary>
        /// Испорченный файл мода виден в части «моды» и делает установку неисправной,
        /// не портя счётчиков игры: чинить надо модпак, а не переустанавливать сборку.
        /// </summary>
        [Fact]
        public async Task ИспорченныйФайлМодаВиденВЧастиМодов() {
            using var dir = new TempDir();
            var game = dir.WriteFile("game.exe", "содержимое игры");
            var mod = dir.WriteFile("BepInEx/plugins/mod.dll", "оригинал мода");
            var expected = TestHash.Sha256OfFile(mod);
            var size = new FileInfo(mod).Length;
            dir.WriteFile("BepInEx/plugins/mod.dll", "испорчено ааа");

            var modsManifest = PlanTestData.Manifest(
                PlanTestData.File("BepInEx/plugins/mod.dll", size, expected),
                PlanTestData.File("BepInEx/core/BepInEx.Preloader.dll", 10, "00"));
            InstallMods(dir.Root, modsManifest);

            var gameManifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", new FileInfo(game).Length, TestHash.Sha256OfFile(game)));

            var report = await CheckWithModsAsync(gameManifest, modsManifest, dir.Root);

            Assert.Equal(1, report.ModsCorruptedFiles);
            Assert.Equal(1, report.ModsMissingFiles);
            Assert.Equal(2, report.ModsTotalFiles);
            Assert.Equal(0, report.MissingFiles);
            Assert.Equal(0, report.CorruptedFiles);
            Assert.False(report.IsOk);
            Assert.True(report.NeedsRepair);

            var text = IntegrityChecker.Describe(report);
            Assert.Contains("Игра:", text, StringComparison.Ordinal);
            Assert.Contains("Моды:", text, StringComparison.Ordinal);
            Assert.Contains("повреждено — 1", text, StringComparison.Ordinal);
            Assert.Contains("отсутствует — 1", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// ПОРЯДОК ПОЧИНКИ: сначала модпак, потом игра. В обратном порядке игра сперва
        /// удалила бы несколько гигабайт файлов модов, а модпак тут же скачал бы их
        /// заново — оплаченный игроком трафик за ничью.
        /// </summary>
        [Fact]
        public async Task ПочинкаИдётСначалаПоМодамПотомПоИгре() {
            using var dir = new TempDir();
            var sync = new RepairRecordingSync();

            await IntegrityChecker.RepairAsync(sync, RepairReport(dir.Root), null, CancellationToken.None);

            Assert.Equal(new[] { "mods-2.2.12", "1.0.0" }, sync.Executed);

            // Починка модов обязана переписать принадлежность: без этого следующая
            // синхронизация игры сочтёт свежие файлы модов мусором.
            Assert.Equal("ASTeam-LethalReloaded-2.2.12", GameLocalState.ReadModsVersionAt(dir.Root));
            Assert.Contains("BepInEx/core/x.dll", GameLocalState.ReadInstalledModPackPaths(dir.Root));
        }

        /// <summary>
        /// План игры строился до починки модов, поэтому свежий файл мода в нём — «лишний».
        /// Починка обязана вывести файлы модпака из-под плана игры, иначе она чинит моды
        /// и тут же их сносит.
        /// </summary>
        [Fact]
        public async Task ПочинкаИгрыНеТрогаетТолькоЧтоПочиненныеМоды() {
            using var dir = new TempDir();
            var report = RepairReport(dir.Root);
            report.Plan.ToDelete.Add("BepInEx/core/x.dll");

            var sync = new RepairRecordingSync();
            await IntegrityChecker.RepairAsync(sync, report, null, CancellationToken.None);

            Assert.DoesNotContain("BepInEx/core/x.dll", report.Plan.ToDelete);
            Assert.Contains("BepInEx/core/x.dll", report.Plan.ForeignPaths);
        }

        /// <summary>Карточка модпака в том виде, в каком её присылает сервер: адреса относительные.</summary>
        private static ModsInfo ModsCard() => new ModsInfo {
            HasLatest = true,
            Version = "ASTeam-LethalReloaded-2.2.12",
            ManifestUrl = "/manifests/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12.json",
            ContentBaseUrl = "/content/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12/files",
        };

        // Отмечает модпак установленным: маркер версии и копия манифеста. Ровно это
        // пишет ModsService после установки, и ровно по этим двум файлам проверка
        // понимает, что второй проход имеет смысл.
        private static void InstallMods(string root, Manifest manifest) {
            GameLocalState.WriteInstalledModPackManifest(root, manifest);
            GameLocalState.WriteModsVersionAt(root, ModsCard().Version);
        }

        // Отчёт с обоими планами: оба непустые, чтобы порядок выполнения был виден.
        private static IntegrityReport RepairReport(string root) {
            var gamePlan = new DiffPlan { GameId = "lethal-company", Version = "1.0.0", LocalRoot = root };
            gamePlan.Downloads.Add(new FileTask { RelativePath = "game.exe" });

            var modsPlan = new DiffPlan { GameId = "lethal-company", Version = "mods-2.2.12", LocalRoot = root };
            modsPlan.Downloads.Add(new FileTask { RelativePath = "BepInEx/core/x.dll" });

            return new IntegrityReport {
                Plan = gamePlan,
                Version = "1.0.0",
                ModsPlan = modsPlan,
                ModsManifest = PlanTestData.Manifest(PlanTestData.File("BepInEx/core/x.dll", 10, "00")),
                ModsVersion = ModsCard().Version,
            };
        }

        // Прогоняет оба прохода подряд — так же, как это делает страница игры.
        private static async Task<IntegrityReport> CheckWithModsAsync(Manifest game, Manifest mods, string localRoot) {
            var trimmed = localRoot.TrimEnd(Path.DirectorySeparatorChar);
            var sync = new StubSync { Manifest = game, ModsManifest = mods };
            try {
                var report = await IntegrityChecker.CheckAsync(
                    sync,
                    "https://x.invalid",
                    Path.GetFileName(trimmed),
                    "1.0.0",
                    Path.GetDirectoryName(trimmed)!,
                    null,
                    CancellationToken.None);

                return await IntegrityChecker.CheckModsAsync(
                    sync, "https://x.invalid", ModsCard(), localRoot, report, null, CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(game.GameId);
                FileHashCache.Remove(mods.GameId);
            }
        }

        // Прогоняет проверку по локальной папке с подставленным манифестом.
        // CheckAsync складывает путь сам, как GamesPath + gameId, поэтому идентификатор
        // берём из имени временной папки — тогда сложенный путь совпадёт с ней.
        private static async Task<IntegrityReport> CheckAsync(Manifest manifest, string localRoot) {
            var trimmed = localRoot.TrimEnd(Path.DirectorySeparatorChar);
            try {
                return await IntegrityChecker.CheckAsync(
                    new StubSync { Manifest = manifest },
                    "https://x.invalid",
                    Path.GetFileName(trimmed),
                    "1.0.0",
                    Path.GetDirectoryName(trimmed)!,
                    null,
                    CancellationToken.None);
            }
            finally {
                FileHashCache.Remove(manifest.GameId);
            }
        }

        /// <summary>
        /// Отдаёт заранее подготовленный манифест (или заданную ошибку) вместо похода в сеть;
        /// планирование при этом настоящее — именно его результат и проверяется.
        /// </summary>
        private sealed class StubSync : ISyncService {
            public Manifest Manifest { get; init; } = new Manifest();

            /// <summary>Манифест модпака: отдаётся на адреса модов, чтобы проходы не путались.</summary>
            public Manifest? ModsManifest { get; init; }

            public Exception? ManifestError { get; init; }

            /// <summary>Сколько раз спрашивали манифест: «второго прохода не было» — это ноль запросов.</summary>
            public int ManifestRequests { get; private set; }

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
                this.ManifestRequests++;
                if (this.ManifestError != null) {
                    return Task.FromException<Manifest>(this.ManifestError);
                }

                if (this.ModsManifest != null && manifestUrl.Contains("_mods", StringComparison.Ordinal)) {
                    return Task.FromResult(this.ModsManifest);
                }

                return Task.FromResult(this.Manifest);
            }

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => this.PlanAsync(manifest, localRoot, contentBaseUrl, PlanOptions.Default, ct);

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => new SimpleSyncService(new HttpClient()).PlanAsync(manifest, localRoot, contentBaseUrl, options, ct);

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct)
                => throw new NotSupportedException("проверка целостности ничего не скачивает");
        }

        /// <summary>
        /// Запоминает, в каком порядке чинили. Ничего не качает: проверяется решение
        /// «сначала моды», а не работа движка синхронизации — у него свои тесты.
        /// </summary>
        private sealed class RepairRecordingSync : ISyncService {
            /// <summary>Версии планов в порядке их выполнения.</summary>
            public List<string> Executed { get; } = new List<string>();

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct)
                => throw new NotSupportedException("починка работает по уже построенному плану");

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => throw new NotSupportedException("починка работает по уже построенному плану");

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => throw new NotSupportedException("починка работает по уже построенному плану");

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
                this.Executed.Add(plan.Version);
                return Task.CompletedTask;
            }
        }
    }
}
