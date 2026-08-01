// <copyright file="SettingsPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.IO;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core;

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
                    catch { /* prevent crash; user can reopen */
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void LoadConfigToUi() {
            var cfg = ConfigService.Current ?? new AppConfig();
            if (this.GamesPathBox != null) {
                // Отображаем путь с одинарными обратными слешами для читаемости
                var p = cfg.GamesPath ?? string.Empty;
                this.GamesPathBox.Text = p.Replace("\\\\", "\\");
            }

            if (this.ThreadsSlider != null) {
                this.ThreadsSlider.Value = cfg.DownloadThreads;
            }

            if (this.ThreadsValueText != null) {
                this.ThreadsValueText.Text = cfg.DownloadThreads.ToString();
            }

            if (this.AutoErrorReportsCheck != null) {
                this.AutoErrorReportsCheck.IsChecked = cfg.AutoErrorReports;
            }

            if (this.VersionText != null) {
                this.VersionText.Text = GetLauncherVersion();
            }

            // Single dark theme now; no theme selection UI
        }

        /// <summary>
        /// Версия лаунчера: сначала маркер launcher.version рядом с exe (его пишет апдейтер),
        /// иначе — версия сборки. Своя маленькая копия логики, чтобы не тянуть зависимость от UpdateWindow.
        /// </summary>
        private static string GetLauncherVersion() {
            try {
                var markerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.version");
                if (File.Exists(markerPath)) {
                    var marker = (File.ReadAllText(markerPath) ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(marker)) {
                        return marker;
                    }
                }
            }
            catch {
            }

            try {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) {
                    return $"{v.Major}.{v.Minor}.{v.Build}";
                }
            }
            catch {
            }

            return "неизвестно";
        }

        private void OpenLogsBtn_Click(object sender, RoutedEventArgs e) {
            try {
                // Путь берём у Logger: логи переехали из %TEMP% (его чистит система
                // вместе с отчётами) в %APPDATA%\ChillHub, к остальному состоянию.
                var dir = ChillHub.Core.Logging.Logger.LogDirectory;
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) {
                MessageBox.Show($"Не удалось открыть папку с логами: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e) {
            if (this.NavigationService != null && this.NavigationService.CanGoBack) {
                this.NavigationService.GoBack();
                return;
            }

            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            win?.ContentFrame.Navigate(new HomePage());
        }

        private void ChooseBtn_Click(object sender, RoutedEventArgs e) {
            try {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog()) {
                    dlg.Description = "Выберите папку для игр";
                    dlg.ShowNewFolderButton = true;
                    dlg.SelectedPath = string.IsNullOrWhiteSpace(this.GamesPathBox.Text)
                        ? AppConfig.DefaultGamesPath()
                        : this.GamesPathBox.Text;
                    var res = dlg.ShowDialog();
                    if (res == System.Windows.Forms.DialogResult.OK) {
                        // Нормализуем отображение: одинарные обратные слеши
                        var sp = dlg.SelectedPath ?? string.Empty;
                        this.GamesPathBox.Text = sp.Replace("\\\\", "\\");
                    }
                }
            }
            catch {
            }
        }

        private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (this.ThreadsValueText != null) {
                this.ThreadsValueText.Text = ((int)this.ThreadsSlider.Value).ToString();
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e) {
            try {
                var cfg = ConfigService.Current;
                var newPath = this.GamesPathBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(newPath)) {
                    newPath = AppConfig.DefaultGamesPath();
                }

                try {
                    // Для файловой системы и конфигурации используем нормальную форму с одинарными слешами
                    newPath = newPath.Replace("\\\\", "\\");
                    Directory.CreateDirectory(newPath);
                }
                catch {
                }

                cfg.GamesPath = newPath;
                cfg.DownloadThreads = (int)this.ThreadsSlider.Value;
                if (this.AutoErrorReportsCheck != null) {
                    cfg.AutoErrorReports = this.AutoErrorReportsCheck.IsChecked == true;
                }

                ConfigService.Save(cfg); // также применяет тему
                try {
                    // Мгновенно обновим цвет заголовка окна согласно новой теме
                    var win = Window.GetWindow(this) as ChillHub.MainWindow;
                    if (win != null) {
                        bool isDark = true; // single dark theme
                        ChillHub.Core.UI.AcrylicHelper.ApplyTitleBarTheme(win, isDark);
                    }
                }
                catch {
                }
            }
            catch (Exception ex) {
                MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
