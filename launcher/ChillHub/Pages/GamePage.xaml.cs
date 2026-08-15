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
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Home;
    using ChillHub.Core.Net;
    using ChillHub.Core.Sync;

    using static ChillHub.Core.Home.HomeFormat;

    /// <summary>
    /// Страница отдельной игры: сведения об установке, состояние, прогресс установки или
    /// обновления, наигранное время и changelog из новостей игры.
    /// Кнопки «Играть» здесь нет намеренно — запуск остаётся на главной странице.
    /// </summary>
    public partial class GamePage : Page {
        private readonly GameInfo game;
        private readonly HttpClient http = HttpClientProvider.Shared;
        private readonly ISyncService sync = new SimpleSyncService();
        private readonly GameBuildsLoader buildsLoader;
        private readonly GameChangelogLoader changelogLoader;
        private readonly GameSyncRunner syncRunner;
        private readonly SyncProgressView progressView = new();

        // Подтверждение копирования пути установки — тот же неинвазивный тост, что и на
        // главной странице (см. Core/Home/ToastHost.cs), но со своим экземпляром элементов.
        private ToastHost? toastHost;

        /// <summary>
        /// Очередь загрузок главной страницы (см. Core/Game/DownloadQueue.cs) — null у страницы,
        /// поднятой без неё (тесты). Установка/обновление идёт через неё, а не через локальный
        /// <see cref="syncRunner"/>: страница может закрыться, а закачка — нет.
        /// </summary>
        private readonly Core.Game.IDownloadQueue? downloadQueue;

        /// <summary>Текущая операция идёт через <see cref="downloadQueue"/>, а не через локальный <see cref="cts"/>.</summary>
        private bool viaQueue;

        private CancellationTokenSource? cts;
        private bool isBusy;
        private List<string> builds = new();
        private GameState currentState = GameState.NotInstalled;
        private string localVersion = string.Empty;

        /// <summary>Initializes a new instance of the <see cref="GamePage"/> class — страницу для конкретной игры.</summary>
        /// <param name="game">Описание игры из списка главной страницы (объект переиспользуется, чтобы статусы совпадали).</param>
        /// <param name="downloadQueue">
        /// Очередь загрузок главной страницы. Опциональна: тесты поднимают страницу без неё,
        /// и тогда установка/обновление идёт старым локальным путём.
        /// </param>
        internal GamePage(GameInfo game, Core.Game.IDownloadQueue? downloadQueue = null) {
            this.InitializeComponent();
            this.game = game ?? new GameInfo();
            this.downloadQueue = downloadQueue;
            this.buildsLoader = new GameBuildsLoader(this.http);
            this.changelogLoader = new GameChangelogLoader(this.http);
            this.syncRunner = new GameSyncRunner(this.sync, this.BuildSyncUi());

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

            if (this.downloadQueue != null) {
                this.downloadQueue.ItemAdded += this.OnQueueItemChanged;
                this.downloadQueue.ItemProgress += this.OnQueueItemChanged;
                this.downloadQueue.ItemCompleted += this.OnQueueItemFinished;
                this.downloadQueue.ItemRemoved += this.OnQueueItemFinished;

                // Страницу могли открыть, пока эта игра уже качается (поставлена в очередь с
                // главной), — подхватываем её состояние сразу, а не ждём следующего события.
                this.SyncFromQueueSnapshot();
            }

            _ = this.InitAsync();
        }

        /// <summary>
        /// Забирает и сбрасывает признак изменения локального состояния.
        /// Возвращает true, если после последнего вызова игра ставилась, обновлялась или откатывалась.
        /// </summary>
        /// <returns>True, если главной странице нужно перечитать состояние игр.</returns>
        internal static bool ConsumeLocalStateChanged() => GameLocalStateChanges.Consume();

        private string BaseApi => ConfigService.Current.ApiBaseUrl;

        private string LocalRoot => GameLocalState.GameLocalRoot(this.game.GameId);

        // --- Инициализация и данные ---
        private async Task InitAsync() {
            try {
                await this.RefreshStateAsync().ConfigureAwait(true);
                await this.LoadBuildsAsync().ConfigureAwait(true);
                await this.LoadChangelogAsync().ConfigureAwait(true);
                this.LoadPlaytime();
            }
            catch (Exception ex) {
                // Страница уже показана: пользователь увидит пустые поля, но не падение лаунчера
                Core.Logging.Logger.Error(ex, "GamePage.InitAsync");
            }
        }

        /// <summary>Наигранное время: читает playtime.json, реконсилируя незакрытые сессии.</summary>
        private void LoadPlaytime() {
            try {
                var entry = Core.Game.PlaytimeStore.Get(this.game.GameId);
                this.TotalPlaytimeText.Text = Core.Game.PlaytimeStore.FormatTotal(entry.TotalSeconds);
                this.LastSessionText.Text = Core.Game.PlaytimeStore.FormatLastSession(entry.LastSessionAt, entry.LastSessionSeconds);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage.LoadPlaytime: {ex.Message}");
            }
        }

        /// <summary>Перечитывает состояние игры с диска и обновляет всю сводку на странице.</summary>
        private async Task RefreshStateAsync() {
            var gid = this.game.GameId;
            var root = this.LocalRoot;

            // Диск читаем в фоне: на медленных HDD обход папки игры заметно подвешивает UI
            var localVer = await Task.Run(() => GameLocalState.ReadLocalVersion(gid)).ConfigureAwait(true);
            var sizeOnDisk = await Task.Run(() => GameDiskInfo.GetDirectorySize(root)).ConfigureAwait(true);
            var freeSpace = await Task.Run(() => GameLocalState.GetAvailableFreeSpaceFor(gid)).ConfigureAwait(true);
            var unfinished = await Task.Run(() => GameLocalState.HasUnfinishedUpdate(gid)).ConfigureAwait(true);
            var hasFiles = await Task.Run(() => GameLocalState.HasAnyLocalGameFiles(root)).ConfigureAwait(true);

            this.localVersion = (localVer ?? string.Empty).Trim();
            var state = GameStateResolver.Compute(unfinished, hasFiles, this.localVersion, this.game.LatestVersion, this.game.NeedsUpdate);

            try {
                var latest = (this.game.LatestVersion ?? string.Empty).Trim();
                // Без версии, но с файлами на диске игра не «не установлена»: рядом стоят
                // размер на диске и чип «Обновление не завершено», и такое соседство путало.
                this.InstalledVersionText.Text = !string.IsNullOrWhiteSpace(this.localVersion)
                    ? this.localVersion
                    : state == GameState.Unfinished ? "установка не завершена" : "не установлена";
                this.LatestVersionText.Text = string.IsNullOrWhiteSpace(latest) ? "неизвестна" : latest;
                this.SizeOnDiskText.Text = sizeOnDisk > 0 ? FormatSize(sizeOnDisk) : "—";
                this.FreeSpaceText.Text = freeSpace > 0 ? FormatSize(freeSpace) : "—";
                this.InstallPathText.Text = NormalizeDisplayPath(root);
                this.OpenFolderBtn.IsEnabled = Directory.Exists(root);
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, "GamePage.RefreshStateAsync.Fields");
            }

            this.ApplyState(state);
        }

        /// <summary>Клик по пути установки — копирует его в буфер обмена. Пустого/дефолтного «—» не копируем.</summary>
        private void InstallPathText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            try {
                var path = this.InstallPathText.Text;
                if (string.IsNullOrWhiteSpace(path) || path == "—") {
                    return;
                }

                Clipboard.SetText(path);
                (this.toastHost ??= new ToastHost(this.Toast, this.ToastText)).Show("Путь скопирован в буфер обмена");
            }
            catch (Exception ex) {
                // Буфер обмена может быть занят другим процессом — не критично, просто не скопировалось
                Core.Logging.Logger.Warn($"GamePage.InstallPathText_MouseLeftButtonUp: {ex.Message}");
            }
        }

        private void ApplyState(GameState state) {
            try {
                // Запоминаем состояние: от него зависит, что означает нажатие кнопки действия
                this.currentState = state;

                // Синхронизируем модель, чтобы главная страница показала тот же статус после возврата
                this.game.InstalledVersion = this.localVersion;
                this.game.IsInstalled = state != GameState.NotInstalled;
                this.game.NeedsUpdate = state is GameState.UpdateAvailable or GameState.Unfinished;

                if (this.isBusy) {
                    this.ActionBtn.Content = "Отмена";
                    this.ActionBtn.IsEnabled = true;
                    return;
                }

                var labels = GameStateResolver.Labels(state);
                this.StateText.Text = labels.StateText;
                this.ActionBtn.Content = labels.ActionText;
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
                this.builds = await this.buildsLoader.LoadAsync(this.BaseApi, gid).ConfigureAwait(true);
            }
            catch (Exception ex) {
                // Без списка сборок страница остаётся рабочей, просто нельзя переключить версию
                Core.Logging.Logger.ErrorNoReport(ex, $"GamePage.LoadBuildsAsync(gid={gid})");
                this.builds = new List<string>();
            }
        }

        private async Task LoadChangelogAsync() {
            var gid = this.game.GameId;
            try {
                var items = await this.changelogLoader.LoadAsync(this.BaseApi, gid).ConfigureAwait(true);

                this.ChangelogList.ItemsSource = items;
                this.ChangelogEmptyText.Text = "Записей пока нет";
                this.ChangelogEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) {
                // Changelog второстепенен: установка и обновление работают без него
                Core.Logging.Logger.ErrorNoReport(ex, $"GamePage.LoadChangelogAsync(gid={gid})");
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
                    if (this.viaQueue) {
                        this.downloadQueue?.Remove(this.game.GameId);
                    }
                    else {
                        this.cts?.Cancel();
                    }
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Warn($"GamePage: отмена операции не выполнилась: {ex.Message}");
                }

                return;
            }

            var version = (this.game.LatestVersion ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(version)) {
                // Список сборок приходит с сервера неотсортированным: берём максимальную по смыслу,
                // а не первую попавшуюся (иначе «установить последнюю» ставит самую старую).
                version = VersionOrder.SelectLatest(this.builds) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(version)) {
                this.StatusText.Text = "Нет доступных сборок для установки";
                return;
            }

            // Установка/обновление/докачка незавершённого — через общую очередь загрузок: страницу
            // можно закрыть или уйти на главную, не обрывая закачку (см. StartQueuedSync). Только
            // «Проверить файлы» (уже установлена) остаётся локальным — это быстрая сверка с
            // манифестом с подтверждением удаления лишнего, ставить её в очередь незачем.
            if (this.currentState != GameState.Installed && this.downloadQueue != null) {
                this.StartQueuedSync();
                return;
            }

            _ = this.StartSyncAsync(version, confirmDeletions: this.currentState == GameState.Installed);
        }

        /// <summary>
        /// Ставит установку/обновление в общую очередь загрузок вместо отдельного локального
        /// запуска. Раньше закачка жила только в <see cref="cts"/> этой страницы и обрывалась в
        /// <see cref="GamePage_Unloaded"/>, стоило уйти на главную — а в самой очереди её было не
        /// видно вовсе, потому что страница никогда её не пополняла.
        /// </summary>
        private void StartQueuedSync() {
            var gid = this.game.GameId;
            if (!this.downloadQueue!.Enqueue(gid)) {
                // Уже стоит в очереди (например, добавили с главной страницы) — просто подхватываем её.
                this.SyncFromQueueSnapshot();
                return;
            }

            this.viaQueue = true;
            this.SetBusy(true);
            this.progressView.Reset();
            this.SyncProgressBar.Value = 0;
            this.SpeedEtaText.Text = string.Empty;
            this.FilesSizeText.Text = string.Empty;
            this.StatusText.Text = "Ждёт очереди…";
        }

        /// <summary>Подхватывает текущее состояние этой игры из очереди, если она там уже есть.</summary>
        private void SyncFromQueueSnapshot() {
            if (this.downloadQueue == null) {
                return;
            }

            var item = this.downloadQueue.Snapshot()
                .FirstOrDefault(i => string.Equals(i.GameId, this.game.GameId, StringComparison.OrdinalIgnoreCase));
            if (item != null) {
                this.ApplyQueueItem(item);
            }
        }

        /// <summary>Событие очереди про эту игру: обновляем прогресс, если страница ещё показана.</summary>
        private void OnQueueItemChanged(Core.Game.QueueItem item) {
            if (!string.Equals(item.GameId, this.game.GameId, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            this.Dispatcher.BeginInvoke(() => this.ApplyQueueItem(item));
        }

        /// <summary>Позиция этой игры ушла из очереди — успехом, ошибкой или снятием вручную.</summary>
        private void OnQueueItemFinished(Core.Game.QueueItem item) {
            if (!string.Equals(item.GameId, this.game.GameId, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            this.Dispatcher.BeginInvoke(async () => {
                this.viaQueue = false;
                this.SetBusy(false);
                this.SyncProgressBar.IsIndeterminate = false;
                this.StatusText.Text = item.StatusText;
                try {
                    await this.RefreshStateAsync().ConfigureAwait(true);
                }
                catch (Exception ex) {
                    Core.Logging.Logger.Error(ex, "GamePage.OnQueueItemFinished.RefreshState");
                }
            });
        }

        /// <summary>Отражает снимок позиции очереди в тех же контролах, что и локальная закачка.</summary>
        private void ApplyQueueItem(Core.Game.QueueItem item) {
            try {
                this.viaQueue = true;
                if (!this.isBusy) {
                    this.SetBusy(true);
                }

                this.StatusText.Text = item.State == Core.Game.QueueItemState.Waiting
                    ? (item.QueuePosition > 1 ? $"В очереди · {item.QueuePosition}-я" : "Следующая в очереди")
                    : item.StatusText;

                this.SyncProgressBar.IsIndeterminate = item.TotalBytes <= 0 && item.State == Core.Game.QueueItemState.Running;

                if (item.TotalBytes > 0) {
                    this.SyncProgressBar.Value = Math.Min(100.0, Math.Max(0.0, item.BytesDownloaded * 100.0 / item.TotalBytes));
                    this.FilesSizeText.Text = $"{FormatSize(item.BytesDownloaded)} / {FormatSize(item.TotalBytes)}";

                    if (item.BytesPerSecond > 0) {
                        var remaining = item.TotalBytes - item.BytesDownloaded;
                        this.SpeedEtaText.Text = remaining > 0
                            ? $"{item.BytesPerSecond / 1024.0 / 1024.0:0.0} МБ/с · осталось {FormatEta(remaining / item.BytesPerSecond)}"
                            : $"{item.BytesPerSecond / 1024.0 / 1024.0:0.0} МБ/с";
                    }
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage.ApplyQueueItem: {ex.Message}");
            }
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
                var url = GameChangelogLoader.ArticleUrl(this.BaseApi, this.game.GameId, item.Slug);
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
            // Уходим со страницы во время локальной операции («Проверить файлы») — её не бросаем
            // «висеть» без владельца, отменяем. Операцию через downloadQueue (viaQueue) НЕ трогаем:
            // она принадлежит очереди, а не странице, и обязана продолжаться после ухода на главную.
            try {
                if (this.isBusy && !this.viaQueue) {
                    this.cts?.Cancel();
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage.Unloaded: отмена не выполнилась: {ex.Message}");
            }

            if (this.downloadQueue != null) {
                this.downloadQueue.ItemAdded -= this.OnQueueItemChanged;
                this.downloadQueue.ItemProgress -= this.OnQueueItemChanged;
                this.downloadQueue.ItemCompleted -= this.OnQueueItemFinished;
                this.downloadQueue.ItemRemoved -= this.OnQueueItemFinished;
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
                // ApplyState, а не ApplyMaintenanceToButtons: последняя умеет только ЗАПРЕЩАТЬ.
                // Пока звали её, окончание работ не снимало запрет — кнопка действия держала
                // подпись «Технические работы» и оставалась серой до конца следующей операции,
                // хотя запрета уже не было. Кнопку смены версии спасал пересчёт ниже, кнопку
                // действия — ничто.
                //
                // ApplyState заново берёт подписи из текущего состояния игры и последним шагом
                // сам применяет режим работ, поэтому одинаково верно отрабатывает и начало
                // работ, и их окончание.
                this.ApplyState(this.currentState);
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

        /// <summary>Связывает установку с контролами страницы: вынесенный сценарий сам про них не знает.</summary>
        private GameSyncUi BuildSyncUi() => new GameSyncUi {
            SetStatus = text => this.StatusText.Text = text,
            SetSpeedEta = text => this.SpeedEtaText.Text = text,
            SetFilesSize = text => this.FilesSizeText.Text = text,
            SetIndeterminate = value => this.SyncProgressBar.IsIndeterminate = value,
            ApplyMaintenanceToButtons = this.ApplyMaintenanceToButtons,
            ReportProgress = this.OnSyncProgress,
            Confirm = (text, title) =>
                MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes,
            ShowUserError = this.ShowUserError,
        };

        private async Task StartSyncAsync(string version, bool confirmDeletions = false) {
            var gid = this.game.GameId;

            // Подстраховка: работы могли начаться уже после отрисовки кнопок
            if (!this.syncRunner.TryBegin(gid, this.game.IsInstalled)) {
                return;
            }

            var localRoot = this.LocalRoot;

            // Предыдущая операция уже завершилась (isBusy проверен вызывающими):
            // освобождаем её источник, а не оставляем на сборщик мусора.
            var previousCts = this.cts;
            this.cts = new CancellationTokenSource();
            previousCts?.Dispose();
            var token = this.cts.Token;
            this.SetBusy(true);
            this.progressView.Reset();
            this.SyncProgressBar.Value = 0;
            this.SpeedEtaText.Text = string.Empty;
            this.FilesSizeText.Text = string.Empty;

            try {
                var request = new GameSyncRequest(
                    gid,
                    version,
                    this.BaseApi,
                    localRoot,
                    this.game.ExeRelativePath,
                    confirmDeletions);
                await this.syncRunner.RunAsync(request, token).ConfigureAwait(true);
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

        /// <summary>
        /// Единая точка показа ошибки, как на главной странице: пользователю — суть,
        /// технические подробности — в лог и в подсказку к строке состояния. Раньше
        /// каждая ветка catch собирала текст по-своему.
        /// </summary>
        /// <param name="userMessage">Короткое сообщение для пользователя.</param>
        /// <param name="ex">Исключение (уходит в лог).</param>
        /// <param name="context">Место, где ошибка поймана.</param>
        private void ShowUserError(string userMessage, Exception? ex = null, string? context = null) {
            try {
                if (ex != null) {
                    Core.Logging.Logger.Error(ex, context ?? "GamePage");
                }
                else if (!string.IsNullOrWhiteSpace(context)) {
                    Core.Logging.Logger.Error($"{context}: {userMessage}");
                }
            }
            catch (Exception logEx) {
                System.Diagnostics.Debug.WriteLine("GamePage.ShowUserError: " + logEx.Message);
            }

            try {
                this.StatusText.Text = userMessage;
                this.StatusText.ToolTip = ex == null ? null : "Подробнее: " + ex.Message;
            }
            catch (Exception uiEx) {
                Core.Logging.Logger.Warn($"GamePage.ShowUserError: {uiEx.Message}");
            }
        }

        private void OnSyncProgress(SyncProgress p, DateTime start) {
            try {
                var display = this.progressView.Describe(p, (DateTime.UtcNow - start).TotalSeconds);
                if (display.Status != null) {
                    this.StatusText.Text = display.Status;
                }

                if (display.Indeterminate.HasValue) {
                    this.SyncProgressBar.IsIndeterminate = display.Indeterminate.Value;
                }

                if (display.Value.HasValue) {
                    this.SyncProgressBar.Value = display.Value.Value;
                }

                if (display.SpeedEta != null) {
                    this.SpeedEtaText.Text = display.SpeedEta;
                }

                if (display.FilesSize != null) {
                    this.FilesSizeText.Text = display.FilesSize;
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

                if (busy) {
                    this.StatusText.ToolTip = null;
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, $"GamePage.SetBusy({busy})");
            }
        }
    }
}
