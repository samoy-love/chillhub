namespace ChillHub.Tests {
    using System;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    using ChillHub.Core.Game;
    using ChillHub.Core.Home;
    using ChillHub.Core.UI;

    using Xunit;

    public class ButtonLayoutTests {
        [Theory]
        [InlineData(null)]
        [InlineData("Style.Button.Primary")]
        [InlineData("Style.Button.GhostNeutral")]
        [InlineData("Style.Button.Danger")]
        [InlineData("Style.Button.HeroInfo")]
        public void ContentHasEqualInsetsAndFitsWithoutClipping(string? styleKey) => UiThread.Run(() => {
            var theme = Theme();
            foreach (var text in new[] { "Играть", "Установить", "Обновить", "Повторить", "Отмена", "Убрать из очереди", "Проверка…", "Удаление…", "Технические работы", "Нужна копия в Steam", "Обновить и перезапустить", "Настроить исключения антивируса" }) {
                foreach (var iconKey in theme.Keys.Cast<object>().OfType<string>().Where(key => key.StartsWith("Icon.", StringComparison.Ordinal))) {
                    var button = new Button { Content = text, MinWidth = 180, Margin = default };
                    button.Style = (Style)theme[styleKey ?? (object)typeof(Button)];
                    ButtonIcon.SetData(button, (Geometry)theme[iconKey]);
                    Layout(button, theme);

                    var pair = Part<StackPanel>(button, "ButtonContent");
                    var icon = Part<Viewbox>(button, "ButtonIcon");
                    var label = Part<ContentPresenter>(button, "ButtonLabel");
                    var origin = pair.TransformToAncestor(button).Transform(default);
                    Assert.InRange(Math.Abs(origin.X - (button.ActualWidth - origin.X - pair.ActualWidth)), 0, 1);
                    Assert.InRange(Math.Abs(origin.Y - (button.ActualHeight - origin.Y - pair.ActualHeight)), 0, 1);
                    Assert.True(origin.X >= button.Padding.Left, $"{styleKey}: {text}");
                    Assert.True(label.ActualWidth >= label.DesiredSize.Width - 1, text);
                    var iconCenter = icon.TransformToAncestor(pair).Transform(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2));
                    var labelCenter = label.TransformToAncestor(pair).Transform(new Point(label.ActualWidth / 2, label.ActualHeight / 2));
                    Assert.InRange(Math.Abs(iconCenter.Y - labelCenter.Y), 0, 1);
                    Assert.InRange(label.TransformToAncestor(pair).Transform(default).X - icon.ActualWidth, 7.5, 8.5);
                }
            }
        });

        [Theory]
        [InlineData("Style.Button.QueueIcon")]
        [InlineData("Style.Button.QueueIcon.Danger")]
        [InlineData("Style.Button.GhostNeutral")]
        public void IconOnlyHasNoPhantomTextGap(string styleKey) => UiThread.Run(() => {
            var theme = Theme();
            foreach (var content in new string?[] { null, string.Empty }) {
                var button = new Button { Content = content, Width = 28, Height = 28, Padding = default, Margin = default, Style = (Style)theme[styleKey] };
                ButtonIcon.SetData(button, (Geometry)theme["Icon.Close"]);
                Layout(button, theme);
                var icon = Part<Viewbox>(button, "ButtonIcon");
                var center = icon.TransformToAncestor(button).Transform(new Point(9, 9));
                Assert.InRange(center.X, 13.5, 14.5);
                Assert.InRange(center.Y, 13.5, 14.5);
                Assert.Equal(Visibility.Collapsed, Part<ContentPresenter>(button, "ButtonLabel").Visibility);
            }
        });

        [Fact]
        public void ChangingBetweenTextAndIconsDoesNotLeaveAnEmptySlot() => UiThread.Run(() => {
            var theme = Theme();
            var button = new Button { Content = "Поделиться идеей", Style = (Style)theme[typeof(Button)] };
            var root = Layout(button, theme);
            Assert.Equal(Visibility.Collapsed, Part<Viewbox>(button, "ButtonIcon").Visibility);
            ButtonIcon.SetData(button, (Geometry)theme["Icon.Play"]);
            root.UpdateLayout();
            Assert.Equal(Visibility.Visible, Part<Viewbox>(button, "ButtonIcon").Visibility);
            ButtonIcon.SetData(button, null);
            root.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, Part<Viewbox>(button, "ButtonIcon").Visibility);
        });

        [Fact]
        public void EveryHomeActionKeepsItsContentCentered() => UiThread.Run(() => {
            var page = HeroLayoutTests.LoadPage();
            var button = (Button)page.FindName("ActionBtn");
            foreach (var mode in Enum.GetValues<ActionMode>()) {
                foreach (var run in Enum.GetValues<GameRunState>()) {
                    var look = ActionButtonState.Appearance(mode, run);
                    button.Content = look.Content;
                    button.Style = (Style)page.FindResource(look.StyleKey);
                    button.IsEnabled = look.IsEnabled;
                    ButtonIcon.ApplyGlyph(button, ActionButtonState.Glyph(mode, run));
                    button.Visibility = Visibility.Visible;
                    page.Measure(new Size(960, 530));
                    page.Arrange(new Rect(0, 0, 960, 530));
                    page.UpdateLayout();
                    var pair = Part<StackPanel>(button, "ButtonContent");
                    var origin = pair.TransformToAncestor(button).Transform(default);
                    Assert.InRange(Math.Abs(origin.X - (button.ActualWidth - origin.X - pair.ActualWidth)), 0, 1);
                    Assert.True(pair.ActualHeight <= button.ActualHeight - button.Padding.Top - button.Padding.Bottom, look.Content);
                    Assert.Equal(mode == ActionMode.Play && run == GameRunState.None, ButtonIcon.GetIsFilled(button));
                }
            }
        });

        private static ResourceDictionary Theme() => (ResourceDictionary)Application.LoadComponent(
            new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative));

        private static StackPanel Layout(Button button, ResourceDictionary theme) {
            var root = new StackPanel { Resources = theme, Orientation = Orientation.Horizontal };
            button.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(button);
            root.Measure(new Size(1000, 100));
            root.Arrange(new Rect(0, 0, 1000, 100));
            root.UpdateLayout();
            return root;
        }

        private static T Part<T>(Button button, string name) where T : FrameworkElement
            => Assert.Single(VisualTreeSearch.Descendants<T>(button), element => element.Name == name);
    }
}
