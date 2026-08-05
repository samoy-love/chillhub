// <copyright file="UpdateWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.Net.Http;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Documents;
    using System.Windows.Media;

    using ChillHub.Core;
    using ChillHub.Core.Net;
    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Sync;

    /// <summary>
    /// Окно самообновления. Самой логики обновления здесь нет: она живёт в
    /// <see cref="ChillHub.Core.SelfUpdate"/> и проверяется тестами. Здесь остаются
    /// обработчики событий, отрисовка <see cref="SelfUpdateUiState"/> и три поля
    /// состояния диалога.
    /// </summary>
    public partial class UpdateWindow : Window {
        private readonly HttpClient http = HttpClientProvider.Shared;
        private readonly ISyncService sync = new SimpleSyncService();
        private readonly SelfUpdatePaths paths = SelfUpdatePaths.Default;
        private readonly UpdateAttemptsStore attempts = new UpdateAttemptsStore();

        private readonly SelfUpdateChecker checker;
        private readonly SelfUpdateDownloader downloader;
        private readonly SelfUpdateApplier applier;

        private bool updateRequired = false; // есть ли новая версия
        private bool loopBlocked = false;    // A4: автообновление остановлено защитой от петли
        private bool updaterStarted = false; // A14: апдейтер запущен, его временный каталог трогать нельзя
        private bool downloaded = false;     // скачан ли пакет
        private string? remoteVersion;
        private string stripPrefix = string.Empty; // корневая папка внутри пакета (обычно пусто)
        private string? pendingTempRoot;
        private string? pendingWorkDir;

        public bool Proceed { get; private set; } = false;

        private string BaseApi => ConfigService.Current.ApiBaseUrl;

        public UpdateWindow() {
            this.InitializeComponent();
            this.checker = new SelfUpdateChecker(this.http, this.sync, () => this.BaseApi, this.paths, this.attempts);
            this.downloader = new SelfUpdateDownloader(this.sync, () => this.BaseApi, this.paths, this.attempts, this.ApplyUiState);
            this.applier = new SelfUpdateApplier(this.paths, this.attempts, this.ApplyUiState);

            SelfUpdateCleanup.TryCleanupTempSelfUpdateDirs(this.paths.TempRoot);
            SelfUpdateCleanup.TryCleanupInstalledUpdaterArtifacts(this.paths.InstallDir);

            // A14. Второй заход при закрытии окна: к этому моменту каталоги, которые
            // в конструкторе были заняты (лог апдейтера, дочитывавшийся при старте),
            // обычно уже свободны. Пропускаем только случай «мы сами запустили
            // апдейтер» — там временный каталог нужен работающему процессу.
            this.Closed += (_, _) => {
                if (!this.updaterStarted) {
                    SelfUpdateCleanup.TryCleanupTempSelfUpdateDirs(this.paths.TempRoot);
                }
            };

            // In DEBUG builds, pre-check the DEV skip checkbox by default
            // so developers can easily bypass self-update if they choose.
#if DEBUG
            try {
                this.DevSkipCheck.IsChecked = true;
            }
            catch {
            }
#endif

            // In Release builds, hide the development-only controls to prevent skipping updates.
            // Window uses SizeToContent=Height so it will shrink automatically.
#if !DEBUG
            try
            {
                this.DevPanel.Visibility = Visibility.Collapsed;
            }
            catch
            {
            }
#endif
        }

        /// <summary>
        /// Единственное место, где состояние процесса превращается в контролы.
        /// null-поля означают «не трогать»: часть веток исходного кода намеренно
        /// оставляла, например, полосу прогресса как есть.
        /// </summary>
        private void ApplyUiState(SelfUpdateUiState state) {
            if (state.StatusText != null) {
                this.StatusText.Text = state.StatusText;
            }

            if (state.Indeterminate.HasValue) {
                this.Progress.IsIndeterminate = state.Indeterminate.Value;
            }

            if (state.ProgressValue.HasValue) {
                this.Progress.Value = state.ProgressValue.Value;
            }

            if (state.ButtonContent != null) {
                this.PrimaryBtn.Content = state.ButtonContent;
            }

            if (state.ButtonEnabled.HasValue) {
                this.PrimaryBtn.IsEnabled = state.ButtonEnabled.Value;
            }
        }

        private void SetUpdateAvailableStatus(string local, string remote) {
            // Resolve theme brushes
            var danger = (Brush)(this.TryFindResource("Brush.Danger") ?? new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));
            var success = (Brush)(this.TryFindResource("Brush.Success") ?? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)));
            var normal = (Brush)(this.TryFindResource("Brush.Text") ?? SystemColors.ControlTextBrush);

            this.StatusText.Inlines.Clear();
            this.StatusText.Inlines.Add(new Run("Доступно обновление лаунчера: ") { Foreground = normal });
            this.StatusText.Inlines.Add(new Run(local) { Foreground = danger, FontWeight = FontWeights.SemiBold });
            this.StatusText.Inlines.Add(new Run(" → ") { Foreground = normal });
            var boldNew = new Bold(new Run(remote) { Foreground = success });
            this.StatusText.Inlines.Add(boldNew);
            this.StatusText.Inlines.Add(new Run(".") { Foreground = normal });
        }

        /// <summary>Показывает решение проверки: состояние окна плюс DEV-скип для «доступно обновление».</summary>
        private void ShowDecision(SelfUpdateDecision decision) {
            this.updateRequired = decision.UpdateRequired;
            this.loopBlocked = decision.LoopBlocked;
            this.stripPrefix = decision.StripPrefix;

            if (decision.State == SelfUpdateState.UpdateAvailable || decision.State == SelfUpdateState.LoopBlocked) {
                this.remoteVersion = decision.RemoteVersion;
            }

            if (decision.State == SelfUpdateState.UpdateAvailable) {
                this.SetUpdateAvailableStatus(decision.LocalVersion, decision.RemoteVersion);
            }

            this.ApplyUiState(decision.Ui);

#if DEBUG
            if (decision.State == SelfUpdateState.UpdateAvailable) {
                try {
                    if (this.DevPanel.Visibility == Visibility.Visible) {
                        this.DevSkipCheck.Checked += (s, _) => { this.PrimaryBtn.Content = "Продолжить без обновления (DEV)"; };
                        this.DevSkipCheck.Unchecked += (s, _) => { this.PrimaryBtn.Content = "Обновить и перезапустить"; };
                        if (this.DevSkipCheck.IsChecked == true) {
                            this.PrimaryBtn.Content = "Продолжить без обновления (DEV)";
                        }
                    }
                }
                catch {
                }
            }
#endif
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) {
            try {
                this.StatusText.Text = "Проверка обновлений лаунчера...";
                this.Progress.IsIndeterminate = true;
                this.PrimaryBtn.IsEnabled = false;
                this.ShowDecision(await this.checker.CheckAsync());
            }
            catch (Exception ex) {
                // Сама проверка свои отказы уже разбирает и возвращает решением.
                // Сюда доходит только сбой отрисовки — но окно всё равно обязано
                // остаться работоспособным, иначе пользователь заперт в диалоге.
                this.StatusText.Text = $"Не удалось проверить обновление (GET {this.BaseApi}/manifests/launcher/latest.json): {ex.Message}";
                this.Progress.IsIndeterminate = false;
                this.Progress.Value = 0;
                this.PrimaryBtn.Content = "Продолжить";
                this.updateRequired = false;
                this.PrimaryBtn.IsEnabled = true;
                try {
                    Core.Logging.Logger.Error(ex, "UpdateWindow.Window_Loaded");
                }
                catch {
                }
            }
            finally {
                this.ShowPreviousUpdateOutcome();
            }
        }

        /// <summary>A12. Дописывает к статусу исход прошлого запуска апдейтера, если тот провалился.</summary>
        private void ShowPreviousUpdateOutcome() {
            try {
                var text = PreviousUpdateOutcome.Describe(this.paths.InstallDir);
                if (text == null) {
                    return;
                }

                var danger = (Brush)(this.TryFindResource("Brush.Danger") ?? new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));
                this.StatusText.Inlines.Add(new LineBreak());
                this.StatusText.Inlines.Add(new Run(text) { Foreground = danger });
            }
            catch {
                // Диагностика не должна мешать запуску.
            }
        }

        private void ExitBtn_Click(object sender, RoutedEventArgs e) {
            this.DialogResult = false;
        }

        private async void PrimaryBtn_Click(object sender, RoutedEventArgs e) {
            // A4. В состоянии «остановлено защитой от петли» кнопка означает
            // «проверить целостность», а не «обновить»: это единственный выход,
            // не требующий переустановки вслепую.
            if (this.loopBlocked) {
                var integrity = await this.checker.VerifyIntegrityAsync(this.remoteVersion, this.ApplyUiState);
                this.loopBlocked = false;
                this.updateRequired = false;
                if (integrity.StripPrefix != null) {
                    this.stripPrefix = integrity.StripPrefix;
                }

                this.ApplyUiState(integrity.Ui);
                return;
            }

            // DEV-скип: только в Debug и только если панель видима; в Release невозможно
#if DEBUG
            var devSkip = this.DevPanel.Visibility == Visibility.Visible && this.DevSkipCheck.IsChecked == true;
#endif

#if DEBUG
            if (!this.updateRequired || devSkip)
#else
            if (!this.updateRequired)
#endif
            {
                this.Proceed = true;
                try {
                    this.DialogResult = true;
                }
                catch {
                    this.Close();
                }
                return;
            }

            // Если пакет не скачан — качаем
            if (!this.downloaded) {
                var download = await this.downloader.DownloadAsync(this.remoteVersion);
                if (download.StripPrefix != null) {
                    this.stripPrefix = download.StripPrefix;
                }

                if (download.Result == SelfUpdateDownloadResult.AlreadyUpToDate) {
                    this.updateRequired = false;
                    return;
                }

                if (!download.Downloaded) {
                    // Ни один отказ загрузки не ведёт к применению: пакета нет.
                    return;
                }

                this.pendingTempRoot = download.TempRoot;
                this.pendingWorkDir = download.WorkDir;
                this.downloaded = true;
            }

            // Применение (создание скрипта, копирование и перезапуск)
            var result = this.applier.Apply(this.pendingTempRoot, this.pendingWorkDir, this.remoteVersion, this.stripPrefix);
            if (result == SelfUpdateApplyResult.Started) {
                // A14: временный каталог нужен работающему апдейтеру — при закрытии окна его не трогаем.
                this.updaterStarted = true;

                // Завершаем приложение: освобождаем файлы и даём апдейтеру применить обновление.
                try {
                    Application.Current.Shutdown();
                }
                catch (Exception ex) {
                    this.StatusText.Text = $"Ошибка применения обновления: {ex.Message}";
                    this.PrimaryBtn.IsEnabled = true;
                    try {
                        Core.Logging.Logger.Error(ex, "UpdateWindow.ApplyUpdate");
                    }
                    catch {
                    }
                }
            }
        }
    }
}
