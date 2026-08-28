// <copyright file="ImageCacheLimitTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.IO;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    using ChillHub.Core.Home;
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Потолок кеша готовых картинок.
    /// <para>
    /// До него кеш рос без границы и жил весь сеанс: каждая обложка новости и иконка игры
    /// оставались в памяти навсегда. Заметить это можно было только по растущему аппетиту
    /// лаунчера, который «ничего не делает», — поэтому предел проверяется счётом.
    /// </para>
    /// </summary>
    [Collection(ImageLoaderCollection.Name)]
    public class ImageCacheLimitTests {
        /// <summary>Сверх потолка кеш не растёт, а вытесняет самые старые адреса.</summary>
        [Fact]
        public void СверхПотолкаВытесняютсяСамыеСтарые() => UiThread.Run(() => {
            ImageLoader.ResetForTests();
            try {
                var image = Frozen();
                for (var i = 0; i < ImageLoader.MaxCachedImages + 5; i++) {
                    ImageLoader.Remember($"https://images.invalid/{i}.png", image);
                }

                Assert.Equal(ImageLoader.MaxCachedImages, ImageLoader.CachedCount);

                // Первые адреса ушли, последние остались.
                Assert.False(ImageLoader.IsCached("https://images.invalid/0.png"));
                Assert.True(ImageLoader.IsCached($"https://images.invalid/{ImageLoader.MaxCachedImages + 4}.png"));
            }
            finally {
                ImageLoader.ResetForTests();
            }
        });

        /// <summary>
        /// Повторная запись по тому же адресу не занимает второе место в кеше: иначе
        /// перезагруженная обложка вытесняла бы соседей вместо себя самой.
        /// </summary>
        [Fact]
        public void ПовторнаяЗаписьНеЗанимаетВтороеМесто() => UiThread.Run(() => {
            ImageLoader.ResetForTests();
            try {
                var image = Frozen();
                ImageLoader.Remember("https://images.invalid/cover.png", image);
                ImageLoader.Remember("https://images.invalid/cover.png", Frozen());

                Assert.Equal(1, ImageLoader.CachedCount);
                Assert.True(ImageLoader.IsCached("https://images.invalid/cover.png"));
            }
            finally {
                ImageLoader.ResetForTests();
            }
        });

        /// <summary>Сброс кеша забывает и порядок: иначе он вытеснял бы то, что положили после.</summary>
        [Fact]
        public void СбросЗабываетИПорядокВытеснения() => UiThread.Run(() => {
            ImageLoader.ResetForTests();
            try {
                for (var i = 0; i < 10; i++) {
                    ImageLoader.Remember($"https://images.invalid/old-{i}.png", Frozen());
                }

                ImageLoader.InvalidateAll();
                ImageLoader.Remember("https://images.invalid/fresh.png", Frozen());

                Assert.Equal(1, ImageLoader.CachedCount);
                Assert.True(ImageLoader.IsCached("https://images.invalid/fresh.png"));
            }
            finally {
                ImageLoader.ResetForTests();
            }
        });

        /// <summary>Замороженная картинка — как её кладёт настоящая загрузка.</summary>
        /// <returns>Готовая к переиспользованию картинка.</returns>
        private static BitmapImage Frozen() {
            var bmp = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
