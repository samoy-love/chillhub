// <copyright file="IntegrityCheckerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

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

            public Exception? ManifestError { get; init; }

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
                if (this.ManifestError != null) {
                    return Task.FromException<Manifest>(this.ManifestError);
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
    }
}
