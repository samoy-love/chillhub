// <copyright file="ContentUrlEncodingTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Linq;
    using System.Threading.Tasks;

    using Xunit;

    /// <summary>
    /// Адрес файла, который загрузчик соберёт по манифесту.
    /// <para>
    /// Проверяется не строка сама по себе, а то, что реально уедет на сервер: адрес
    /// подставляется в <c>HttpRequestMessage</c>, то есть разбирается как
    /// <see cref="Uri"/>. Имя с решёткой раньше превращалось во фрагмент — на провод
    /// уходил запрос на обрезанное имя, сервер отвечал 404, три попытки подряд роняли
    /// весь план, и такой модпак не устанавливался никогда.
    /// </para>
    /// </summary>
    public class ContentUrlEncodingTests {
        /// <summary>
        /// Решётка в имени файла — это часть имени, а не начало фрагмента.
        /// </summary>
        [Fact]
        public async Task РешёткаВИмениФайлаДоезжаетДоСервера() {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(PlanTestData.File("BepInEx/plugins/Mod#2.dll", 7, "aabb"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            var uri = new Uri(Assert.Single(plan.Downloads).Url);
            Assert.Equal(string.Empty, uri.Fragment);
            Assert.EndsWith("/BepInEx/plugins/Mod#2.dll", Uri.UnescapeDataString(uri.AbsolutePath), StringComparison.Ordinal);
        }

        /// <summary>
        /// Процент в имени не должен разбираться как начало escape-последовательности:
        /// «patch%2Fboot.dll» — это один файл, а не файл «boot.dll» в папке «patch».
        /// </summary>
        [Fact]
        public async Task ПроцентВИмениНеПревращаетсяВДругойПуть() {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(PlanTestData.File("patch%2Fboot.dll", 7, "aabb"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            var uri = new Uri(Assert.Single(plan.Downloads).Url);
            Assert.EndsWith("/patch%2Fboot.dll", Uri.UnescapeDataString(uri.AbsolutePath), StringComparison.Ordinal);
        }

        /// <summary>
        /// Пробелы и кириллица работали и до кодирования — за них отвечал разбор Uri.
        /// Тест держит их на месте: кодировать посегментно нужно так, чтобы дерево
        /// каталогов осталось деревом, а имена — прежними.
        /// </summary>
        [Theory]
        [InlineData("data/sub dir/pack.bin")]
        [InlineData("моды/русское имя.pak")]
        [InlineData("data/скобки (1)/pack.bin")]
        public async Task ПробелыИКириллицаОстаютсяПрежними(string rel) {
            using var dir = new TempDir();
            var manifest = PlanTestData.Manifest(PlanTestData.File(rel, 7, "aabb"));

            var plan = await PlanTestData.PlanAsync(manifest, dir.Root);

            var uri = new Uri(Assert.Single(plan.Downloads).Url);
            Assert.EndsWith("/" + rel, Uri.UnescapeDataString(uri.AbsolutePath), StringComparison.Ordinal);
        }

        /// <summary>
        /// Самообновление собирает адреса своим кодом — та же решётка ломала и его,
        /// а лаунчер после неудачной загрузки предлагает обновиться снова и снова.
        /// </summary>
        [Fact]
        public void АдресаСамообновленияКодируютсяТакЖе() {
            using var stand = new SelfUpdateStand();
            var manifest = SelfUpdateManifest.Of(SelfUpdateManifest.Different("lib/Mod#2.dll"));

            var plan = SelfUpdateDownloadTests
                .NewDownloader(stand, new FakeSync(), out _)
                .BuildSelfUpdatePlan(manifest, string.Empty, stand.Temp.Root, "https://example.test/content");

            var uri = new Uri(plan.Downloads.Single().Url);
            Assert.Equal(string.Empty, uri.Fragment);
            Assert.EndsWith("/lib/Mod#2.dll", Uri.UnescapeDataString(uri.AbsolutePath), StringComparison.Ordinal);
        }
    }
}
