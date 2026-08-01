// <copyright file="ManifestValidationTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;
    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Манифест — это данные с сервера раздачи, а не доверенный конфиг. Его пути
    /// подставляются в Path.Combine и открываются на запись, поэтому неправильный
    /// путь означает запись файла куда угодно на диске пользователя.
    /// </summary>
    public class ManifestValidationTests {
        /// <summary>
        /// Ровно та атака, ради которой существует проверка: путь уводит запись
        /// в автозагрузку текущего пользователя. Хеш совпадёт — он взят из того же
        /// манифеста, поэтому единственная защита здесь — проверка самого пути.
        /// </summary>
        [Fact]
        public async Task ВыходЗаКореньЧерезDotDotОтвергается() {
            using var dir = new TempDir();
            var evil = "../../../../Users/x/AppData/Roaming/Microsoft/Windows/Start Menu/Programs/Startup/x.exe";
            var manifest = PlanTestData.Manifest(PlanTestData.File(evil, 10, "aa"));

            await Assert.ThrowsAsync<ManifestValidationException>(
                () => PlanTestData.PlanAsync(manifest, dir.Root));
        }

        [Theory]
        [InlineData("../evil.exe")]
        [InlineData("data/../../evil.exe")]
        [InlineData("data/../evil.exe")]
        [InlineData("..")]
        [InlineData("./evil.exe")]
        [InlineData("C:/Windows/System32/evil.dll")]
        [InlineData(@"C:\Windows\System32\evil.dll")]
        [InlineData("/etc/passwd")]
        [InlineData(@"\\server\share\evil.exe")]
        [InlineData("data/game.exe:hidden.exe")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("NUL")]
        [InlineData("data/CON.txt")]
        [InlineData("data/evil.exe.")]
        [InlineData("data/evil.exe ")]
        public async Task ОпасныйПутьОтвергаетМанифестЦеликом(string evilPath) {
            using var dir = new TempDir();

            // Второй файл абсолютно нормальный: отвергнуть обязаны ВЕСЬ манифест,
            // а не «пропустить плохую запись и скачать остальное».
            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", 1, "cafebabe"),
                PlanTestData.File(evilPath, 10, "deadbeef"));

            await Assert.ThrowsAsync<ManifestValidationException>(
                () => PlanTestData.PlanAsync(manifest, dir.Root));
        }

        [Fact]
        public async Task ОпасныйПутьВПустыхКаталогахТожеОтвергается() {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 1, "aa"));
            manifest.EmptyDirs = new List<string> { "saves", "../../../evil" };

            await Assert.ThrowsAsync<ManifestValidationException>(
                () => PlanTestData.PlanAsync(manifest, dir.Root));
        }

        [Fact]
        public async Task НормальныйМанифестПроходитПроверку() {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(
                PlanTestData.File("game.exe", 1, "cafebabe"),
                PlanTestData.File("data/sub dir/pack.bin", 2, "deadbeef"),
                PlanTestData.File("данные/игра.exe", 3, "beefcafe"),
                PlanTestData.File("FreeTP/.hash", 4, "aabb"));
            manifest.EmptyDirs = new List<string> { "saves", "logs/crash" };

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.Equal(3, plan.TotalFilesToDownload);
            Assert.Equal(new[] { "saves", "logs/crash" }, plan.EmptyDirsToCreate);
        }

        /// <summary>
        /// Пункт 2: канонизация подписи не совпадала с путём, который реально пишется.
        /// Все пять мутаций ниже нормализуются обратно в "game/app.exe", то есть
        /// подписываемые байты не меняются и подпись остаётся валидной — а на диск
        /// уходил сырой путь. Единственное лекарство: требовать, чтобы путь УЖЕ был
        /// каноническим, тогда подписанное и используемое — одни и те же байты.
        /// </summary>
        [Theory]
        [InlineData("/game/app.exe")]
        [InlineData(@"game\app.exe")]
        [InlineData(" game/app.exe")]
        [InlineData("game/app.exe/")]
        [InlineData("game//app.exe")]
        public async Task НеканоническийПутьОтвергается(string mutated) {
            // Контроль: канонизация действительно схлопывает мутацию в исходный путь,
            // то есть подпись бы не сломалась и тест бьёт именно в эту дыру.
            Assert.Equal("game/app.exe", ManifestPath.Canonicalize(mutated));

            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(PlanTestData.File(mutated, 10, "aa"));

            await Assert.ThrowsAsync<ManifestValidationException>(
                () => PlanTestData.PlanAsync(manifest, dir.Root));
        }

        [Fact]
        public void EnforceОтвергаетНеканоническийПутьДажеБезПодписи() {
            // Режим совместимости распространяется на ОТСУТСТВИЕ подписи, но не на
            // опасное содержимое: неподписанный манифест с таким путём тоже вне закона.
            var manifest = PlanTestData.Manifest(PlanTestData.File("/game/app.exe", 10, "aa"));
            manifest.Signature = "dev-mock-signature";

            Assert.Throws<ManifestValidationException>(
                () => ManifestSignature.Enforce(manifest, "test://legacy"));
        }

        [Theory]
        [InlineData("game.exe")]
        [InlineData("data/pack.bin")]
        [InlineData("данные/игра.exe")]
        [InlineData("data/sub dir/pack.bin")]
        [InlineData("FreeTP/.hash")]
        [InlineData("a/b/c/d/e.dat")]
        public void БезопасныеПутиПринимаются(string path) {
            Assert.Null(ManifestPath.Describe(path));
            Assert.True(ManifestPath.IsSafe(path));
        }

        [Theory]
        [InlineData("../x")]
        [InlineData("a/../../x")]
        [InlineData("a/./x")]
        [InlineData("/x")]
        [InlineData("x/")]
        [InlineData("a//b")]
        [InlineData(@"a\b")]
        [InlineData(" a/b")]
        [InlineData("a/b ")]
        [InlineData("C:/x")]
        [InlineData("x:y")]
        [InlineData("a/b\tc")]
        [InlineData("a/b\nc")]
        [InlineData("a/b*c")]
        [InlineData("a/b?c")]
        [InlineData("PRN")]
        [InlineData("lpt1.txt")]
        [InlineData(null)]
        public void НебезопасныеПутиОтвергаются(string? path) {
            Assert.NotNull(ManifestPath.Describe(path));
            Assert.False(ManifestPath.IsSafe(path));
        }

        [Fact]
        public void СлишкомДлинныйПутьОтвергается() {
            Assert.NotNull(ManifestPath.Describe(new string('a', ManifestPath.MaxLength + 1)));
        }

        [Fact]
        public void CombineВозвращаетПутьВнутриКорня() {
            using var dir = new TempDir();
            var full = ManifestPath.Combine(dir.Root, "data/pack.bin");

            Assert.Equal(dir.PathTo("data/pack.bin"), full);
        }

        [Fact]
        public void CombineОтвергаетПопыткуВыйтиЗаКорень() {
            using var dir = new TempDir();

            var ex = Assert.Throws<ManifestPathException>(() => ManifestPath.Combine(dir.Root, "../evil.exe"));
            Assert.Contains("..", ex.Message, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Сквозная проверка на реальной файловой системе: без валидации файл лёг бы
        /// РЯДОМ с корнем игры, а не внутри него.
        /// </summary>
        [Fact]
        public void CombineНеДаётЗаписатьФайлЗаПределамиКорня() {
            using var dir = new TempDir();
            var root = Path.Combine(dir.Root, "game");
            Directory.CreateDirectory(root);

            Assert.Throws<ManifestPathException>(() => ManifestPath.Combine(root, "../outside.exe"));
            Assert.False(File.Exists(Path.Combine(dir.Root, "outside.exe")));
        }
    }
}
