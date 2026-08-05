// <copyright file="ImageUrlAndPlanLogTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Разбор адресов картинок и диагностический дамп плана синхронизации.
    /// </summary>
    public class ImageUrlAndPlanLogTests {
        /// <summary>Абсолютный адрес берётся как есть.</summary>
        [Theory]
        [InlineData("https://cdn.example.test/icon.png")]
        [InlineData("http://localhost:8080/icon.png")]
        public void АбсолютныйАдресОстаётсяКакЕсть(string raw) {
            Assert.Equal(raw, ImageLoader.ResolveUrl(raw, "https://launcher.samoy.love"));
        }

        /// <summary>
        /// Протокол-относительный адрес берёт схему у сервера: на https-странице
        /// картинка по http не загрузится вовсе.
        /// </summary>
        [Fact]
        public void ПротоколОтносительныйАдресБерётСхемуСервера() {
            Assert.Equal(
                "https://cdn.example.test/icon.png",
                ImageLoader.ResolveUrl("//cdn.example.test/icon.png", "https://launcher.samoy.love"));
        }

        /// <summary>
        /// Относительный путь привязывается к origin сервера, а не к текущему каталогу:
        /// иначе иконка игры искалась бы по случайному адресу.
        /// </summary>
        [Theory]
        [InlineData("manifests/game/icon.png")]
        [InlineData("/manifests/game/icon.png")]
        public void ОтносительныйПутьПривязываетсяКСерверу(string raw) {
            Assert.Equal(
                "https://launcher.samoy.love/manifests/game/icon.png",
                ImageLoader.ResolveUrl(raw, "https://launcher.samoy.love"));
        }

        /// <summary>Хвостовой слеш адреса сервера не двоится в готовой ссылке.</summary>
        [Fact]
        public void ХвостовойСлешСервераНеДвоится() {
            Assert.Equal(
                "https://launcher.samoy.love/icon.png",
                ImageLoader.ResolveUrl("icon.png", "https://launcher.samoy.love/"));
        }

        /// <summary>
        /// Путь с подкаталогом в адресе сервера всё равно берёт только origin:
        /// картинки раздаются от корня, а не относительно пути API.
        /// </summary>
        [Fact]
        public void ПутьСервераНеПодмешиваетсяВАдресКартинки() {
            Assert.Equal(
                "https://launcher.samoy.love/icon.png",
                ImageLoader.ResolveUrl("icon.png", "https://launcher.samoy.love/api/v1"));
        }

        /// <summary>Дамп плана не роняет установку, даже если файлов из плана на диске нет.</summary>
        [Fact]
        public void ДампПланаНеРоняетУстановку() {
            using var dir = new TempDir();
            var plan = new DiffPlan {
                GameId = "game",
                Version = "1.0.0",
                LocalRoot = dir.Root,
                Downloads = new List<FileTask> {
                    new FileTask { RelativePath = "нет-такого.dll", Size = 100, Sha256 = "abc" },
                },
                ToDelete = new List<string> { "тоже-нет.dll" },
                EmptyDirsToCreate = new List<string> { "и-папки-нет" },
            };

            SyncPlanLog.LogPlanDownloads("game", "before", plan, dir.Root);
        }

        /// <summary>Пустой план — не повод для сбоя: так выглядит «всё уже совпадает».</summary>
        [Fact]
        public void ПустойПланВыгружаетсяБезСбоя() {
            using var dir = new TempDir();

            SyncPlanLog.LogPlanDownloads("game", "after", new DiffPlan { LocalRoot = dir.Root }, dir.Root);
        }

        /// <summary>Отсутствующий план не роняет диагностику: она вспомогательная.</summary>
        [Fact]
        public void ОтсутствующийПланНеРоняетДиагностику() {
            using var dir = new TempDir();

            SyncPlanLog.LogPlanDownloads("game", "before", null!, dir.Root);
        }

        /// <summary>
        /// Крупный план не выгружается целиком: в жалобах «скачивает всё заново» счёт
        /// файлов идёт на тысячи, и полный список забил бы лог пользователя.
        /// </summary>
        [Fact]
        public void КрупныйПланВыгружаетсяЧастично() {
            using var dir = new TempDir();
            var downloads = new List<FileTask>();
            var deletes = new List<string>();
            for (var i = 0; i < 500; i++) {
                downloads.Add(new FileTask { RelativePath = $"data/file{i}.pak", Size = i });
                deletes.Add($"old/file{i}.pak");
            }

            var plan = new DiffPlan {
                GameId = "game",
                LocalRoot = dir.Root,
                Downloads = downloads,
                ToDelete = deletes,
            };

            SyncPlanLog.LogPlanDownloads("game", "before", plan, dir.Root);
        }

        /// <summary>Существующие файлы плана тоже обходятся — по ним и видно, что уже на диске.</summary>
        [Fact]
        public void СуществующиеФайлыПланаОбходятся() {
            using var dir = new TempDir();
            dir.WriteFile("data/file.pak", "содержимое");
            Directory.CreateDirectory(dir.PathTo("empty"));

            var plan = new DiffPlan {
                GameId = "game",
                LocalRoot = dir.Root,
                Downloads = new List<FileTask> {
                    new FileTask { RelativePath = "data/file.pak", Size = 10, Sha256 = "a", Blake3 = "b" },
                },
                ToDelete = new List<string> { "data/file.pak" },
                EmptyDirsToCreate = new List<string> { "empty" },
            };

            SyncPlanLog.LogPlanDownloads("game", "before", plan, dir.Root);
        }
    }
}
