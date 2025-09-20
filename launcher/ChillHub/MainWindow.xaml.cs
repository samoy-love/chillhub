// <copyright file="MainWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub
{
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Threading;

    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer resizeTimer;

        public MainWindow()
        {
            this.InitializeComponent();
            Console.WriteLine("[BOOT] Showing MainWindow");
            this.resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            this.resizeTimer.Tick += (s, e) =>
            {
                this.resizeTimer.Stop();
                var w = (int)this.ActualWidth;
                var h = (int)this.ActualHeight;
                Console.WriteLine($"[UI] MainWindow resized: {w}x{h}");
            };
            this.SizeChanged += this.MainWindow_SizeChanged;
            this.ContentFrame.Navigate(new Pages.HomePage());
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Debounce resize events: only log after resizing has stopped for a short interval
            this.resizeTimer.Stop();
            this.resizeTimer.Start();
        }

        private void CatalogBtn_Click(object sender, RoutedEventArgs e)
        {
            this.ContentFrame.Navigate(new Pages.HomePage());
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Do not re-open Settings if it's already shown
                if (this.ContentFrame.Content is Pages.SettingsPage)
                {
                    return;
                }

                this.ContentFrame.Navigate(new Pages.SettingsPage());
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Не удалось открыть страницу настроек: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Theme toggle removed: single dark theme is used
    }
}
