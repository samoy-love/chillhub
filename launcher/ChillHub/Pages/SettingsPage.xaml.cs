// <copyright file="SettingsPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    public partial class SettingsPage : Page {
        private readonly ISyncService sync = new SimpleSyncService();
        private readonly HttpClient http = ChillHub.Core.Net.HttpClientProvider.Shared;

        private CancellationTokenSource? integrityCts;
        private IntegrityReport? lastReport;
        private bool integrityBusy;
        private bool integrityRepairing;

        public SettingsPage() {
            this.InitializeComponent();

            // Defer UI population until the page is fully loaded to avoid template/resource init races (seen in dark theme)
            this.Loaded += this.SettingsPage_Loaded;
            this.Unloaded += this.SettingsPage_Unloaded;
        }

        private string BaseApi => ConfigService.Current.ApiBaseUrl;

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e) {
            // Ensure templates/resources are fully applied (especially in dark theme)
            this.Dispatcher.BeginInvoke(
                new Action(() => {
                    try {
                        this.LoadConfigToUi();
                    }
                    catch { /* prevent crash; user can reopen */
                    }

                    _ = this.LoadGamesForIntegrityAsync();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e) {
            // Уходим со страницы — отменяем незавершённую проверку, чтобы она не читала диск впустую.
            // Восстановление НЕ трогаем: обрыв на фазе активации оставит маркер .updating
            // и наполовину обновлённую игру, поэтому доводим его до конца в фоне.
            try {
                if (!this.integrityRepairing) {
                    this.integrityCts?.Cancel();
                }
            }
            catch {
            }
        }

        private void LoadConfigToUi() {
            var cfg = ConfigService.Current ?? new AppConfig();
            if (this.GamesPathBox != null) {
                // Отображаем путь с одинарными обратными слешами для читаемости.
                // Ведущий \\ сетевого пути при этом обязан уцелеть — см. NormalizeWindowsPath.
                var p = cfg.GamesPath ?? string.Empty;
                this.GamesPathBox.Text = HomeFormat.NormalizeWindowsPath(p);
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

            if (this.DiscordRpcCheck != null) {
                this.DiscordRpcCheck.IsChecked = cfg.DiscordRichPresence;
            }

            // Честно предупреждаем: без Application ID переключатель ничего не включает.
            // Иначе он создаёт иллюзию работающей интеграции, а статуса в Discord нет.
            if (this.DiscordRpcHintText != null) {
                var configured = ChillHub.Core.DiscordRichPresence.IsConfigured;
                this.DiscordRpcHintText.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
                this.DiscordRpcHintText.Text = configured
                    ? string.Empty
                    : "Интеграция пока не настроена владельцем лаунчера (не указан Application ID приложения Discord), "
                      + "поэтому статус не появится даже при включённом переключателе. Настройка сохранится и заработает после обновления лаунчера.";
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

        // ---- Проверка целостности игры (задача 18) ----

        /// <summary>
        /// Заполняет список игр для проверки. Сервер может быть недоступен —
        /// тогда просто говорим об этом, страница настроек остаётся рабочей.
        /// </summary>
        /// <returns>Задача заполнения списка.</returns>
        private async Task LoadGamesForIntegrityAsync() {
            try {
                var resp = await this.http.GetFromJsonAsync<GamesResponse>($"{this.BaseApi.TrimEnd('/')}/api/games").ConfigureAwait(true);
                var games = resp?.Items ?? new List<GameInfo>();
                if (this.IntegrityGameBox == null) {
                    return;
                }

                this.IntegrityGameBox.ItemsSource = games;
                if (games.Count == 0) {
                    this.SetIntegrityStatus("Список игр пуст.");
                    return;
                }

                // Подставим последнюю запускавшуюся игру, иначе первую установленную, иначе первую в списке
                var gamesPath = ConfigService.Current.GamesPath;
                var lastId = ConfigService.Current.LastGameId;
                var preselect = games.FirstOrDefault(g => string.Equals(g.GameId, lastId, StringComparison.OrdinalIgnoreCase))
                                ?? games.FirstOrDefault(g => IntegrityChecker.HasAnyLocalGameFiles(IntegrityChecker.GameLocalRoot(gamesPath, g.GameId)))
                                ?? games[0];
                this.IntegrityGameBox.SelectedItem = preselect;
            }
            catch (Exception ex) {
                try {
                    ChillHub.Core.Logging.Logger.Error(ex, "SettingsPage.LoadGamesForIntegrityAsync");
                }
                catch {
                }

                this.SetIntegrityStatus("Не удалось получить список игр — проверьте подключение к серверу.");
            }
        }

        private async void IntegrityCheckBtn_Click(object sender, RoutedEventArgs e) {
            if (this.integrityBusy) {
                return;
            }

            var game = this.IntegrityGameBox?.SelectedItem as GameInfo;
            if (game == null || string.IsNullOrWhiteSpace(game.GameId)) {
                this.SetIntegrityStatus("Выберите игру для проверки.");
                return;
            }

            this.lastReport = null;
            this.SetIntegrityBusy(true, repairing: false);
            this.IntegrityRepairBtn.Visibility = Visibility.Collapsed;
            this.SetIntegrityStatus("Проверка файлов…");

            var cts = new CancellationTokenSource();
            this.integrityCts = cts;
            var progress = new Progress<SyncProgress>(p => this.ReportIntegrityProgress(p, "Проверено"));

            try {
                var report = await IntegrityChecker.CheckAsync(
                    this.sync,
                    this.BaseApi,
                    game.GameId,
                    game.LatestVersion,
                    ConfigService.Current.GamesPath,
                    progress,
                    cts.Token);

                this.lastReport = report;
                this.SetIntegrityStatus(IntegrityChecker.Describe(report));
                this.IntegrityRepairBtn.Visibility = report.NeedsRepair ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) {
                this.SetIntegrityStatus("Проверка отменена.");
            }
            catch (IntegrityCheckException ex) {
                this.SetIntegrityStatus(ex.Message);
            }
            catch (Exception ex) {
                try {
                    ChillHub.Core.Logging.Logger.Error(ex, "SettingsPage.IntegrityCheck");
                }
                catch {
                }

                this.SetIntegrityStatus($"Не удалось проверить целостность: {ex.Message}");
            }
            finally {
                this.SetIntegrityBusy(false, repairing: false);
                this.integrityCts = null;
                cts.Dispose();
            }
        }

        private async void IntegrityRepairBtn_Click(object sender, RoutedEventArgs e) {
            if (this.integrityBusy) {
                return;
            }

            var report = this.lastReport;
            if (report == null || !report.NeedsRepair) {
                this.SetIntegrityStatus("Восстанавливать нечего — сначала выполните проверку.");
                return;
            }

            var confirm = MessageBox.Show(
                $"Будет перекачано файлов: {report.Plan.Downloads.Count}, удалено лишних: {report.Plan.ToDelete.Count}.\n\nПродолжить восстановление?",
                "Восстановление файлов игры",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) {
                return;
            }

            this.SetIntegrityBusy(true, repairing: true);
            this.IntegrityRepairBtn.Visibility = Visibility.Collapsed;
            this.SetIntegrityStatus("Восстановление…");

            var cts = new CancellationTokenSource();
            this.integrityCts = cts;
            var progress = new Progress<SyncProgress>(p => this.ReportIntegrityProgress(p, StageToRu(p.Stage)));

            try {
                // Маркер .updating ставится и снимается внутри ExecuteAsync
                await this.sync.ExecuteAsync(report.Plan, progress, cts.Token);
                this.lastReport = null;
                this.SetIntegrityStatus("Восстановление завершено. Рекомендуем проверить целостность ещё раз.");
            }
            catch (OperationCanceledException) {
                this.SetIntegrityStatus("Восстановление отменено. Игра может остаться в незавершённом состоянии — повторите восстановление.");
            }
            catch (Exception ex) {
                try {
                    ChillHub.Core.Logging.Logger.Error(ex, "SettingsPage.IntegrityRepair");
                }
                catch {
                }

                this.SetIntegrityStatus($"Не удалось восстановить файлы: {ex.Message}");
            }
            finally {
                this.SetIntegrityBusy(false, repairing: false);
                this.integrityCts = null;
                cts.Dispose();
            }
        }

        private void IntegrityCancelBtn_Click(object sender, RoutedEventArgs e) {
            try {
                this.integrityCts?.Cancel();
                this.SetIntegrityStatus("Отмена…");
            }
            catch {
            }
        }

        private void ReportIntegrityProgress(SyncProgress p, string label) {
            if (p == null || this.IntegrityProgress == null) {
                return;
            }

            var percent = p.TotalFiles > 0 ? p.FilesDownloaded * 100.0 / p.TotalFiles : 0;
            this.IntegrityProgress.Value = Math.Clamp(percent, 0, 100);
            this.SetIntegrityStatus($"{label}: {p.FilesDownloaded} из {p.TotalFiles}…");
        }

        private void SetIntegrityBusy(bool busy, bool repairing) {
            this.integrityBusy = busy;
            this.integrityRepairing = busy && repairing;
            if (this.IntegrityProgressPanel != null) {
                this.IntegrityProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            }

            if (this.IntegrityProgress != null && !busy) {
                this.IntegrityProgress.Value = 0;
            }

            if (this.IntegrityCheckBtn != null) {
                this.IntegrityCheckBtn.IsEnabled = !busy;
            }

            if (this.IntegrityGameBox != null) {
                this.IntegrityGameBox.IsEnabled = !busy;
            }
        }

        private void SetIntegrityStatus(string text) {
            if (this.IntegrityStatusText != null) {
                this.IntegrityStatusText.Text = text;
            }
        }

        private static string StageToRu(string stage) => stage switch {
            "Checking" => "Подготовка",
            "Downloading" => "Скачано",
            "Verifying" => "Проверка",
            "Activating" => "Установка",
            "Completed" => "Готово",
            _ => "Обработано",
        };

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
                        // Нормализуем отображение: одинарные обратные слеши (кроме префикса UNC)
                        var sp = dlg.SelectedPath ?? string.Empty;
                        this.GamesPathBox.Text = HomeFormat.NormalizeWindowsPath(sp);
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

                // Для файловой системы и конфигурации используем нормальную форму с одинарными
                // слешами. Сетевой путь вида \\nas\games при этом не превращаем в \nas\games.
                newPath = HomeFormat.NormalizeWindowsPath(newPath);
                try {
                    Directory.CreateDirectory(newPath);
                }
                catch (Exception ex) {
                    // Каталог мог быть недоступен (сетевая шара оффлайн, нет прав) — настройку
                    // всё равно сохраняем: путь может стать доступным позже.
                    ChillHub.Core.Logging.Logger.Warn($"SettingsPage.SaveBtn: не удалось создать папку игр '{newPath}': {ex.Message}");
                }

                cfg.GamesPath = newPath;
                cfg.DownloadThreads = (int)this.ThreadsSlider.Value;
                if (this.AutoErrorReportsCheck != null) {
                    cfg.AutoErrorReports = this.AutoErrorReportsCheck.IsChecked == true;
                }

                if (this.DiscordRpcCheck != null) {
                    var wasEnabled = cfg.DiscordRichPresence;
                    cfg.DiscordRichPresence = this.DiscordRpcCheck.IsChecked == true;

                    // Выключили при запущенной игре — статус надо снять сразу, а не при выходе из лаунчера
                    if (wasEnabled && !cfg.DiscordRichPresence) {
                        try {
                            ChillHub.Core.DiscordRichPresence.Shutdown();
                        }
                        catch (Exception ex) {
                            ChillHub.Core.Logging.Logger.Warn($"SettingsPage: снять статус Discord не удалось: {ex.Message}");
                        }
                    }
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
