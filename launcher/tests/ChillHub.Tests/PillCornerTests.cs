// <copyright file="PillCornerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Пилюля — это скругление ровно в половину высоты, а не «побольше».
    /// <para>
    /// Бейджи состояния делались через CornerRadius="999" и выглядели овалами: WPF
    /// ужимает лишний радиус по каждой стороне отдельно, и у бейджа 80×20 угол
    /// выходит 40 по горизонтали и 10 по вертикали — эллипс. Глазом это заметили
    /// только на экране, поэтому здесь форма меряется.
    /// </para>
    /// </summary>
    public class PillCornerTests {
        // Комментарии в разметке объясняют, почему 999 не годится, — их не считаем.
        private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>Скругление — половина меньшей стороны, в какую сторону ни вытянут.</summary>
        [Theory]
        [InlineData(80, 20, 10)]
        [InlineData(4, 300, 2)]
        [InlineData(24, 24, 12)]
        public void СкруглениеРавноПоловинеМеньшейСтороны(double width, double height, double radius) {
            var r = PillCornerConverter.For(width, height);

            Assert.Equal(new CornerRadius(radius), r);
        }

        /// <summary>Первый проход разметки даёт нули и NaN — это не повод падать.</summary>
        [Theory]
        [InlineData(double.NaN, 20)]
        [InlineData(0, 0)]
        [InlineData(-5, 20)]
        [InlineData(double.PositiveInfinity, double.NaN)]
        public void БезРазмеровСкругленияНет(double width, double height)
            => Assert.Equal(new CornerRadius(0), PillCornerConverter.For(width, height));

        /// <summary>
        /// Бейдж со стилем из темы после разметки — пилюля: радиус равен половине его
        /// настоящей высоты, какой бы она ни вышла от шрифта и отступов.
        /// </summary>
        [Fact]
        public void БейджСоСтилемТемыРисуетсяПилюлей()
            => UiThread.Run(() => {
                var theme = (ResourceDictionary)Application.LoadComponent(
                    new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative));

                var badge = new Border {
                    Padding = new Thickness(9, 3, 9, 3),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "Установлена", FontSize = 11 },
                };
                var root = new Grid { Width = 400, Height = 200 };
                root.Resources.MergedDictionaries.Add(theme);
                root.Children.Add(badge);
                badge.Style = (Style)theme["Border.Pill"];

                root.Measure(new Size(400, 200));
                root.Arrange(new Rect(0, 0, 400, 200));
                root.UpdateLayout();

                Assert.True(badge.ActualWidth > badge.ActualHeight, "бейдж должен быть шире своей высоты");
                var r = badge.CornerRadius;
                Assert.Equal(badge.ActualHeight / 2, r.TopLeft, 3);
                Assert.True(
                    r.TopLeft == r.TopRight && r.TopLeft == r.BottomRight && r.TopLeft == r.BottomLeft,
                    $"углы разные: {r}");
            });

        /// <summary>
        /// Радиус «с запасом» в разметке — это овал, а не пилюля. Пилюли берут стиль
        /// Border.Pill; здесь ловится возврат к числу.
        /// </summary>
        [Fact]
        public void ВРазметкеНетРадиусаСЗапасом() {
            var launcher = Path.Combine(FindRepoRoot(), "launcher", "ChillHub");
            var big = new Regex(@"CornerRadius=""(\d+)""", RegexOptions.CultureInvariant);

            var offenders = Directory.EnumerateFiles(launcher, "*.xaml", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .SelectMany(p => big.Matches(Comments.Replace(File.ReadAllText(p), string.Empty))
                    .Where(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 50)
                    .Select(m => $"{Path.GetFileName(p)}: {m.Value}"))
                .ToList();

            Assert.True(offenders.Count == 0, "радиус вместо пилюли: " + string.Join(", ", offenders));
        }

        private static string FindRepoRoot() {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null) {
                if (File.Exists(Path.Combine(current.FullName, "CLAUDE.md")) &&
                    Directory.Exists(Path.Combine(current.FullName, "launcher"))) {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"не нашли корень репозитория, поднимаясь от {AppContext.BaseDirectory}");
        }
    }
}
