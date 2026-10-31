namespace ChillHub.Tests {
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using Xunit;

    public sealed class SecurityHelperThemeTests {
        [Fact]
        public void HelperCanRenderItsOwnCopyOfTheSharedTheme() => UiThread.Run(() => {
            var theme = (ResourceDictionary)Application.LoadComponent(new Uri("/ChillHub.SecurityHelper;component/Themes/Theme.Dark.xaml", UriKind.Relative));
            var root = new StackPanel();
            root.Resources.MergedDictionaries.Add(theme);
            var primary = new Button { Content = "Настроить", Style = (Style)theme["Style.Button.Primary"] };
            var details = new Expander { Header = "Что не получилось", Content = new TextBox { Text = "Проверено", IsReadOnly = true }, IsExpanded = true, Style = (Style)theme["Style.Expander.Compact"] };
            root.Children.Add(new ComboBox { ItemsSource = new[] { "Microsoft Defender" }, SelectedIndex = 0 });
            root.Children.Add(new CheckBox { Content = "Папка игры", IsChecked = true });
            root.Children.Add(primary); root.Children.Add(details);
            root.Measure(new Size(600, 500)); root.Arrange(new Rect(0, 0, 600, 500)); root.UpdateLayout();
            Assert.NotNull(primary.Template);
            Assert.True(primary.ActualHeight >= 30);
            Assert.True(((TextBox)details.Content).ActualHeight > 0);
        });
    }
}
