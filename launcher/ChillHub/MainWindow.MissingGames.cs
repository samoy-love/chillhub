namespace ChillHub {
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Input;
    using System.Windows.Threading;

    using ChillHub.Core.Home;

    public partial class MainWindow {
        private readonly MissingGamesCheck missingGamesCheck = new();
        private DispatcherTimer? missingGamesTimer;
        private Controls.MissingGamesDialog? missingGamesDialog;
        private bool missingGamesWindowClosed;

        private void AttachMissingGamesCheck(Pages.HomePage home) {
            // Страница продолжает обновлять каталог и вне визуального дерева.
            // Подписка живёт столько же, сколько само окно, а не только открытая главная.
            home.CatalogReceived += games => _ = this.CheckMissingGamesAsync(games, home.IsGameBusyForCleanup);
        }

        internal async Task CheckMissingGamesAsync(
            System.Collections.Generic.IReadOnlyList<Core.GameInfo> games,
            Func<string, bool> busy) {
            try {
                var snapshot = await this.missingGamesCheck.ScanAsync(Core.ConfigService.Current.GamesPath, games);
                if (snapshot == null || snapshot.Folders.Count == 0 || this.missingGamesWindowClosed || this.Dispatcher.HasShutdownStarted) {
                    return;
                }

                // Таймер ждёт только свободного UI, запросов к серверу здесь нет.
                this.missingGamesTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, __) => {
                    try {
                        if (!this.IsVisible || !this.IsActive || this.WindowState == WindowState.Minimized
                            || this.ChangelogOverlay.Visibility == Visibility.Visible
                            || this.ContentFrame.Content is not Pages.HomePage home || !home.CanShowMissingGames
                            || this.OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) {
                            return;
                        }

                        this.missingGamesTimer?.Stop();
                        this.missingGamesTimer = null;
                        var previousFocus = Keyboard.FocusedElement;
                        var dialog = new Controls.MissingGamesDialog(snapshot, folder => {
                            // Состояние UI снимаем до ухода в фон; путь перепроверяется перед каждой папкой.
                            var isBusy = busy(folder.Name);
                            return Task.Run(() => MissingGamesCleanup.DeleteFolder(
                                snapshot, folder, () => Core.ConfigService.Current.GamesPath,
                                _ => isBusy, this.missingGamesCheck.IsAvailable));
                        });
                        this.missingGamesDialog = dialog;
                        dialog.Closed += () => {
                            this.MissingGamesHost.Content = null;
                            this.MissingGamesHost.Visibility = Visibility.Collapsed;
                            this.MainContent.IsEnabled = true;
                            this.missingGamesDialog = null;
                            if (previousFocus is UIElement element && element.IsVisible && element.IsEnabled) {
                                Keyboard.Focus(element);
                            }
                        };
                        this.MainContent.IsEnabled = false;
                        this.MissingGamesHost.Content = dialog;
                        this.MissingGamesHost.Visibility = Visibility.Visible;
                    }
                    catch (Exception ex) {
                        this.missingGamesTimer?.Stop();
                        this.MissingGamesHost.Content = null;
                        this.MissingGamesHost.Visibility = Visibility.Collapsed;
                        this.MainContent.IsEnabled = true;
                        this.missingGamesDialog = null;
                        Core.Logging.Logger.Error(ex, "MissingGamesDialog.Show");
                    }
                }, this.Dispatcher);
                this.missingGamesTimer.Start();
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"MissingGamesCheck: {ex.Message}");
            }
        }
    }
}
