// <copyright file="ThemeContrastTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Windows;
    using System.Windows.Media;

    using Xunit;

    /// <summary>
    /// Читаемость подписей и заметность подсветки — счётом, а не на глаз.
    /// <para>
    /// Обе беды, которые здесь проверяются, живьём выглядят как «вроде работает»: подпись
    /// на кнопке видно, пока смотришь на неё в упор, а подсветка пункта меню совпадала с
    /// фоном самого меню — список вариантов запуска выглядел неживым, и понять это по
    /// разметке было нельзя, потому что триггер наведения был на месте.
    /// </para>
    /// <para>
    /// Порог 4.5:1 — требование WCAG AA к мелкому тексту; кнопки и пункты меню набраны
    /// именно им. Тема грузится как словарь ресурсов, без окна и приложения.
    /// </para>
    /// </summary>
    public class ThemeContrastTests {
        /// <summary>Подпись главной кнопки читается в покое, под курсором и в нажатии.</summary>
        [Theory]
        [InlineData("Brush.AccentFill")]
        [InlineData("Brush.AccentFillHover")]
        [InlineData("Brush.AccentFillPressed")]
        public void БелаяПодписьНаЗаливкеКнопкиПроходитПорог(string fillKey)
            => UiThread.Run(() => {
                var fill = Color(fillKey);

                Assert.True(
                    Contrast(Colors.White, fill) >= 4.5,
                    $"{fillKey}: белая подпись даёт {Contrast(Colors.White, fill):0.00}:1 при пороге 4.5:1");
            });

        /// <summary>
        /// Подписи вторичным и приглушённым цветом читаются на самом светлом из фонов, на
        /// которых они встречаются, — на карточке поверх карточки.
        /// </summary>
        [Theory]
        [InlineData("Brush.TextSecondary")]
        [InlineData("Brush.TextMuted")]
        public void ВторичныеПодписиЧитаютсяНаКарточке(string textKey)
            => UiThread.Run(() => {
                var ratio = Contrast(Color(textKey), Color("Brush.Surface2"));

                Assert.True(ratio >= 4.5, $"{textKey} на Surface2 даёт {ratio:0.00}:1 при пороге 4.5:1");
            });

        /// <summary>
        /// Подсветка пункта заметна на фоне списка. Проверяется отношением светлот: пока
        /// подсветкой служил Brush.Hover, она совпадала с фоном меню в точности, и
        /// наведение не показывало ровно ничего. Порог 1.4 — не «различимо при
        /// внимательном взгляде», а «видно сразу»: подсветка отвечает на вопрос, на каком
        /// пункте курсор.
        /// </summary>
        [Theory]
        [InlineData("Brush.MenuHover")]
        [InlineData("Brush.MenuPressed")]
        public void ПодсветкаПунктаЗаметнаНаФонеМеню(string hoverKey)
            => UiThread.Run(() => {
                var ratio = Contrast(Color(hoverKey), Color("Brush.Surface2"));

                Assert.True(ratio >= 1.4, $"{hoverKey} против фона меню — всего {ratio:0.000}:1, наведения не видно");
            });

        /// <summary>Нажатие отличается от наведения: иначе клик по пункту ничем не отзывается.</summary>
        [Fact]
        public void НажатыйПунктОтличаетсяОтПодсвеченного()
            => UiThread.Run(() => {
                var ratio = Contrast(Color("Brush.MenuPressed"), Color("Brush.MenuHover"));

                Assert.True(ratio >= 1.1, $"нажатие против наведения — всего {ratio:0.000}:1");
            });

        /// <summary>Белая подпись читается на подсвеченном и нажатом пункте.</summary>
        [Theory]
        [InlineData("Brush.MenuHover")]
        [InlineData("Brush.MenuPressed")]
        public void ПодписьПунктаЧитаетсяНаПодсветке(string key)
            => UiThread.Run(() => {
                var ratio = Contrast(Colors.White, Color(key));

                Assert.True(ratio >= 4.5, $"белая подпись на {key} даёт {ratio:0.00}:1 при пороге 4.5:1");
            });

        private static Color Color(string key) {
            var theme = (ResourceDictionary)Application.LoadComponent(
                new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative));

            var brush = Assert.IsType<SolidColorBrush>(theme[key]);
            return brush.Color;
        }

        /// <summary>Отношение контраста по WCAG 2.1: (L1 + 0.05) / (L2 + 0.05).</summary>
        private static double Contrast(Color a, Color b) {
            var la = Luminance(a);
            var lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color c)
            => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

        private static double Channel(byte value) {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }
}
