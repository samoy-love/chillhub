namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    using ChillHub.Controls;
    using ChillHub.Core.Home;

    using Xunit;

    public class MissingGamesDialogTests {
        [Fact]
        public void SelectionAndCloseNeverDeleteWithoutConfirmation() => UiThread.Run(() => {
            var calls = 0;
            var dialog = CreateDialog(2, _ => { calls++; return Task.FromResult(MissingGamesText.Deleted); });
            var rows = Rows(dialog);
            Assert.All(rows, r => Assert.True(r.IsSelected));
            foreach (var row in rows) {
                row.IsSelected = false;
            }

            Assert.False(((Button)dialog.FindName("DeleteButton")).IsEnabled);
            rows[0].IsSelected = true;
            Assert.True(((Button)dialog.FindName("DeleteButton")).IsEnabled);
            var closed = false;
            dialog.Closed += () => closed = true;
            dialog.Close();
            Assert.True(closed);
            Assert.Equal(0, calls);
        });

        [Fact]
        public void DeleteUsesSelectionBlocksCloseAndContinuesAfterFailure() => UiThread.Run(async () => {
            var first = new TaskCompletionSource<string>();
            var calls = new List<string>();
            var dialog = CreateDialog(3, folder => {
                calls.Add(folder.Name);
                return calls.Count == 1 ? first.Task : Task.FromResult(MissingGamesText.Deleted);
            });
            var rows = Rows(dialog);
            rows[1].IsSelected = false;
            var closed = false;
            dialog.Closed += () => closed = true;
            var task = dialog.DeleteSelectedAsync();
            Assert.True(dialog.IsBusy);
            Assert.All(rows, row => Assert.False(row.CanSelect));
            dialog.Close();
            Assert.False(closed);
            await dialog.DeleteSelectedAsync();
            Assert.Single(calls);
            first.SetException(new IOException("locked"));
            await task;
            Assert.Equal(new[] { rows[0].Folder.Name, rows[2].Folder.Name }, calls);
            Assert.Equal(MissingGamesText.Failed, rows[0].Result);
            Assert.Equal(string.Empty, rows[1].Result);
            Assert.Equal(MissingGamesText.Deleted, rows[2].Result);
            Assert.False(dialog.IsBusy);
            await dialog.DeleteSelectedAsync();
            Assert.Equal(2, calls.Count);
            dialog.Close();
            Assert.True(closed);
        });

        [Theory]
        [InlineData(980, 640, 96)]
        [InlineData(980, 640, 144)]
        [InlineData(1280, 800, 96)]
        public void DialogFitsWindowAndLongListScrolls(int width, int height, int dpi) => UiThread.Run(async () => {
            var dialog = CreateDialog(14, _ => Task.FromResult(MissingGamesText.Deleted));
            dialog.Resources.MergedDictionaries.Add(new ResourceDictionary {
                Source = new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative),
            });
            dialog.Measure(new Size(width, height));
            dialog.Arrange(new Rect(0, 0, width, height));
            dialog.UpdateLayout();
            await UiThread.Settle();
            var button = (Button)dialog.FindName("DeleteButton");
            var position = button.TranslatePoint(new Point(0, 0), dialog);
            Assert.InRange(position.X, 0, width - button.ActualWidth);
            Assert.InRange(position.Y, 0, height - button.ActualHeight);
            var scroll = FindVisual<ScrollViewer>(dialog)!;
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);

            var directory = Environment.GetEnvironmentVariable("CHILLHUB_UI_CAPTURE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(directory)) {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(width * dpi / 96, height * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(dialog);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"missing-games-{width}-{height}-{dpi}.png"));
                encoder.Save(file);
            }
        });

        private static MissingGamesDialog CreateDialog(int count, Func<MissingGameFolder, Task<string>> delete) {
            var folders = Enumerable.Range(1, count).Select(i => new MissingGameFolder(
                $"Игра {i}", $@"D:\Games\ChillHub\Игра {i} — папка с длинным названием и сохранениями игрока")).ToArray();
            return new MissingGamesDialog(new MissingGamesSnapshot(@"D:\Games\ChillHub", folders), delete);
        }

        private static MissingGamesDialog.MissingGameRow[] Rows(MissingGamesDialog dialog)
            => ((ItemsControl)dialog.FindName("FolderList")).Items.Cast<MissingGamesDialog.MissingGameRow>().ToArray();

        private static T? FindVisual<T>(DependencyObject parent)
            where T : DependencyObject {
            if (parent is T found) {
                return found;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                var child = FindVisual<T>(VisualTreeHelper.GetChild(parent, i));
                if (child != null) {
                    return child;
                }
            }

            return null;
        }
    }
}
