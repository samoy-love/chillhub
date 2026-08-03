// <copyright file="UpdateMarkerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Маркер `.updating` в корне игры означает «обновление оборвалось на фазе активации,
    /// игра в неконсистентном состоянии». Защита работает только пока маркер живёт своей жизнью:
    /// его нельзя ни качать по манифесту, ни удалять как «лишний» файл.
    /// </summary>
    public class UpdateMarkerTests {
        [Fact]
        public void HasUpdateMarker_ВидитСуществующийМаркер() {
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.0");

            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root));
        }

        [Fact]
        public void HasUpdateMarker_БезМаркераВозвращаетFalse() {
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "x");

            Assert.False(SimpleSyncService.HasUpdateMarker(dir.Root));
        }

        [Fact]
        public void HasUpdateMarker_НесуществующаяПапкаНеРоняетПроверку() {
            using var dir = new TempDir();

            Assert.False(SimpleSyncService.HasUpdateMarker(Path.Combine(dir.Root, "нет-такой-папки")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void HasUpdateMarker_ПустойКореньВозвращаетFalse(string root) {
            Assert.False(SimpleSyncService.HasUpdateMarker(root));
        }

        [Fact]
        public void ReadUpdateMarker_ВозвращаетСодержимоеБезКраевыхПробелов() {
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "\r\nversion=1.2.3\r\npid=42\r\n");

            Assert.Equal("version=1.2.3\r\npid=42", SimpleSyncService.ReadUpdateMarker(dir.Root));
        }

        [Fact]
        public void ReadUpdateMarker_БезМаркераВозвращаетПустуюСтроку() {
            using var dir = new TempDir();

            Assert.Equal(string.Empty, SimpleSyncService.ReadUpdateMarker(dir.Root));
        }

        [Fact]
        public async Task PlanAsync_МаркерОбновленияИзМанифестаНеПопадаетВЗагрузку() {
            // Ключевой тест: если маркер окажется в плане загрузки, лаунчер начнёт качать
            // собственный служебный файл и затрёт им признак незавершённого обновления.
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(
                PlanTestData.File(SimpleSyncService.UpdateMarkerFileName, 10, "deadbeef"),
                PlanTestData.File("game.exe", 1, "cafebabe"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.DoesNotContain(plan.Downloads, d => d.RelativePath == SimpleSyncService.UpdateMarkerFileName);
            Assert.Contains(plan.Downloads, d => d.RelativePath == "game.exe");
        }

        [Fact]
        public async Task PlanAsync_МаркерОбновленияНеПопадаетВСписокНаУдаление() {
            // Ключевой тест: маркера нет в манифесте, значит формально он «лишний».
            // Если бы его удаляли, признак оборванного обновления стирался бы при первой же синхронизации.
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.0");
            dir.WriteFile("game.exe", "x");
            dir.WriteFile("мусор.tmp", "y");

            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 1, "cafebabe"));
            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.DoesNotContain(SimpleSyncService.UpdateMarkerFileName, plan.ToDelete);

            // Проверяем, что механизм удаления в принципе работает — иначе тест выше ничего не значил бы.
            Assert.Contains("мусор.tmp", plan.ToDelete);
        }

        [Fact]
        public async Task PlanAsync_МаркерОбновленияПереживаетПланированиеНаДиске() {
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.0");

            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 1, "cafebabe"));
            await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root));
        }

        [Fact]
        public async Task PlanAsync_ФайлыВStagingНеПопадаютВСписокНаУдаление() {
            // .staging — рабочая папка самой синхронизации. Если планировщик посчитает
            // её содержимое «лишними файлами», активация будет сносить только что скачанное.
            using var dir = new TempDir();
            dir.WriteFile(".staging/game.exe", "скачано");
            dir.WriteFile(".staging/data/pack.bin", "скачано");
            dir.WriteFile("game.exe", "x");

            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 1, "cafebabe"));
            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.DoesNotContain(plan.ToDelete, p => p.StartsWith(".staging/"));
            Assert.Empty(plan.ToDelete);
        }

        [Fact]
        public async Task PlanAsync_МаркерВерсииНеПопадаетВСписокНаУдаление() {
            // Регрессия: `.version` в манифесте отсутствует, и пока IsServiceRelFile знал
            // только про `.updating`, маркер версии попадал в ToDelete и стирался на фазе
            // активации. При обычной установке это маскировалось — WriteLocalVersion
            // вызывается сразу после ExecuteAsync. Но «проверка целостности» из настроек
            // делает ExecuteAsync БЕЗ записи маркера, и после успешного ремонта игра
            // показывалась как неустановленная.
            using var dir = new TempDir();
            dir.WriteFile(".version", "1.0.0");
            dir.WriteFile("game.exe", "x");

            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 1, "cafebabe"));
            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.DoesNotContain(".version", plan.ToDelete);

            // Контроль: посторонний файл в ToDelete попадать обязан — иначе тест зелёный
            // просто потому, что список пуст.
            dir.WriteFile("stale.dat", "y");
            var plan2 = await PlanTestData.PlanAsync(manifest, dir.Root);
            Assert.Contains("stale.dat", plan2.ToDelete);
            Assert.DoesNotContain(".version", plan2.ToDelete);
        }

        [Fact]
        public async Task PlanAsync_ОтложеннаяЗаменаNewНеПопадаетВСписокНаУдаление() {
            // Файл, который держит игра или античит, активация кладёт рядом как
            // "<файл>.new" и заказывает замену на перезагрузку через MoveFileEx.
            // В манифесте такого файла нет по определению, и пока он считался «лишним»,
            // следующий план стирал его — отложенная замена молча отменялась, а игра
            // навсегда оставалась со старым содержимым.
            using var dir = new TempDir();
            dir.WriteFile("game.exe", "x");
            dir.WriteFile("game.exe.new", "новое содержимое");
            dir.WriteFile("orphan.new", "y");

            var manifest = PlanTestData.Manifest(PlanTestData.File("game.exe", 1, "cafebabe"));
            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            Assert.DoesNotContain("game.exe.new", plan.ToDelete);

            // ".new" без парного файла в манифесте — обычный мусор: его удаляем как раньше.
            Assert.Contains("orphan.new", plan.ToDelete);
        }
    }

    /// <summary>
    /// Конструирование манифеста и запуск планировщика в памяти: сеть не нужна,
    /// <see cref="SimpleSyncService.PlanAsync(Manifest, string, string, CancellationToken)"/>
    /// работает только с локальными файлами.
    /// </summary>
    internal static class PlanTestData {
        public const string ContentBase = "https://example.invalid/content/game/1.0.0/files";

        public static ManifestFile File(string path, long size, string? sha256 = null, string blake3 = "") {
            return new ManifestFile {
                Path = path,
                Size = size,
                Sha256 = sha256,
                Blake3 = blake3,
            };
        }

        public static Manifest Manifest(params ManifestFile[] files) {
            return new Manifest {
                GameId = "test-" + System.Guid.NewGuid().ToString("N"),
                Version = "1.0.0",
                BuildId = "build-1",
                Files = files.ToList(),
                EmptyDirs = new List<string>(),
            };
        }

        /// <summary>
        /// Строит план и убирает за собой файл кеша хешей, который планировщик пишет в %APPDATA%.
        /// Идентификатор игры уникален на каждый манифест, поэтому тесты не мешают друг другу.
        /// </summary>
        /// <param name="keepCache">
        /// Не удалять кеш после построения плана — нужно тестам, которые повторно строят
        /// план по тому же манифесту и проверяют работу кеша.
        /// </param>
        public static async Task<DiffPlan> PlanAsync(
            Manifest manifest,
            string localRoot,
            PlanOptions? options = null,
            CancellationToken ct = default,
            bool keepCache = false) {
            var sync = new SimpleSyncService(new HttpClient());
            try {
                return await sync.PlanAsync(manifest, localRoot, ContentBase, options ?? PlanOptions.Default, ct);
            }
            finally {
                if (!keepCache) {
                    FileHashCache.Remove(manifest.GameId);
                }
            }
        }
    }
}
