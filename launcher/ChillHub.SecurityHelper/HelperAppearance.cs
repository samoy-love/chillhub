namespace ChillHub.SecurityHelper {
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    internal static class HelperAppearance {
        internal static void Apply(Window window, string title) {
            window.Title = "Chill Hub — " + title;
            window.Icon = new BitmapImage(new Uri("pack://application:,,,/ChillHub.SecurityHelper;component/Assets/app.ico"));
            window.SetResourceReference(Control.BackgroundProperty, "Brush.Background");
            window.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
            window.FontFamily = new FontFamily("Segoe UI");
            window.FontSize = 14;
            window.UseLayoutRounding = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.SourceInitialized += (_, _) => Core.UI.AcrylicHelper.ApplyTitleBarTheme(window, true);
        }

        internal static Border Card(UIElement content) {
            var card = new Border { Child = content, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
            card.SetResourceReference(FrameworkElement.StyleProperty, "Style.Card");
            return card;
        }

        internal static TextBlock Text(string text, string color = "Brush.TextSecondary", double size = 13) {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size };
            label.SetResourceReference(TextBlock.ForegroundProperty, color);
            return label;
        }

        internal static bool Confirm(string[] paths, bool removing) {
            var window = new Window { Width = 660, MaxHeight = Math.Min(760, SystemParameters.WorkArea.Height - 48), SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
            Apply(window, "подтверждение настройки защиты");
            var content = new StackPanel { Margin = new Thickness(24) };
            window.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            content.Children.Add(Text(removing ? "Удалить исключения Defender?" : "Добавить исключения Defender?", "Brush.Title", 22));
            var description = Text(removing ? "Файлы снова будут проверяться, если их не покрывают другие исключения." : Security.ExclusionPolicy.Warning);
            description.Margin = new Thickness(0, 12, 0, 18);
            content.Children.Add(description);
            content.Children.Add(Card(Text(string.Join("\n\n", paths), "Brush.Text")));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Отмена", MinWidth = 110, MinHeight = 38, IsCancel = true, IsDefault = true, Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 0, 10, 0) };
            var confirm = new Button { Content = removing ? "Удалить исключения" : "Добавить исключения", MinWidth = 180, MinHeight = 38, Padding = new Thickness(18, 10, 18, 10) };
            confirm.SetResourceReference(FrameworkElement.StyleProperty, "Style.Button.Primary");
            confirm.Click += (_, _) => window.DialogResult = true;
            buttons.Children.Add(cancel); buttons.Children.Add(confirm); content.Children.Add(buttons);
            return window.ShowDialog() == true;
        }
    }
}
