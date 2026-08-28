// <copyright file="PlaceholderAlignmentTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Подсказка в пустом поле стоит ровно там, где после клика появится каретка.
    /// <para>
    /// Пока подсказка была отдельной надписью поверх поля, её отступы подбирались руками
    /// под отступы поля — и расходились с ними: в форме обратной связи текст подсказки
    /// стоял выше и левее места набора. Глазом такое ловится плохо, поэтому здесь оба
    /// места измеряются: каретка — через <see cref="TextBox.GetRectFromCharacterIndex"/>,
    /// подсказка — по её положению в дереве отрисовки.
    /// </para>
    /// </summary>
    public class PlaceholderAlignmentTests {
        /// <summary>Допуск в пикселях: округление разметки, но не сдвиг на отступ.</summary>
        private const double Tolerance = 1.0;

        /// <summary>Однострочное поле: текст по центру высоты, подсказка там же.</summary>
        [Fact]
        public void ПодсказкаОднострочногоПоляСтоитНаМестеКаретки()
            => AssertAligned("Style.TextBox.Field", "Ваше имя", width: 240, height: 36);

        /// <summary>Поле сообщения: текст от верхнего края, подсказка там же.</summary>
        [Fact]
        public void ПодсказкаПоляСообщенияСтоитНаМестеКаретки()
            => AssertAligned("Style.TextBox.Field.Multiline", "Опишите проблему или идею", width: 480, height: 160);

        /// <summary>Набранный текст прячет подсказку: иначе она ложится поверх него.</summary>
        [Fact]
        public void ПодсказкаПропадаетКакТолькоПоявляетсяТекст()
            => UiThread.Run(() => {
                var box = Field("Style.TextBox.Field", "Ваше имя", 240, 36, out var root);
                Assert.Equal(Visibility.Visible, Hint(box).Visibility);

                box.Text = "Алексей";
                root.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, Hint(box).Visibility);
            });

        private static void AssertAligned(string styleKey, string hintText, double width, double height)
            => UiThread.Run(() => {
                var box = Field(styleKey, hintText, width, height, out _);
                var hint = Hint(box);

                var caret = box.GetRectFromCharacterIndex(0);
                var hintOrigin = hint.TransformToAncestor(box).Transform(default);

                Assert.True(
                    Math.Abs(caret.X - hintOrigin.X) <= Tolerance,
                    $"{styleKey}: подсказка по горизонтали в {hintOrigin.X}, каретка в {caret.X}");
                Assert.True(
                    Math.Abs(caret.Y - hintOrigin.Y) <= Tolerance,
                    $"{styleKey}: подсказка по вертикали в {hintOrigin.Y}, каретка в {caret.Y}");
            });

        /// <summary>Поле нужного стиля в дереве с ресурсами темы, уже размеченное.</summary>
        private static TextBox Field(string styleKey, string hintText, double width, double height, out Grid root) {
            var theme = (ResourceDictionary)Application.LoadComponent(
                new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative));

            var box = new TextBox { Width = width, Height = height };
            Placeholder.SetText(box, hintText);

            root = new Grid();
            root.Resources.MergedDictionaries.Add(theme);
            root.Children.Add(box);
            box.Style = (Style)theme[styleKey];

            root.Measure(new Size(width + 40, height + 40));
            root.Arrange(new Rect(0, 0, width + 40, height + 40));
            root.UpdateLayout();
            return box;
        }

        private static TextBlock Hint(TextBox box)
            => Assert.Single(VisualTreeSearch.Descendants<TextBlock>(box), t => t.Name == "Hint");
    }
}
