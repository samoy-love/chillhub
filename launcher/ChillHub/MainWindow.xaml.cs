using System.Windows;
using System.Windows.Controls;
using System;
using System.Windows.Threading;

namespace ChillHub
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _resizeTimer;
        public MainWindow()
        {
            InitializeComponent();
            Console.WriteLine("[BOOT] Showing MainWindow");
            _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _resizeTimer.Tick += (s, e) =>
            {
                _resizeTimer.Stop();
                var w = (int)this.ActualWidth;
                var h = (int)this.ActualHeight;
                Console.WriteLine($"[UI] MainWindow resized: {w}x{h}");
            };
            this.SizeChanged += MainWindow_SizeChanged;
            ContentFrame.Navigate(new Pages.HomePage());
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Debounce resize events: only log after resizing has stopped for a short interval
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void CatalogBtn_Click(object sender, RoutedEventArgs e)
        {
            ContentFrame.Navigate(new Pages.HomePage());
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Do not re-open Settings if it's already shown
                if (ContentFrame.Content is Pages.SettingsPage)
                {
                    return;
                }
                ContentFrame.Navigate(new Pages.SettingsPage());
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Не удалось открыть страницу настроек: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Theme toggle removed: single dark theme is used
    }
}
