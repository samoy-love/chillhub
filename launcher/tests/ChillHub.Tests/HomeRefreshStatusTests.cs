namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.UI;
    using ChillHub.Pages;

    using Xunit;

    public class HomeRefreshStatusTests {
        [Theory]
        [InlineData("Готово", false, 0)]
        [InlineData("Удаление файлов…", true, 0)]
        [InlineData("Скачивание…", false, 42)]
        [InlineData("Не удалось скачать файлы", false, 0)]
        public void CatalogVerificationDoesNotTouchOperationPanel(string status, bool indeterminate, double progress) => UiThread.Run(async () => {
            using var temp = new TempDir();
            using var config = new ConfigDirsScope();
            ConfigService.Current.GamesPath = temp.Root;
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new ResourceDictionary {
                Source = new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative),
            });
            resources["HeightShareConv"] = new HeightShareConverter();
            resources["AspectHeightConv"] = new AspectHeightConverter();
            resources["NewsColumnsConv"] = new NewsColumnsConverter();
            resources["RuDateConv"] = new RuDateConverter();
            resources["GameRowStatusTextConv"] = new GameRowStatusTextConverter();
            resources["GameRowStatusBrushConv"] = new GameRowStatusBrushConverter();
            resources["QueueItemPercentTextConv"] = new QueueItemPercentTextConverter();
            resources["QueueItemStatusConv"] = new QueueItemStatusConverter();
            resources["QueueItemStateVisibilityConv"] = new QueueItemStateVisibilityConverter();
            resources["QueueItemSizeConv"] = new QueueItemSizeConverter();
            resources["QueueItemSpeedConv"] = new QueueItemSpeedConverter();
            resources["QueueItemPercentConv"] = new QueueItemPercentConverter();
            resources["QueueItemIndeterminateConv"] = new QueueItemIndeterminateConverter();
            var page = new HomePage(startServices: false, resources);
            using var queue = (IDisposable)Field("downloadQueue").GetValue(page)!;
            var games = new List<GameInfo> { new() { GameId = "demo", Title = "Demo" } };
            Field("games").SetValue(page, games);
            var text = (TextBlock)page.FindName("StatusText");
            var bar = (ProgressBar)page.FindName("UpdateProgress");
            var panel = (FrameworkElement)page.FindName("BottomBar");
            text.Text = status;
            bar.IsIndeterminate = indeterminate;
            bar.Value = progress;
            page.Measure(new Size(980, 640));
            page.Arrange(new Rect(0, 0, 980, 640));
            page.UpdateLayout();
            var visibility = panel.Visibility;
            var height = panel.ActualHeight;
            var changed = false;
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(FrameworkElement));
            EventHandler listener = (_, __) => changed = true;
            descriptor.AddValueChanged(panel, listener);
            try {
                for (var i = 0; i < 3; i++) {
                    await (Task)typeof(HomePage).GetMethod("VerifyAllGamesStatusesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(page, new object?[] { "demo" })!;
                    page.RefreshLaunchOptionsFromDisk();
                    page.UpdateLayout();
                    Assert.Equal(status, text.Text);
                    Assert.Equal(indeterminate, bar.IsIndeterminate);
                    Assert.Equal(progress, bar.Value);
                    Assert.Equal(visibility, panel.Visibility);
                    Assert.Equal(height, panel.ActualHeight);
                }

                Assert.False(changed);
            }
            finally {
                descriptor.RemoveValueChanged(panel, listener);
            }
        });

        private static FieldInfo Field(string name) => typeof(HomePage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    }
}
