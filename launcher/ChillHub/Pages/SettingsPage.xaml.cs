// <copyright file="SettingsPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media.Animation;
    using System.Windows.Threading;

    using ChillHub.Core;
    using ChillHub.Core.Settings;
    using ChillHub.Core.Shell;

    /// <summary>
    /// Страница настроек. Каждая правка применяется сразу (см. <see cref="ApplyNow"/>):
    /// кнопки «Сохранить» нет — с ней правки терялись у тех, кто уходил «Назад».
    /// </summary>
    public partial class SettingsPage : Page {
        /// <summary>
        /// Пока страница заполняется из конфига, обработчики контролов молчат — иначе
        /// первое же присвоение ползунку записало бы конфиг обратно на диск. Взведён с
        /// самого рождения страницы: ValueChanged ползунка стреляет уже внутри
        /// InitializeComponent (Minimum=2 подтягивает Value с 0 до 2), и без флага это
        /// записывало в конфиг «2 потока» поверх настоящего значения ещё до показа страницы.
        /// </summary>
        private bool loading = true;

        /// <summary>Гасит подпись «Сохранено» через пару секунд после последней правки.</summary>
        private readonly DispatcherTimer saveStatusTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

        public SettingsPage() {
            this.InitializeComponent();
            this.saveStatusTimer.Tick += (s, e) => {
                this.saveStatusTimer.Stop();
                this.SaveStatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
            };

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
            try {
                if (this.GamesPathBox != null) {
                    this.GamesPathBox.Text = view.GamesPath;
                }

                if (this.ThreadsSlider != null) {
                    this.ThreadsSlider.Value = view.DownloadThreads;
                }

                if (this.ThreadsBox != null) {
                    this.ThreadsBox.Text = view.DownloadThreadsText;
                }

                if (this.SpeedLimitCheck != null) {
                    this.SpeedLimitCheck.IsChecked = view.SpeedLimitMbps > 0;
                }

                if (this.SpeedLimitBox != null) {
                    this.SpeedLimitBox.Text = view.SpeedLimitMbps > 0 ? view.SpeedLimitMbps.ToString(CultureInfo.InvariantCulture) : "5";
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
            }
            finally {
                this.loading = false;
            }
        }

        private void OpenLogsBtn_Click(object sender, RoutedEventArgs e) => SettingsActions.OpenLogsFolder();

        private void BackBtn_Click(object sender, RoutedEventArgs e) {
            // Правка пути могла остаться в поле без потери фокуса — забираем её перед уходом
            this.ApplyNow();

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
                this.ApplyNow();
            }
        }

        private void GamesPathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => this.ApplyNow();

        private void GamesPathBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter) {
                this.ApplyNow();
                e.Handled = true;
            }
        }

        private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (this.ThreadsBox != null) {
                this.ThreadsBox.Text = ((int)this.ThreadsSlider.Value).ToString(CultureInfo.InvariantCulture);
            }

            this.ApplyNow();
        }

        private void ThreadsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
            // Число из поля — в ползунок; ползунок сам обрежет до 2..16 и применит
            if (int.TryParse(this.ThreadsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) {
                this.ThreadsSlider.Value = Math.Clamp(n, (int)this.ThreadsSlider.Minimum, (int)this.ThreadsSlider.Maximum);
            }

            this.ThreadsBox.Text = ((int)this.ThreadsSlider.Value).ToString(CultureInfo.InvariantCulture);
        }

        private void SpeedLimitCheck_Click(object sender, RoutedEventArgs e) => this.ApplyNow();

        private void SpeedLimitBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => this.ApplyNow();

        /// <summary>Enter в числовом поле — то же, что уйти из него: значение применяется.</summary>
        private void NumberBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter && sender is UIElement el) {
                // Снимаем фокус — сработает LostKeyboardFocus и всё, что к нему привязано
                el.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
        }

        private void Toggle_Click(object sender, RoutedEventArgs e) => this.ApplyNow();

        /// <summary>Значение лимита скорости, как его понимает конфиг: 0 — без лимита.</summary>
        private int ReadSpeedLimit() {
            if (this.SpeedLimitCheck?.IsChecked != true) {
                return 0;
            }

            if (!int.TryParse((this.SpeedLimitBox?.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mbps)) {
                mbps = 5;
            }

            // Тот же диапазон, что и в Config.Clamp: показываем то, что реально сохранится
            mbps = Math.Clamp(mbps, 1, 10);
            if (this.SpeedLimitBox != null) {
                this.SpeedLimitBox.Text = mbps.ToString(CultureInfo.InvariantCulture);
            }

            return mbps;
        }

        /// <summary>
        /// Записывает текущее состояние страницы в конфиг и на диск. Ошибку записи
        /// <see cref="SettingsActions.Save"/> показывает сам; здесь — только короткое
        /// «Сохранено» в шапке, чтобы было видно, что правка не пропала.
        /// </summary>
        private void ApplyNow() {
            if (this.loading || this.GamesPathBox == null || this.ThreadsSlider == null) {
                return;
            }

            var speed = this.ReadSpeedLimit();
            if (this.SpeedLimitValueText != null) {
                this.SpeedLimitValueText.Text = SettingsView.FormatSpeedLimit(speed);
            }

            var saved = SettingsActions.Save(new SettingsInput {
                GamesPathText = this.GamesPathBox.Text,
                DownloadThreads = this.ThreadsSlider.Value,
                SpeedLimitMbps = speed,
                AutoErrorReports = this.AutoErrorReportsCheck == null ? null : this.AutoErrorReportsCheck.IsChecked == true,
                SendUsageMetrics = this.UsageMetricsCheck == null ? null : this.UsageMetricsCheck.IsChecked == true,
                MinimizeToTray = this.MinimizeToTrayCheck == null ? null : this.MinimizeToTrayCheck.IsChecked == true,
            });
            if (!saved) {
                return;
            }

            // Поле пути могло быть нормализовано при записи — показываем то, что сохранилось
            if (!this.GamesPathBox.IsKeyboardFocused) {
                this.GamesPathBox.Text = SettingsView.Build(ConfigService.Current).GamesPath;
            }

            this.ShowSaved();
        }

        private void ShowSaved() {
            if (this.SaveStatusText == null) {
                return;
            }

            this.SaveStatusText.Text = "Сохранено";
            this.SaveStatusText.BeginAnimation(OpacityProperty, null);
            this.SaveStatusText.Opacity = 1;
            this.saveStatusTimer.Stop();
            this.saveStatusTimer.Start();
        }

        private async void CheckUpdateBtn_Click(object sender, RoutedEventArgs e) {
            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            if (win == null) {
                return;
            }

            this.CheckUpdateBtn.IsEnabled = false;
            this.CheckUpdateStatus.Text = "Проверяем…";
            try {
                var shown = await win.CheckForLauncherUpdateAsync();
                this.CheckUpdateStatus.Text = shown ? string.Empty : "Установлена последняя версия";
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"SettingsPage.CheckUpdate: {ex.Message}");
                this.CheckUpdateStatus.Text = "Не удалось проверить: нет связи с сервером";
            }
            finally {
                this.CheckUpdateBtn.IsEnabled = true;
            }
        }
    }
}
