// <copyright file="SyncFinishPlanTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Завершающая фаза обновления: удаление лишних файлов, уборка опустевших каталогов
    /// и снятие маркера незавершённого обновления.
    /// <para>
    /// Это единственное место клиента, которое УДАЛЯЕТ с диска пользователя. Ошибка здесь
    /// не «неверно нарисованный экран», а потерянные сохранения игры или снятый маркер над
    /// наполовину обновлённой сборкой. Поэтому проверяется не только то, что удаляется, но
    /// и — главным образом — то, что НЕ удаляется.
    /// </para>
    /// </summary>
    public class SyncFinishPlanTests {
        /// <summary>Лишний файл из плана удаляется.</summary>
        [Fact]
        public void ЛишнийФайлУдаляется() {
            using var dir = new TempDir();
            dir.WriteFile("old.dll", "мусор");

            Finish(NewPlan(dir.Root, toDelete: new[] { "old.dll" }));

            Assert.False(File.Exists(dir.PathTo("old.dll")));
        }

        /// <summary>
        /// Каталог, опустевший из-за нашего удаления, убирается — иначе от старых версий
        /// сборки в папке игры копится дерево пустых папок.
        /// </summary>
        [Fact]
        public void КаталогОпустевшийИзЗаНашегоУдаленияУбирается() {
            using var dir = new TempDir();
            dir.WriteFile("dlc/old/mod.pak", "x");

            Finish(NewPlan(dir.Root, toDelete: new[] { "dlc/old/mod.pak" }));

            Assert.False(Directory.Exists(dir.PathTo("dlc/old")));
            Assert.False(Directory.Exists(dir.PathTo("dlc")));
        }

        /// <summary>
        /// Пустой каталог, созданный САМОЙ игрой, обязан пережить обновление.
        /// <para>
        /// Здесь уже была регрессия: уборка шла обходом всего дерева игры и сносила
        /// любую пустую папку. Игра теряла Saves/Config при каждом обновлении, хотя
        /// лаунчер в этих папках ничего не трогал.
        /// </para>
        /// </summary>
        [Fact]
        public void ЧужойПустойКаталогНеТрогаем() {
            using var dir = new TempDir();
            Directory.CreateDirectory(dir.PathTo("Saves"));
            dir.WriteFile("old.dll", "x");

            Finish(NewPlan(dir.Root, toDelete: new[] { "old.dll" }));

            Assert.True(Directory.Exists(dir.PathTo("Saves")), "пустая папка игры удалена уборкой");
        }

        /// <summary>Каталог, где после удаления остались другие файлы, не трогаем.</summary>
        [Fact]
        public void НеопустевшийКаталогОстаётся() {
            using var dir = new TempDir();
            dir.WriteFile("data/old.pak", "x");
            dir.WriteFile("data/keep.pak", "y");

            Finish(NewPlan(dir.Root, toDelete: new[] { "data/old.pak" }));

            Assert.True(Directory.Exists(dir.PathTo("data")));
            Assert.True(File.Exists(dir.PathTo("data/keep.pak")));
        }

        /// <summary>
        /// Каталог из манифеста (<c>EmptyDirsToCreate</c>) уборка не сносит, даже если
        /// опустошила его сама: сборка объявила его нужным.
        /// </summary>
        [Fact]
        public void КаталогИзМанифестаПереживаетУборку() {
            using var dir = new TempDir();
            dir.WriteFile("logs/old.txt", "x");

            Finish(NewPlan(dir.Root, toDelete: new[] { "logs/old.txt" }, emptyDirs: new[] { "logs" }));

            Assert.True(Directory.Exists(dir.PathTo("logs")));
        }

        /// <summary>
        /// Папка FreeTP сохраняется даже пустой — см. комментарий у IsIgnoredRelDir.
        /// Пиратские сборки с FreeTP.Org открывают сайт при запуске, если её нет.
        /// </summary>
        [Fact]
        public void ПапкаFreeTpСохраняетсяПустой() {
            using var dir = new TempDir();
            dir.WriteFile("FreeTP/readme.txt", "x");

            Finish(NewPlan(dir.Root, toDelete: new[] { "FreeTP/readme.txt" }));

            Assert.True(Directory.Exists(dir.PathTo("FreeTP")), "папка FreeTP удалена уборкой");
        }

        /// <summary>
        /// FreeTP/.hash не удаляется, даже если он каким-то образом попал в ToDelete.
        /// <para>
        /// Планировщик его отфильтровывает, но здесь стоит вторая проверка: без этого
        /// файла пиратская сборка открывает сайт FreeTP.Org при каждом запуске игры,
        /// а удаление необратимо. Тест закрепляет именно рубеж на удалении.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("FreeTP/.hash")]
        [InlineData("freetp/.hash")]
        [InlineData("FreeTP\\.hash")]
        public void ХешFreeTpНеУдаляетсяДажеИзПлана(string rel) {
            using var dir = new TempDir();
            dir.WriteFile("FreeTP/.hash", "не трогать");

            Finish(NewPlan(dir.Root, toDelete: new[] { rel }));

            Assert.True(File.Exists(dir.PathTo("FreeTP/.hash")), "FreeTP/.hash удалён при обновлении");
        }

        /// <summary>Пустые каталоги из манифеста создаются, даже если их не было.</summary>
        [Fact]
        public void ПустыеКаталогиИзМанифестаСоздаются() {
            using var dir = new TempDir();

            Finish(NewPlan(dir.Root, emptyDirs: new[] { "mods", "Saves/profiles" }));

            Assert.True(Directory.Exists(dir.PathTo("mods")));
            Assert.True(Directory.Exists(dir.PathTo("Saves/profiles")));
        }

        /// <summary>
        /// Путь с выходом за корень игры не удаляет ничего снаружи.
        /// Список ToDelete строится обходом самой папки, но удаление необратимо,
        /// поэтому подменённый план не должен доставать до соседних каталогов.
        /// </summary>
        [Theory]
        [InlineData("../снаружи.txt")]
        [InlineData("dlc/../../снаружи.txt")]
        public void ВыходЗаКореньИгрыНичегоНеУдаляет(string rel) {
            using var dir = new TempDir();
            var root = Path.Combine(dir.Root, "game");
            Directory.CreateDirectory(root);
            var outside = dir.WriteFile("снаружи.txt", "чужое");

            Finish(NewPlan(root, toDelete: new[] { rel }));

            Assert.True(File.Exists(outside), "удаление выбралось за пределы папки игры");
        }

        /// <summary>
        /// Отклонённый путь пропускается, а фаза завершения доводится до конца.
        /// <para>
        /// Проверка пути жила ВНЕ try, и её исключение улетало наружу из всей фазы.
        /// Между тем в папке игры такое имя заводится без всякой подмены плана:
        /// файл с именем устройства (CON.txt), имя с краевой точкой, путь длиннее
        /// 1024 символов — NTFS всё это позволяет, а обход папки честно кладёт их
        /// в ToDelete. Итог: остальные файлы не удалялись, каталоги из манифеста не
        /// создавались, маркер незавершённого обновления не снимался, и игра
        /// НАВСЕГДА оставалась в состоянии «обновление прервано».
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("CON.txt")]
        [InlineData("mods/имя-с-точкой.")]
        [InlineData("../снаружи.txt")]
        public void ОтклонённыйПутьНеСрываетВсюФазуЗавершения(string bad) {
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.0");
            dir.WriteFile("old.dll", "мусор");

            var plan = NewPlan(dir.Root, toDelete: new[] { bad, "old.dll" }, emptyDirs: new[] { "mods" });
            Finish(plan, changesDisk: true);

            Assert.False(File.Exists(dir.PathTo("old.dll")), "следующий за отклонённым файл не удалён");
            Assert.True(Directory.Exists(dir.PathTo("mods")), "каталог из манифеста не создан");
            Assert.False(SimpleSyncService.HasUpdateMarker(dir.Root), "маркер не снят — игра навсегда «обновление прервано»");
        }

        /// <summary>Маркер незавершённого обновления снимается, когда всё применилось.</summary>
        [Fact]
        public void МаркерСнимаетсяПослеПолногоПрименения() {
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.0");

            Finish(NewPlan(dir.Root), changesDisk: true);

            Assert.False(SimpleSyncService.HasUpdateMarker(dir.Root));
        }

        /// <summary>
        /// Пока хоть один файл заменится только после перезагрузки, маркер обязан остаться.
        /// Снять его означало бы объявить «игра обновлена» над сборкой, у которой на диске
        /// лежит старый исполняемый файл, — ровно то, от чего маркер и защищает.
        /// </summary>
        [Fact]
        public void ОтложенныеФайлыОставляютМаркерНаМесте() {
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.0");

            Finish(NewPlan(dir.Root, version: "2.0.0"), changesDisk: true, deferred: new[] { "game.exe" });

            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root));

            var marker = SimpleSyncService.ReadUpdateMarker(dir.Root);
            Assert.Contains("state=reboot-required", marker, System.StringComparison.Ordinal);
            Assert.Contains("version=2.0.0", marker, System.StringComparison.Ordinal);
            Assert.Contains("pendingFile=game.exe", marker, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Если фаза активации ничего не меняла, маркер не ставился — и снимать его нельзя:
        /// он мог остаться от предыдущего, действительно оборвавшегося обновления.
        /// </summary>
        [Fact]
        public void БезИзмененийНаДискеЧужойМаркерНеСнимается() {
            using var dir = new TempDir();
            dir.WriteFile(SimpleSyncService.UpdateMarkerFileName, "version=1.0.0");

            Finish(NewPlan(dir.Root), changesDisk: false);

            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root));
        }

        /// <summary>Отсутствующий файл из ToDelete — не ошибка: план мог устареть.</summary>
        [Fact]
        public void ОтсутствующийФайлИзПланаНеРоняетЗавершение() {
            using var dir = new TempDir();

            Finish(NewPlan(dir.Root, toDelete: new[] { "нет-такого.dll" }));

            Assert.False(File.Exists(dir.PathTo("нет-такого.dll")));
        }

        private static DiffPlan NewPlan(
            string root,
            IEnumerable<string>? toDelete = null,
            IEnumerable<string>? emptyDirs = null,
            string version = "1.0.0")
            => new DiffPlan {
                GameId = "test-game",
                Version = version,
                LocalRoot = root,
                ToDelete = new List<string>(toDelete ?? System.Array.Empty<string>()),
                EmptyDirsToCreate = new List<string>(emptyDirs ?? System.Array.Empty<string>()),
            };

        private static void Finish(DiffPlan plan, bool changesDisk = false, IEnumerable<string>? deferred = null) {
            var bag = new ConcurrentBag<string>();
            foreach (var d in deferred ?? System.Array.Empty<string>()) {
                bag.Add(d);
            }

            SimpleSyncService.FinishPlan(plan, bag, changesDisk, CancellationToken.None);
        }
    }
}
