// <copyright file="SyncFileRulesTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Правила, решающие судьбу файла при синхронизации: что считается служебным,
    /// что игнорируется целиком, и как удаляются файлы, которые кто-то держит открытыми.
    /// </summary>
    public class SyncFileRulesTests {
        /// <summary>
        /// Маркеры лаунчера — служебные: в манифесте их нет, поэтому без этой проверки
        /// они попадали в список «лишних» и стирались. Для `.version` это уже стоило
        /// регрессии: после ремонта из настроек игра показывалась как неустановленная.
        /// </summary>
        [Theory]
        [InlineData(".updating")]
        [InlineData(".version")]
        [InlineData(".VERSION")]
        [InlineData("/.version")]
        [InlineData("\\.updating")]
        public void МаркерыЛаунчераСлужебные(string rel) {
            Assert.True(SimpleSyncService.IsServiceRelFile(rel));
        }

        /// <summary>
        /// КАЖДЫЙ маркер, который лаунчер кладёт в корень игры, обязан быть служебным —
        /// перечислением, а не списком, набранным вручную.
        /// <para>
        /// Проверка написана после того, как забытый в списке `.mods.revision` дал вечную
        /// кнопку «Обновить»: установка модов писала отпечаток, синхронизация игры тут же
        /// стирала его как лишний файл, а проверка статуса, не найдя отпечатка, снова
        /// звала обновляться. Следующий маркер, заведённый и не внесённый сюда, уронит
        /// этот тест вместо того, чтобы уронить обновление у игрока.
        /// </para>
        /// </summary>
        [Fact]
        public void ВсеМаркерыВКорнеИгрыСлужебные() {
            var markers = new[] {
                IntegrityChecker.VersionMarkerFileName,
                IntegrityChecker.ModsVersionMarkerFileName,
                IntegrityChecker.ModsRevisionMarkerFileName,
                IntegrityChecker.ModsManifestFileName,
                InstallFingerprint.FileName,
            };

            foreach (var marker in markers) {
                Assert.True(
                    SimpleSyncService.IsServiceRelFile(marker),
                    $"маркер '{marker}' не помечен служебным — синхронизация игры сотрёт его как лишний файл");
            }
        }

        /// <summary>
        /// Отпечаток модпака отдельным случаем: именно он сломал обновление, и именно на
        /// него смотрит проверка «нужно ли обновляться».
        /// </summary>
        [Theory]
        [InlineData(".mods.revision")]
        [InlineData(".MODS.REVISION")]
        [InlineData("/.mods.revision")]
        [InlineData("\\.mods.revision")]
        public void ОтпечатокМодпакаНеУдаляется(string rel) {
            Assert.True(SimpleSyncService.IsServiceRelFile(rel));
        }

        /// <summary>Обычные файлы игры служебными не считаются, иначе они перестанут обновляться.</summary>
        [Theory]
        [InlineData("game.exe")]
        [InlineData("data/.version.bak")]
        [InlineData("saves/.updating.old")]
        [InlineData("")]
        [InlineData("   ")]
        public void ФайлыИгрыНеСлужебные(string rel) {
            Assert.False(SimpleSyncService.IsServiceRelFile(rel));
        }

        /// <summary>
        /// FreeTP/.hash игнорируется целиком: пиратские сборки с FreeTP.Org открывают
        /// сайт при каждом запуске, если лаунчер трогает этот файл.
        /// </summary>
        [Theory]
        [InlineData("FreeTP/.hash")]
        [InlineData("freetp/.hash")]
        [InlineData("FreeTP\\.hash")]
        [InlineData("/FreeTP/.hash")]
        public void ХешFreeTpИгнорируется(string rel) {
            Assert.True(SimpleSyncService.IsIgnoredRelFile(rel));
        }

        /// <summary>Игнорируется именно этот файл, а не всё внутри FreeTP.</summary>
        [Theory]
        [InlineData("FreeTP/other.dll")]
        [InlineData("data/FreeTP/.hash")]
        [InlineData(".hash")]
        [InlineData("")]
        public void ОстальныеФайлыНеИгнорируются(string rel) {
            Assert.False(SimpleSyncService.IsIgnoredRelFile(rel));
        }

        /// <summary>Папка FreeTP в корне игры сохраняется даже пустой.</summary>
        [Theory]
        [InlineData("FreeTP")]
        [InlineData("freetp")]
        [InlineData("/FreeTP/")]
        [InlineData("FreeTP\\")]
        public void ПапкаFreeTpВКорнеСохраняется(string rel) {
            Assert.True(SimpleSyncService.IsIgnoredRelDir(rel));
        }

        /// <summary>Правило про FreeTP работает только в корне: вложенная папка обычная.</summary>
        [Theory]
        [InlineData("data/FreeTP")]
        [InlineData("FreeTP/inner")]
        [InlineData("Saves")]
        [InlineData("")]
        public void ВложеннаяПапкаFreeTpОбычная(string rel) {
            Assert.False(SimpleSyncService.IsIgnoredRelDir(rel));
        }

        /// <summary>Обычный файл удаляется.</summary>
        [Fact]
        public void ОбычныйФайлУдаляется() {
            using var dir = new TempDir();
            var path = dir.WriteFile("old.dll", "мусор");

            SimpleSyncService.SafeDeleteFile(path);

            Assert.False(File.Exists(path));
        }

        /// <summary>
        /// Read-only и «системный» атрибуты снимаются перед удалением: иначе остатки
        /// прошлой сборки не вычищаются и ломают проверку целостности.
        /// </summary>
        [Fact]
        public void АтрибутыНеМешаютУдалению() {
            using var dir = new TempDir();
            var path = dir.WriteFile("old.dll", "мусор");
            File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.System);

            SimpleSyncService.SafeDeleteFile(path);

            Assert.False(File.Exists(path));
        }

        /// <summary>Несуществующий файл — не ошибка: план мог устареть.</summary>
        [Fact]
        public void УдалениеНесуществующегоНеРоняет() {
            using var dir = new TempDir();

            SimpleSyncService.SafeDeleteFile(dir.PathTo("нет-такого"));
        }

        /// <summary>Маркер незавершённого обновления содержит версию — по ней видно, что чинить.</summary>
        [Fact]
        public void МаркерОбновленияНазываетВерсию() {
            using var dir = new TempDir();

            SimpleSyncService.WriteUpdateMarker(dir.Root, "2.1.0");

            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root));
            Assert.Contains("version=2.1.0", SimpleSyncService.ReadUpdateMarker(dir.Root), StringComparison.Ordinal);
        }

        /// <summary>Маркер ставится даже в ещё не созданную папку игры — это первая установка.</summary>
        [Fact]
        public void МаркерСтавитсяВНесуществующуюПапку() {
            using var dir = new TempDir();
            var root = dir.PathTo("новая-игра");

            SimpleSyncService.WriteUpdateMarker(root, "1.0.0");

            Assert.True(SimpleSyncService.HasUpdateMarker(root));
        }

        /// <summary>Снятие маркера убирает файл, а повторное снятие ничего не ломает.</summary>
        [Fact]
        public void СнятиеМаркераИдемпотентно() {
            using var dir = new TempDir();
            SimpleSyncService.WriteUpdateMarker(dir.Root, "1.0.0");

            SimpleSyncService.ClearUpdateMarker(dir.Root);
            SimpleSyncService.ClearUpdateMarker(dir.Root);

            Assert.False(SimpleSyncService.HasUpdateMarker(dir.Root));
        }

        /// <summary>
        /// Список отложенных файлов в маркере обрезается: при полной переустановке
        /// заняты бывают сотни файлов, и весь их список в маркере не нужен никому.
        /// </summary>
        [Fact]
        public void СписокОтложенныхФайловОбрезается() {
            using var dir = new TempDir();
            var deferred = new List<string>();
            for (var i = 0; i < 50; i++) {
                deferred.Add($"file{i}.dll");
            }

            SimpleSyncService.WriteRebootPendingMarker(dir.Root, "3.0.0", deferred);

            var marker = SimpleSyncService.ReadUpdateMarker(dir.Root);
            Assert.Contains("pending=50", marker, StringComparison.Ordinal);
            Assert.Equal(20, CountOccurrences(marker, "pendingFile="));
        }

        /// <summary>Уборка каталогов не поднимается выше корня игры.</summary>
        [Fact]
        public void УборкаНеПоднимаетсяВышеКорняИгры() {
            using var dir = new TempDir();
            var root = Path.Combine(dir.Root, "game");
            Directory.CreateDirectory(root);

            // Корень игры пуст, и «удалённый» файл лежал прямо в нём: подниматься некуда.
            SimpleSyncService.CleanupDirsEmptiedByUpdate(root, new[] { "old.dll" }, new HashSet<string>());

            Assert.True(Directory.Exists(root), "уборка снесла сам корень игры");
            Assert.True(Directory.Exists(dir.Root), "уборка выбралась выше корня игры");
        }

        /// <summary>Несуществующий корень уборку не роняет: папку игры могли удалить руками.</summary>
        [Fact]
        public void НесуществующийКореньНеРоняетУборку() {
            using var dir = new TempDir();

            SimpleSyncService.CleanupDirsEmptiedByUpdate(
                dir.PathTo("нет-такой"), new[] { "a/b.dll" }, new HashSet<string>());
        }

        private static int CountOccurrences(string haystack, string needle) {
            var count = 0;
            var at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) {
                count++;
                at += needle.Length;
            }

            return count;
        }
    }
}
