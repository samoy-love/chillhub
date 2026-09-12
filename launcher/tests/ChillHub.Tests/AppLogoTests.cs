// <copyright file="AppLogoTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Markup;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    using Xunit;

    /// <summary>
    /// Логотип в шапке лаунчера — вектор из Assets/AppLogo.xaml, собранный генератором
    /// значка. Растровый кадр из app.ico WPF растягивал под масштаб экрана, и на 125–200 %
    /// он мылился. Здесь логотип рисуется в тех размерах, какие даёт шапка на разных
    /// масштабах, и проверяется по пикселям: шар тёплый, рамка сиреневая, угол прозрачный.
    /// </summary>
    public class AppLogoTests {
        [Fact]
        public void ЛоготипЗагружаетсяКакВекторИЧётокНаЛюбомМасштабе() {
            UiThread.Run(() => {
                var path = Path.Combine(ChangelogTests.FindRepoRoot(), "launcher", "ChillHub", "Assets", "AppLogo.xaml");
                var dictionary = Assert.IsType<ResourceDictionary>(XamlReader.Parse(File.ReadAllText(path)));
                var logo = Assert.IsType<DrawingImage>(dictionary["AppLogo"]);
                Assert.Equal(32, logo.Width, 3);
                Assert.Equal(32, logo.Height, 3);

                // Шапка даёт 28 DIP: на 125 % это 35 px, на 150 % — 42, на 200 % — 56.
                foreach (var px in new[] { 35, 42, 56 }) {
                    var pixels = Render(logo, px);
                    var ball = At(pixels, px, px / 2, (int)(px * 10.0 / 32));
                    Assert.True(ball[3] == 255 && ball[2] > 200 && ball[2] > ball[0] + 60, $"{px}: центр шара не коралловый ({Rgba(ball)})");
                    Assert.Equal(0, At(pixels, px, 0, 0)[3]);
                }

                // На 200 % рамка шириной больше пикселя: второй пиксель от края — целиком её.
                var rim = At(Render(logo, 56), 56, 2, 28);
                Assert.True(rim[3] == 255 && rim[0] > 80 && rim[0] > rim[2], $"рамка не сиреневая ({Rgba(rim)})");
            });
        }

        /// <summary>Пиксель как B, G, R, A — так лежит Pbgra32.</summary>
        private static byte[] At(byte[] pixels, int px, int x, int y) {
            var i = (y * px + x) * 4;
            return new[] { pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3] };
        }

        private static string Rgba(byte[] p) => $"rgba({p[2]}, {p[1]}, {p[0]}, {p[3]})";

        private static byte[] Render(DrawingImage logo, int px) {
            var image = new Image { Source = logo, Width = px, Height = px, UseLayoutRounding = true, SnapsToDevicePixels = true };
            image.Measure(new Size(px, px));
            image.Arrange(new Rect(0, 0, px, px));
            var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(image);
            var pixels = new byte[px * px * 4];
            bitmap.CopyPixels(pixels, px * 4, 0);

            // Для ревью глазами: CHILLHUB_LOGO_DUMP=<каталог> кладёт туда logo-<px>.png.
            var dump = Environment.GetEnvironmentVariable("CHILLHUB_LOGO_DUMP");
            if (!string.IsNullOrEmpty(dump)) {
                Directory.CreateDirectory(dump);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(dump, $"logo-{px}.png"));
                encoder.Save(stream);
            }

            return pixels;
        }
    }
}
