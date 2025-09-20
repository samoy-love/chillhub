using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChillHub.Core;

namespace ChillHub.Pages
{
    public partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            // Defer UI population until the page is fully loaded to avoid template/resource init races (seen in dark theme)
            this.Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Ensure templates/resources are fully applied (especially in dark theme)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { LoadConfigToUi(); }
                catch { /* prevent crash; user can reopen */ }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void LoadConfigToUi()
        {
            var cfg = ConfigService.Current ?? new AppConfig();
            if (GamesPathBox != null) GamesPathBox.Text = cfg.GamesPath;
            if (ThreadsSlider != null) ThreadsSlider.Value = cfg.DownloadThreads;
            if (ThreadsValueText != null) ThreadsValueText.Text = cfg.DownloadThreads.ToString();

            // Single dark theme now; no theme selection UI
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService != null && NavigationService.CanGoBack)
            {
                NavigationService.GoBack();
                return;
            }
            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            win?.ContentFrame.Navigate(new HomePage());
        }

        private void ChooseBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Выберите папку для игр";
                    dlg.ShowNewFolderButton = true;
                    dlg.SelectedPath = string.IsNullOrWhiteSpace(GamesPathBox.Text)
                        ? AppConfig.DefaultGamesPath()
                        : GamesPathBox.Text;
                    var res = dlg.ShowDialog();
                    if (res == System.Windows.Forms.DialogResult.OK)
                    {
                        GamesPathBox.Text = dlg.SelectedPath;
                    }
                }
            }
            catch { }
        }

        private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ThreadsValueText != null)
                ThreadsValueText.Text = ((int)ThreadsSlider.Value).ToString();
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = ConfigService.Current;
                var newPath = GamesPathBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(newPath))
                    newPath = AppConfig.DefaultGamesPath();

                try { Directory.CreateDirectory(newPath); } catch { }

                cfg.GamesPath = newPath;
                cfg.DownloadThreads = (int)ThreadsSlider.Value;

                ConfigService.Save(cfg); // также применяет тему
                try
                {
                    // Мгновенно обновим цвет заголовка окна согласно новой теме
                    var win = Window.GetWindow(this) as ChillHub.MainWindow;
                    if (win != null)
                    {
                        bool isDark = true; // single dark theme
                        ChillHub.Core.UI.AcrylicHelper.ApplyTitleBarTheme(win, isDark);
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}