// <copyright file="GamePage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
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
    using ChillHub.Core.Net;
    using ChillHub.Core.Sync;

    using static ChillHub.Core.Home.HomeFormat;

    /// <summary>
    /// Страница отдельной игры: сведения об установке, состояние, прогресс установки/обновления,
    /// выбор версии сборки (в том числе откат), changelog из новостей игры и раздел «Игра по сети».
    /// Кнопки «Играть» здесь нет намеренно — запуск остаётся на главной странице (см. Backlog.md).
    /// </summary>
    public partial class GamePage : Page {
        /// <summary>Чувствительность EMA для сглаживания скорости скачивания.</summary>
        private const double EmaAlpha = 0.2;

        /// <summary>
        /// Признак того, что со страницы игры менялось локальное состояние (установка/обновление/откат).
        /// Главная страница читает и сбрасывает флаг при возврате, чтобы освежить список без полной перезагрузки.
        /// </summary>
        private static bool localStateChanged;

        private readonly GameInfo game;
        private readonly HttpClient http = HttpClientProvider.Shared;
        private readonly ISyncService sync = new SimpleSyncService();

        private CancellationTokenSource? cts;
        private bool isBusy;
        private List<string> builds = new();
        private string localVersion = string.Empty;
        private double emaSpeedMBs;

        /// <summary>Initializes a new instance of the <see cref="GamePage"/> class — страницу для конкретной игры.</summary>
        /// <param name="game">Описание игры из списка главной страницы (объект переиспользуется, чтобы статусы совпадали).</param>
        public GamePage(GameInfo game) {
            this.InitializeComponent();
            this.game = game ?? new GameInfo();

            try {
                this.TitleText.Text = string.IsNullOrWhiteSpace(this.game.Title) ? this.game.GameId : this.game.Title;
                this.GameIcon.Tag = this.game.IconUrl;
                this.GameIcon.Source = null;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.ctor.Header");
            }

            this.Unloaded += this.GamePage_Unloaded;

            // Режим технических работ может включиться и выключиться, пока страница открыта (задача 25).
            // Подписка нужна на КАЖДЫЙ показ: после возврата из changelog страница снова Loaded,
            // а отписка уже произошла в Unloaded — иначе режим работ до неё больше не доходит.
            this.SubscribeMaintenance();
            this.Loaded += (s, e) => this.SubscribeMaintenance();

            _ = this.InitAsync();
        }

        /// <summary>Состояние игры на диске, определяющее подписи и доступность действий.</summary>
        private enum GameState {
            /// <summary>Локальных файлов нет.</summary>
            NotInstalled,

            /// <summary>Установлена и совпадает с последней версией.</summary>
            Installed,

            /// <summary>Установлена, но доступна более новая сборка.</summary>
            UpdateAvailable,

            /// <summary>Найден маркер `.updating`: обновление прервали посередине.</summary>
            Unfinished,
        }

        /// <summary>
        /// Забирает и сбрасывает признак изменения локального состояния.
        /// Возвращает true, если после последнего вызова игра ставилась, обновлялась или откатывалась.
        /// </summary>
        /// <returns>True, если главной странице нужно перечитать состояние игр.</returns>
        internal static bool ConsumeLocalStateChanged() {
            var value = localStateChanged;
            localStateChanged = false;
            return value;
        }

        private string BaseApi => ConfigService.Current.ApiBaseUrl;

        private string LocalRoot => GameLocalState.GameLocalRoot(this.game.GameId);

        // --- Инициализация и данные ---
        private async Task InitAsync() {
            try {
                await this.RefreshStateAsync().ConfigureAwait(true);
                await this.LoadBuildsAsync().ConfigureAwait(true);
                await this.LoadChangelogAsync().ConfigureAwait(true);
            }
            catch (Exception ex) {
                // Страница уже показана: пользователь увидит пустые поля, но не падение лаунчера
                Core.Logging.Logger.Error(ex, "GamePage.InitAsync");
            }
        }

        /// <summary>Перечитывает состояние игры с диска и обновляет всю сводку на странице.</summary>
        private async Task RefreshStateAsync() {
            var gid = this.game.GameId;
            var root = this.LocalRoot;

            // Диск читаем в фоне: на медленных HDD обход папки игры заметно подвешивает UI
            var localVer = await Task.Run(() => GameLocalState.ReadLocalVersion(gid)).ConfigureAwait(true);
            var sizeOnDisk = await Task.Run(() => GetDirectorySize(root)).ConfigureAwait(true);
            var freeSpace = await Task.Run(() => GameLocalState.GetAvailableFreeSpaceFor(gid)).ConfigureAwait(true);
            var unfinished = await Task.Run(() => GameLocalState.HasUnfinishedUpdate(gid)).ConfigureAwait(true);
            var hasFiles = await Task.Run(() => GameLocalState.HasAnyLocalGameFiles(root)).ConfigureAwait(true);

            this.localVersion = (localVer ?? string.Empty).Trim();

            try {
                var latest = (this.game.LatestVersion ?? string.Empty).Trim();
                this.InstalledVersionText.Text = string.IsNullOrWhiteSpace(this.localVersion) ? "не установлена" : this.localVersion;
                this.LatestVersionText.Text = string.IsNullOrWhiteSpace(latest) ? "неизвестна" : latest;
                this.SizeOnDiskText.Text = sizeOnDisk > 0 ? FormatSize(sizeOnDisk) : "—";
                this.FreeSpaceText.Text = freeSpace > 0 ? FormatSize(freeSpace) : "—";
                this.InstallPathText.Text = NormalizeDisplayPath(root);
                this.OpenFolderBtn.IsEnabled = Directory.Exists(root);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.RefreshStateAsync.Fields");
            }

            var state = this.ComputeState(unfinished, hasFiles);
            this.ApplyState(state);
            this.UpdateVersionSwitchAvailability();
        }

        private GameState ComputeState(bool unfinished, bool hasFiles) {
            if (unfinished) {
                return GameState.Unfinished;
            }

            var installed = hasFiles || !string.IsNullOrWhiteSpace(this.localVersion);
            if (!installed) {
                return GameState.NotInstalled;
            }

            var latest = (this.game.LatestVersion ?? string.Empty).Trim();

            // NeedsUpdate главная страница считает по полному сравнению с манифестом — доверяем ему,
            // а сравнение маркеров версий добавляем как второй, более дешёвый признак.
            var versionMismatch = !string.IsNullOrWhiteSpace(latest)
                && !string.Equals(this.localVersion, latest, StringComparison.OrdinalIgnoreCase);
            return (this.game.NeedsUpdate || versionMismatch) ? GameState.UpdateAvailable : GameState.Installed;
        }

        private void ApplyState(GameState state) {
            try {
                // Синхронизируем модель, чтобы главная страница показала тот же статус после возврата
                this.game.InstalledVersion = this.localVersion;
                this.game.IsInstalled = state != GameState.NotInstalled;
                this.game.NeedsUpdate = state is GameState.UpdateAvailable or GameState.Unfinished;

                if (this.isBusy) {
                    this.ActionBtn.Content = "Отмена";
                    this.ActionBtn.IsEnabled = true;
                    return;
                }

                switch (state) {
                    case GameState.NotInstalled:
                        this.StateText.Text = "Не установлена";
                        this.ActionBtn.Content = "Установить";
                        break;
                    case GameState.UpdateAvailable:
                        this.StateText.Text = "Доступно обновление";
                        this.ActionBtn.Content = "Обновить";
                        break;
                    case GameState.Unfinished:
                        this.StateText.Text = "Обновление не завершено";
                        this.ActionBtn.Content = "Завершить обновление";
                        break;
                    case GameState.Installed:
                    default:
                        this.StateText.Text = "Установлена";
                        this.ActionBtn.Content = "Проверить файлы";
                        break;
                }

                this.ActionBtn.IsEnabled = true;

                // Последним словом остаётся режим технических работ: он может запретить действие
                this.ApplyMaintenanceToButtons();
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, $"GamePage.ApplyState({state})");
            }
        }

        private async Task LoadBuildsAsync() {
            var gid = this.game.GameId;
            try {
                var url = $"{this.BaseApi}/api/games/{gid}/builds";
                var resp = await this.http.GetFromJsonAsync<BuildsResponse>(url).ConfigureAwait(true);
                this.builds = resp?.Items ?? new List<string>();
                this.BuildsCombo.ItemsSource = this.builds;

                // По умолчанию подставляем установленную версию, иначе последнюю
                var preselect = !string.IsNullOrWhiteSpace(this.localVersion) ? this.localVersion : this.game.LatestVersion;
                var idx = this.builds.FindIndex(b => string.Equals(b?.Trim(), (preselect ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
                this.BuildsCombo.SelectedIndex = idx >= 0 ? idx : (this.builds.Count > 0 ? 0 : -1);
                this.UpdateVersionSwitchAvailability();
            }
            catch (Exception ex) {
                // Без списка сборок страница остаётся рабочей, просто нельзя переключить версию
                Core.Logging.Logger.Error(ex, $"GamePage.LoadBuildsAsync(gid={gid})");
                this.builds = new List<string>();
                this.BuildsCombo.ItemsSource = this.builds;
                this.VersionHintText.Text = "Не удалось получить список версий. Проверьте подключение к интернету.";
            }
        }

        private async Task LoadChangelogAsync() {
            var gid = this.game.GameId;
            try {
                var url = $"{this.BaseApi}/news/games/{gid}/index.json";
                var index = await this.http.GetFromJsonAsync<NewsIndex>(url).ConfigureAwait(true);
                var items = index?.Items ?? new List<NewsItem>();
                foreach (var item in items) {
                    if (!string.IsNullOrWhiteSpace(item.CoverUrl) && item.CoverUrl.StartsWith("/", StringComparison.Ordinal)) {
                        item.CoverUrl = this.BaseApi + item.CoverUrl;
                    }
                }

                this.ChangelogList.ItemsSource = items;
                this.ChangelogEmptyText.Text = "Записей пока нет";
                this.ChangelogEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) {
                // Changelog второстепенен: установка и обновление работают без него
                Core.Logging.Logger.Error(ex, $"GamePage.LoadChangelogAsync(gid={gid})");
                this.ChangelogList.ItemsSource = Array.Empty<NewsItem>();
                this.ChangelogEmptyText.Text = "Не удалось загрузить changelog. Проверьте подключение к интернету.";
                this.ChangelogEmptyText.Visibility = Visibility.Visible;
            }
        }

        // --- Обработчики, на имена которых ссылается XAML ---
        private void BackBtn_Click(object sender, RoutedEventArgs e) {
            try {
                // Возврат по стеку сохраняет состояние главной страницы (выбранная игра, загруженные новости)
                if (this.NavigationService?.CanGoBack == true) {
                    this.NavigationService.GoBack();
                    return;
                }

                // Переиспользуем единственный HomePage, иначе получим вторую копию страницы
                var win = Window.GetWindow(this) as ChillHub.MainWindow;
                win?.NavigateToHome();
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.BackBtn_Click");
            }
        }

        private void CoverImg_Loaded(object sender, RoutedEventArgs e) {
            if (sender is System.Windows.Controls.Image img) {
                ImageLoader.AttachAndLoad(img, this.BaseApi);
            }
        }

        private void CoverImg_ImageFailed(object sender, System.Windows.ExceptionRoutedEventArgs e) {
            if (sender is System.Windows.Controls.Image img) {
                ImageLoader.HandleImageFailed(img, e.ErrorException);
            }
        }

        private void ActionBtn_Click(object sender, RoutedEventArgs e) {
            if (this.isBusy) {
                try {
                    this.cts?.Cancel();
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Warn($"GamePage: отмена операции не выполнилась: {ex.Message}");
                }

                return;
            }

            var version = (this.game.LatestVersion ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(version)) {
                version = this.builds.Count > 0 ? (this.builds[0] ?? string.Empty).Trim() : string.Empty;
            }

            if (string.IsNullOrWhiteSpace(version)) {
                this.StatusText.Text = "Нет доступных сборок для установки";
                return;
            }

            _ = this.StartSyncAsync(version, isVersionSwitch: false);
        }

        private void BuildsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            this.UpdateVersionSwitchAvailability();
        }

        private void SwitchVersionBtn_Click(object sender, RoutedEventArgs e) {
            if (this.isBusy) {
                return;
            }

            var selected = (this.BuildsCombo.SelectedItem as string)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(selected)) {
                return;
            }

            var latest = (this.game.LatestVersion ?? string.Empty).Trim();
            var isRollback = !string.IsNullOrWhiteSpace(latest)
                && !string.Equals(selected, latest, StringComparison.OrdinalIgnoreCase);

            var title = isRollback ? "Переключение версии (откат)" : "Переключение версии";
            var text = isRollback
                ? $"Сейчас будет установлена версия {selected}, а не последняя ({latest}).\n\n"
                  + "Это откат, а не обновление: файлы игры будут приведены к состоянию выбранной сборки, "
                  + "новый контент и исправления из более свежих версий пропадут. "
                  + "Сетевая игра с теми, у кого другая версия, работать не будет.\n\nПродолжить?"
                : $"Файлы игры будут приведены к состоянию версии {selected}.\n\nПродолжить?";

            var answer = MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) {
                return;
            }

            _ = this.StartSyncAsync(selected, isVersionSwitch: true);
        }

        private void OpenFolderBtn_Click(object sender, RoutedEventArgs e) {
            try {
                var root = this.LocalRoot;
                if (!Directory.Exists(root)) {
                    this.StatusText.Text = "Папка игры не найдена";
                    return;
                }

                Process.Start(new ProcessStartInfo { FileName = root, UseShellExecute = true });
            }
            catch (Exception ex) {
                this.StatusText.Text = "Не удалось открыть папку игры.";
                Core.Logging.Logger.Error(ex, "GamePage.OpenFolderBtn_Click");
            }
        }

        private void OpenLogsBtn_Click(object sender, RoutedEventArgs e) {
            try {
                var dir = Core.Logging.Logger.LogDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) {
                this.StatusText.Text = "Не удалось открыть папку с логами.";
                Core.Logging.Logger.Error(ex, "GamePage.OpenLogsBtn_Click");
            }
        }

        private async void RefreshChangelog_Click(object sender, RoutedEventArgs e) {
            await this.LoadChangelogAsync().ConfigureAwait(true);
        }

        private void ChangelogList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (this.ChangelogList.SelectedItem is not NewsItem item) {
                return;
            }

            try {
                var url = $"{this.BaseApi}/news/games/{this.game.GameId}/{item.Slug}.md";
                var win = Window.GetWindow(this) as ChillHub.MainWindow;
                win?.ContentFrame.Navigate(new NewsDetailPage(item.Title, url));
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.ChangelogList_SelectionChanged");
            }
            finally {
                this.ChangelogList.SelectedItem = null;
            }
        }

        private void GamePage_Unloaded(object sender, RoutedEventArgs e) {
            // Уходим со страницы во время закачки — операцию не бросаем «висеть» без владельца
            try {
                if (this.isBusy) {
                    this.cts?.Cancel();
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage.Unloaded: отмена не выполнилась: {ex.Message}");
            }

            // Статическое событие переживёт страницу — отписываемся, иначе утечёт ссылка
            this.UnsubscribeMaintenance();
        }

        // Подписка на статическое событие живёт ровно столько, сколько страница показана.
        private bool maintenanceSubscribed;

        private void SubscribeMaintenance() {
            if (this.maintenanceSubscribed) {
                return;
            }

            try {
                Core.Maintenance.MaintenanceService.Changed += this.OnMaintenanceChanged;
                this.maintenanceSubscribed = true;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage: подписка на режим работ не выполнилась: {ex.Message}");
            }
        }

        private void UnsubscribeMaintenance() {
            if (!this.maintenanceSubscribed) {
                return;
            }

            try {
                Core.Maintenance.MaintenanceService.Changed -= this.OnMaintenanceChanged;
                this.maintenanceSubscribed = false;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage.Unloaded: отписка от режима работ: {ex.Message}");
            }
        }

        /// <summary>
        /// Сервер сообщил, что работы начались или закончились. Перезапуск клиента не нужен:
        /// просто пересчитываем доступность кнопок.
        /// </summary>
        private void OnMaintenanceChanged(Core.Maintenance.MaintenanceState state) {
            try {
                this.ApplyMaintenanceToButtons();
                this.UpdateVersionSwitchAvailability();
                if (state.Enabled) {
                    this.StatusText.Text = state.BuildBannerText();
                }
                else if (!this.isBusy) {
                    this.StatusText.Text = string.Empty;
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.OnMaintenanceChanged");
            }
        }

        /// <summary>
        /// Блокирует установку/обновление, если сервер объявил технические работы.
        /// Подпись кнопки объясняет причину, чтобы неактивная кнопка не выглядела поломкой.
        /// </summary>
        private void ApplyMaintenanceToButtons() {
            try {
                if (this.isBusy) {
                    return; // идёт закачка: кнопка работает как «Отмена», её не трогаем
                }

                if (this.IsSyncBlockedByMaintenance()) {
                    this.ActionBtn.Content = "Технические работы";
                    this.ActionBtn.IsEnabled = false;
                    this.SwitchVersionBtn.IsEnabled = false;
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.ApplyMaintenanceToButtons");
            }
        }

        /// <summary>
        /// Запрещена ли сейчас любая работа с файлами игры. Установка и обновление ходят по
        /// одной и той же раздаче, поэтому блокируем, если запрещено хотя бы одно из них
        /// применительно к текущему состоянию игры.
        /// </summary>
        private bool IsSyncBlockedByMaintenance() {
            var state = Core.Maintenance.MaintenanceService.Current;
            return this.game.IsInstalled ? state.BlocksUpdate : state.BlocksInstall;
        }

        // --- Установка / обновление / переключение версии ---
        private void UpdateVersionSwitchAvailability() {
            try {
                var selected = (this.BuildsCombo.SelectedItem as string)?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(selected)) {
                    this.SwitchVersionBtn.IsEnabled = false;
                    return;
                }

                var sameAsInstalled = !string.IsNullOrWhiteSpace(this.localVersion)
                    && string.Equals(selected, this.localVersion, StringComparison.OrdinalIgnoreCase);

                // Маркер незавершённого обновления означает, что версия на диске «смешанная»:
                // повторная установка той же версии в этом случае осмысленна.
                var unfinished = GameLocalState.HasUnfinishedUpdate(this.game.GameId);

                var maintenanceBlocked = this.IsSyncBlockedByMaintenance();
                this.SwitchVersionBtn.IsEnabled = !this.isBusy && !maintenanceBlocked && (!sameAsInstalled || unfinished);

                var latest = (this.game.LatestVersion ?? string.Empty).Trim();
                if (maintenanceBlocked) {
                    this.VersionHintText.Text = "Переключение версии недоступно: на сервере идут технические работы.";
                }
                else if (sameAsInstalled && !unfinished) {
                    this.VersionHintText.Text = "Эта версия уже установлена.";
                }
                else if (!string.IsNullOrWhiteSpace(latest) && !string.Equals(selected, latest, StringComparison.OrdinalIgnoreCase)) {
                    this.VersionHintText.Text = $"Внимание: {selected} — не последняя версия. Установка будет откатом с {latest}.";
                }
                else {
                    this.VersionHintText.Text = "Выбрана последняя версия.";
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.UpdateVersionSwitchAvailability");
            }
        }

        private async Task StartSyncAsync(string version, bool isVersionSwitch) {
            var gid = this.game.GameId;
            if (string.IsNullOrWhiteSpace(gid)) {
                this.StatusText.Text = "Не удалось определить игру";
                return;
            }

            // Подстраховка: работы могли начаться уже после отрисовки кнопок
            if (this.IsSyncBlockedByMaintenance()) {
                this.StatusText.Text = Core.Maintenance.MaintenanceService.Current.BuildBannerText();
                this.ApplyMaintenanceToButtons();
                return;
            }

            var localRoot = this.LocalRoot;
            this.cts = new CancellationTokenSource();
            var token = this.cts.Token;
            this.SetBusy(true);
            this.emaSpeedMBs = 0.0;
            this.SyncProgressBar.Value = 0;
            this.SpeedEtaText.Text = string.Empty;
            this.FilesSizeText.Text = string.Empty;

            try {
                // Игра запущена — файлы менять нельзя
                if (this.IsGameRunning(out var exeName)) {
                    this.StatusText.Text = $"Игра запущена ({exeName}). Закройте игру и повторите.";
                    return;
                }

                Core.Logging.Logger.Info($"GamePage.StartSync gid={gid} version={version} switch={isVersionSwitch}");
                this.StatusText.Text = "Загрузка манифеста…";
                this.SyncProgressBar.IsIndeterminate = true;

                var manifestUrl = IntegrityChecker.ManifestUrl(this.BaseApi, gid, version);
                var contentBase = IntegrityChecker.ContentBaseUrl(this.BaseApi, gid, version);
                var manifest = await this.sync.GetManifestAsync(manifestUrl, token).ConfigureAwait(true);

                this.StatusText.Text = "Сравнение файлов…";

                // PlanAsync только выглядит асинхронным: внутри полный обход папки игры с пересчётом
                // хешей, а Task возвращается уже завершённым. С UI-потока это подвешивает окно
                // на всё время обхода — уводим в пул потоков (как в IntegrityChecker).
                var plan = await Task.Run(() => this.sync.PlanAsync(manifest, localRoot, contentBase, token), token).ConfigureAwait(true);
                Core.Logging.Logger.Info($"GamePage plan gid={gid} downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count}");

                // Свободного места может не хватить — предупреждаем до начала закачки
                var free = GameLocalState.GetAvailableFreeSpaceFor(gid);
                if (plan.TotalDownloadBytes > 0) {
                    this.FilesSizeText.Text = $"Нужно: {FormatSize(plan.TotalDownloadBytes)} ({FormatSize(free)} доступно)";
                    if (free > 0 && free < plan.TotalDownloadBytes) {
                        this.StatusText.Text = "Недостаточно свободного места.";
                        return;
                    }
                }

                var start = DateTime.UtcNow;
                var progress = new Progress<SyncProgress>(p => this.OnSyncProgress(p, start));
                await this.sync.ExecuteAsync(plan, progress, token).ConfigureAwait(true);

                // Маркер версии обязан соответствовать тому, что реально установлено (в т.ч. после отката)
                GameLocalState.WriteLocalVersion(gid, version);
                localStateChanged = true;

                this.StatusText.Text = isVersionSwitch ? $"Готово. Установлена версия {version}." : "Готово.";
                this.SpeedEtaText.Text = string.Empty;
                Core.Logging.Logger.Info($"GamePage.StartSync done gid={gid} version={version}");
            }
            catch (OperationCanceledException) {
                this.StatusText.Text = "Операция отменена.";
                this.SpeedEtaText.Text = string.Empty;
                Core.Logging.Logger.Info($"GamePage.StartSync cancelled gid={gid} version={version}");
            }
            catch (ManifestSignatureException ex) {
                // Манифест подписан неверно: раздачу могли подменить. Файлы игры не тронуты —
                // говорим об этом прямо, а не общей фразой «попробуйте ещё раз».
                this.StatusText.Text = ManifestSignature.UserMessage;
                this.StatusText.ToolTip = "Подробнее: " + ex.Message;
                Core.Logging.Logger.Error(ex, $"GamePage.StartSyncAsync.ManifestSignature(gid={gid}, version={version})");
            }
            catch (Exception ex) {
                var message = ex is IOException
                    ? "Не удалось записать файлы игры. Проверьте свободное место и права доступа."
                    : "Не удалось завершить операцию. Попробуйте ещё раз.";
                this.StatusText.Text = message;
                this.StatusText.ToolTip = "Подробнее: " + ex.Message;
                Core.Logging.Logger.Error(ex, $"GamePage.StartSyncAsync(gid={gid}, version={version})");
            }
            finally {
                this.SetBusy(false);
                this.SyncProgressBar.IsIndeterminate = false;
                try {
                    await this.RefreshStateAsync().ConfigureAwait(true);
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Error(ex, "GamePage.StartSyncAsync.RefreshState");
                }
            }
        }

        private void OnSyncProgress(SyncProgress p, DateTime start) {
            try {
                switch (p.Stage) {
                    case "Checking":
                        this.StatusText.Text = "Проверка файлов…";
                        this.SyncProgressBar.IsIndeterminate = true;
                        break;
                    case "Downloading":
                        this.StatusText.Text = "Скачивание…";
                        this.SyncProgressBar.IsIndeterminate = false;
                        if (p.TotalBytes > 0) {
                            this.SyncProgressBar.Value = Math.Min(100, Math.Max(0, p.BytesDownloaded * 100.0 / p.TotalBytes));
                            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                            var instant = elapsed > 0 ? (p.BytesDownloaded / 1024.0 / 1024.0) / elapsed : 0;
                            this.emaSpeedMBs = this.emaSpeedMBs <= 0 ? instant : ((EmaAlpha * instant) + ((1 - EmaAlpha) * this.emaSpeedMBs));
                            var remain = p.TotalBytes - p.BytesDownloaded;
                            var eta = this.emaSpeedMBs > 0 ? (remain / 1024.0 / 1024.0) / this.emaSpeedMBs : 0;
                            this.SpeedEtaText.Text = $"Скорость: {this.emaSpeedMBs:0.0} МБ/с • Осталось: {FormatEta(eta)}";
                            this.FilesSizeText.Text = $"{p.FilesDownloaded}/{p.TotalFiles} • {FormatSize(p.BytesDownloaded)}/{FormatSize(p.TotalBytes)}";
                        }

                        break;
                    case "Verifying":
                        this.StatusText.Text = "Проверка скачанного…";
                        this.SyncProgressBar.Value = 100;
                        this.SyncProgressBar.IsIndeterminate = true;
                        this.SpeedEtaText.Text = string.Empty;
                        break;
                    case "Activating":
                        this.StatusText.Text = "Применение…";
                        this.SyncProgressBar.Value = 100;
                        this.SyncProgressBar.IsIndeterminate = true;
                        this.SpeedEtaText.Text = string.Empty;
                        break;
                    case "Completed":
                        this.SyncProgressBar.IsIndeterminate = false;
                        this.SyncProgressBar.Value = 100;
                        this.StatusText.Text = "Готово";
                        this.SpeedEtaText.Text = string.Empty;
                        break;
                    default:
                        this.StatusText.Text = p.Stage;
                        break;
                }
            }
            catch (Exception ex) {
                // Отрисовка прогресса не должна прерывать саму закачку
                Core.Logging.Logger.Warn($"GamePage.OnSyncProgress: {ex.Message}");
            }
        }

        private void SetBusy(bool busy) {
            this.isBusy = busy;
            try {
                this.ActionBtn.Content = busy ? "Отмена" : this.ActionBtn.Content;
                this.ActionBtn.IsEnabled = true;
                this.SwitchVersionBtn.IsEnabled = !busy && this.SwitchVersionBtn.IsEnabled;
                this.BuildsCombo.IsEnabled = !busy;
                if (busy) {
                    this.StatusText.ToolTip = null;
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, $"GamePage.SetBusy({busy})");
            }
        }

        private bool IsGameRunning(out string exeName) {
            exeName = string.Empty;
            try {
                if (string.IsNullOrWhiteSpace(this.game.ExeRelativePath)) {
                    return false;
                }

                exeName = Path.GetFileNameWithoutExtension(this.game.ExeRelativePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(exeName)) {
                    return false;
                }

                return Process.GetProcessesByName(exeName).Length > 0;
            }
            catch (Exception ex) {
                // Опрос процессов может быть запрещён политиками — не мешаем операции
                Core.Logging.Logger.Warn($"GamePage.IsGameRunning: {ex.Message}");
                return false;
            }
        }

        /// <summary>Суммарный размер файлов в папке игры. 0, если папки нет или её не удалось обойти.</summary>
        private static long GetDirectorySize(string root) {
            try {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                    return 0;
                }

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                    try {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) {
                        // Файл могли удалить во время обхода — пропускаем и считаем дальше
                        Core.Logging.Logger.Warn($"GamePage.GetDirectorySize: '{file}': {ex.Message}");
                    }
                }

                return total;
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage.GetDirectorySize('{root}'): {ex.Message}");
                return 0;
            }
        }
    }
}
