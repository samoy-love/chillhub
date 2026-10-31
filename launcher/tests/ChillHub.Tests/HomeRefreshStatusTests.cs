namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.UI;
    using ChillHub.Pages;

    using Xunit;

    public class HomeRefreshStatusTests {
        [Theory]
        [InlineData("Готово", false, 0, Visibility.Collapsed)]
        [InlineData("Удаление файлов…", true, 0, Visibility.Visible)]
        [InlineData("Скачивание…", false, 42, Visibility.Visible)]
        [InlineData("Не удалось скачать файлы", false, 0, Visibility.Visible)]
        public void CatalogVerificationDoesNotTouchOperationPanel(string status, bool indeterminate, double progress, Visibility expectedVisibility) => UiThread.Run(async () => {
            using var temp = new TempDir();
            using var config = new ConfigDirsScope();
            ConfigService.Current.GamesPath = temp.Root;
            using var http = new HttpClient(new CatalogHandler());
            var page = CreatePage();
            using var queue = (IDisposable)Field("downloadQueue").GetValue(page)!;
            Field("http").SetValue(page, http);
            Field("galleryClient").SetValue(page, new Core.Game.GalleryClient(http));
            Field("allowFileChecks").SetValue(page, true);
            var games = new List<GameInfo> { new() { GameId = "demo", Title = "Demo" } };
            Field("games").SetValue(page, games);
            var text = (TextBlock)page.FindName("StatusText");
            var bar = (ProgressBar)page.FindName("UpdateProgress");
            var panel = (FrameworkElement)page.FindName("BottomBar");
            text.Text = status;
            bar.IsIndeterminate = indeterminate;
            bar.Value = progress;
            // BusyGate показывает панель с задержкой. Замеряем уже показанную операцию,
            // иначе её штатное появление на быстром раннере принимается за скачок проверки.
            await UiThread.WaitUntil(() => panel.Visibility == expectedVisibility, "исходное состояние панели");
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
                    if (i == 0) {
                        await (Task)typeof(HomePage).GetMethod("VerifyAllGamesStatusesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                            .Invoke(page, new object?[] { "demo" })!;
                    }
                    else if (i == 1) {
                        await page.RefreshGamesAsync();
                    }
                    else {
                        await page.RefreshCatalogQuietlyAsync();
                    }

                    await UiThread.WaitUntil(() => Field("selectionCts").GetValue(page) == null, "обновление выбора игры");
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

        [Fact]
        public void CatalogRebindPreservesDownloadError() => UiThread.Run(async () => {
            using var temp = new TempDir();
            using var config = new ConfigDirsScope();
            ConfigService.Current.GamesPath = temp.Root;
            using var http = new HttpClient(new CatalogHandler());
            var page = CreatePage();
            using var queue = (IDisposable)Field("downloadQueue").GetValue(page)!;
            Field("http").SetValue(page, http);
            Field("galleryClient").SetValue(page, new Core.Game.GalleryClient(http));
            var game = new GameInfo { GameId = "demo", Title = "Demo" };
            var games = new List<GameInfo> { game };
            Field("games").SetValue(page, games);
            Field("allowFileChecks").SetValue(page, true);
            var list = (ListBox)page.FindName("GameList");
            list.ItemsSource = games;
            list.SelectedItem = game;
            await UiThread.WaitUntil(() => Field("selectionCts").GetValue(page) == null, "выбор игры");
            Field("updateErrorGameId").SetValue(page, "demo");
            Field("lastErrorDetails").SetValue(page, "download failed");
            var text = (TextBlock)page.FindName("StatusText");
            text.Text = "Не удалось скачать файлы";
            text.ToolTip = "download failed";

            await page.RefreshCatalogQuietlyAsync();
            await UiThread.WaitUntil(() => Field("selectionCts").GetValue(page) == null, "выбор после обновления");

            Assert.Equal(2, list.Items.Count);
            Assert.Same(game, list.SelectedItem);
            Assert.Equal("demo", Field("updateErrorGameId").GetValue(page));
            Assert.Equal("download failed", Field("lastErrorDetails").GetValue(page));
            Assert.Equal("download failed", text.ToolTip);
            Assert.Equal("Не удалось скачать файлы", text.Text);
        });

        [Theory]
        [InlineData("Удаление файлов Demo…", "")]
        [InlineData("Не удалось скачать файлы", "Не удалось скачать файлы")]
        public void CompletedDeletionClearsOnlyItsOwnStatus(string current, string expected) => UiThread.Run(() => {
            using var config = new ConfigDirsScope();
            var page = CreatePage();
            using var queue = (IDisposable)Field("downloadQueue").GetValue(page)!;
            var text = (TextBlock)page.FindName("StatusText");
            text.Text = current;
            page.CompleteFileDeletionStatus("Удаление файлов Demo…");
            Assert.Equal(expected, text.Text);
        });

        [Fact]
        public void DetachedPagePublishesFreshAndEmptyCatalogsForCleanup() => UiThread.Run(async () => {
            using var temp = new TempDir();
            using var config = new ConfigDirsScope();
            ConfigService.Current.GamesPath = temp.Root;
            using var handler = new CatalogHandler();
            using var http = new HttpClient(handler);
            var page = CreatePage();
            using var queue = (IDisposable)Field("downloadQueue").GetValue(page)!;
            Field("http").SetValue(page, http);
            Field("galleryClient").SetValue(page, new Core.Game.GalleryClient(http));
            Field("games").SetValue(page, new List<GameInfo> { new() { GameId = "demo", Title = "Demo" } });
            Field("allowFileChecks").SetValue(page, true);
            var check = new Core.Home.MissingGamesCheck();
            await check.ScanAsync(temp.Root, Array.Empty<GameInfo>());
            var responses = 0;
            page.CatalogReceived += games => {
                responses++;
                _ = check.ScanAsync(temp.Root, games);
            };
            Assert.Null(Window.GetWindow(page));

            await page.RefreshCatalogQuietlyAsync();
            Assert.True(check.IsAvailable("new"));
            handler.CatalogJson = "{\"items\":[]}";
            await page.RefreshCatalogQuietlyAsync();
            Assert.False(check.IsAvailable("new"));
            Assert.Equal(2, responses);
        });

        private static HomePage CreatePage() {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative)));
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
            return new HomePage(startServices: false, resources);
        }

        private sealed class CatalogHandler : HttpMessageHandler {
            internal string CatalogJson { get; set; } = "{\"items\":[{\"gameId\":\"demo\",\"title\":\"Demo\"},{\"gameId\":\"new\",\"title\":\"New\"}]}";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                var json = request.RequestUri!.AbsolutePath == "/api/games"
                    ? this.CatalogJson
                    : "{\"items\":[]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
        }

        private static FieldInfo Field(string name) => typeof(HomePage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    }
}
