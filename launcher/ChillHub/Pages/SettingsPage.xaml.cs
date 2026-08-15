// <copyright file="SettingsPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.Settings;
    using ChillHub.Core.Shell;

    public partial class SettingsPage : Page {
        public SettingsPage() {
            this.InitializeComponent();

            // Defer UI population until the page is fully loaded to avoid template/resource init races (seen in dark theme)
            this.Loaded += this.SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e) {
            // Ensure templates/resources are fully applied (especially in dark theme)
            this.Dispatcher.BeginInvoke(
                new Action(() => {
                    try {
                        this.LoadConfigToUi();
                    }
                    catch (Exception ex) {
                        // Страница остаётся открытой с пустыми полями: пользователь может
                        // зайти в настройки повторно, а причина будет видна в логе.
                        ChillHub.Core.Logging.Logger.Error(ex, "SettingsPage.LoadConfigToUi");
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void LoadConfigToUi() {
            var view = SettingsView.Build(ConfigService.Current);
            if (this.GamesPathBox != null) {
                this.GamesPathBox.Text = view.GamesPath;
            }

            if (this.ThreadsSlider != null) {
                this.ThreadsSlider.Value = view.DownloadThreads;
            }

            if (this.ThreadsValueText != null) {
                this.ThreadsValueText.Text = view.DownloadThreadsText;
            }

            if (this.SpeedLimitSlider != null) {
                this.SpeedLimitSlider.Value = view.SpeedLimitMbps;
            }

            if (this.SpeedLimitValueText != null) {
                this.SpeedLimitValueText.Text = view.SpeedLimitText;
            }

            if (this.UsageMetricsCheck != null) {
                this.UsageMetricsCheck.IsChecked = view.SendUsageMetrics;
            }

            if (this.AutoErrorReportsCheck != null) {
                this.AutoErrorReportsCheck.IsChecked = view.AutoErrorReports;
            }

            if (this.MinimizeToTrayCheck != null) {
                this.MinimizeToTrayCheck.IsChecked = view.MinimizeToTray;
            }

            if (this.VersionText != null) {
                this.VersionText.Text = view.VersionText;
            }

            // Single dark theme now; no theme selection UI
        }

        private void OpenLogsBtn_Click(object sender, RoutedEventArgs e) => SettingsActions.OpenLogsFolder();

        private void BackBtn_Click(object sender, RoutedEventArgs e) {
            if (ShellNavigation.ShouldGoBack(this.NavigationService != null, this.NavigationService?.CanGoBack == true)) {
                this.NavigationService!.GoBack();
                return;
            }

            // Переиспользуем единственный HomePage, иначе получим вторую копию страницы
            // со своим FeedbackService и своей очередью сообщений
            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            win?.NavigateToHome();
        }

        private void ChooseBtn_Click(object sender, RoutedEventArgs e) {
            var chosen = SettingsActions.ChooseGamesFolder(this.GamesPathBox.Text);
            if (chosen != null) {
                this.GamesPathBox.Text = chosen;
            }
        }

        private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (this.ThreadsValueText != null) {
                this.ThreadsValueText.Text = ((int)this.ThreadsSlider.Value).ToString();
            }
        }

        private void SpeedLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (this.SpeedLimitValueText != null) {
                this.SpeedLimitValueText.Text = SettingsView.FormatSpeedLimit((int)this.SpeedLimitSlider.Value);
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e) {
            var saved = SettingsActions.Save(new SettingsInput {
                GamesPathText = this.GamesPathBox.Text,
                DownloadThreads = this.ThreadsSlider.Value,
                SpeedLimitMbps = this.SpeedLimitSlider.Value,
                AutoErrorReports = this.AutoErrorReportsCheck == null ? null : this.AutoErrorReportsCheck.IsChecked == true,
                SendUsageMetrics = this.UsageMetricsCheck == null ? null : this.UsageMetricsCheck.IsChecked == true,
                MinimizeToTray = this.MinimizeToTrayCheck == null ? null : this.MinimizeToTrayCheck.IsChecked == true,
            });
            if (!saved) {
                return;
            }

            try {
                // Мгновенно обновим цвет заголовка окна согласно новой теме
                var win = Window.GetWindow(this) as ChillHub.MainWindow;
                if (win != null) {
                    bool isDark = true; // single dark theme
                    ChillHub.Core.UI.AcrylicHelper.ApplyTitleBarTheme(win, isDark);
                }
            }
            catch (Exception ex) {
                // Цвет заголовка окна — косметика; настройки уже сохранены
                ChillHub.Core.Logging.Logger.Warn($"SettingsPage.SaveBtn: тема заголовка не применена: {ex.Message}");
            }
        }
    }
}
