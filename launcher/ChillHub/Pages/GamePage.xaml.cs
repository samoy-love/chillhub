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
                Core.UI.CoverImage.SetUrl(this.GameIcon, this.game.IconUrl);
                this.ShowModpack();
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

            // Подписка на очередь — на КАЖДЫЙ показ, по той же причине, что и режим работ.
            // Пока она стояла только в конструкторе, страница глохла после возврата назад:
            // из неё открывают новость, Unloaded отписывает, а журнал навигации возвращает
            // ТОТ ЖЕ объект страницы — конструктор второй раз не выполняется. Полоса и
            // подписи после этого стояли неподвижно, пока игра качалась.
            this.SubscribeQueue();
            this.Loaded += (s, e) => this.SubscribeQueue();

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
                    this.ApplyBusyLook();
                    return;
                }

                var labels = GameStateResolver.Labels(state);
                this.StateText.Text = labels.StateText;
                this.ActionBtn.Content = labels.ActionText;
                this.ActionBtn.IsEnabled = true;
                this.ActionBtn.Style = this.TryFindResource("Style.Button.GamePrimary") as Style ?? this.ActionBtn.Style;

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

        /// <summary>
        /// Показывает установленный модпак со ссылкой на его страницу.
        /// <para>
        /// Строки нет вовсе у игры без модов: пустое «Модпак: —» не рассказывает о ней
        /// ничего. Ссылки нет, когда сервер не прислал слаг сообщества, — имя пакета
        /// остаётся, а вести в никуда мы не будем.
        /// </para>
        /// </summary>
        private void ShowModpack() {
            try {
                // Само правило — в Core.Mods.ModsLink: внутри страницы его никто не
                // проверит, а ошибка в нём выглядит как ссылка в никуда.
                var row = Core.Mods.ModsLink.RowFor(this.game.Mods);
                this.ModpackRow.Visibility = row.Visible ? Visibility.Visible : Visibility.Collapsed;
                if (!row.Visible) {
                    return;
                }

                this.ModpackText.Text = row.Name;
                this.ModpackText.Tag = row.Url;
                this.ModpackText.ToolTip = row.Url.Length > 0 ? row.Url : null;
                this.ModpackText.Cursor = row.Url.Length > 0 ? System.Windows.Input.Cursors.Hand : null;
                this.ModpackText.MouseLeftButtonUp -= this.ModpackText_MouseLeftButtonUp;
                if (row.Url.Length > 0) {
                    this.ModpackText.Foreground = (System.Windows.Media.Brush)this.FindResource("Brush.Accent");
                    this.ModpackText.MouseLeftButtonUp += this.ModpackText_MouseLeftButtonUp;
                }
            }
            catch (Exception ex) {
                // Строка о модпаке — справка, а не причина не открыть страницу игры.
                Core.Logging.Logger.Warn($"GamePage.ShowModpack: {ex.Message}");
            }
        }

        /// <summary>Открывает страницу модпака во внешнем браузере.</summary>
        /// <param name="sender">Строка с именем модпака.</param>
        /// <param name="e">Аргументы события.</param>
        private void ModpackText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            try {
                if ((sender as FrameworkElement)?.Tag is string url && url.Length > 0) {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = url,
                        UseShellExecute = true,
                    });
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Warn($"GamePage.ModpackText_MouseLeftButtonUp: {ex.Message}");
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

            // ВСЁ ДОЛГОЕ — ЧЕРЕЗ ОБЩУЮ ОЧЕРЕДЬ, включая проверку файлов. Страницу можно
            // закрыть или уйти на главную, не обрывая работу, и видно её в панели
            // загрузок наравне с остальным. Проверка казалась «быстрой сверкой», но она
            // читает и хеширует всю папку игры — десятки гигабайт: уход со страницы
            // обрывал её на середине, а в очереди её не было видно вовсе.
            if (this.downloadQueue != null) {
                this.StartQueuedSync(Core.Game.GameStateWork.QueueKindFor(this.currentState));
                return;
            }

            _ = this.StartSyncAsync(version, SyncKindFor(this.currentState));
        }

        /// <summary>
        /// Ставит установку/обновление в общую очередь загрузок вместо отдельного локального
        /// запуска. Раньше закачка жила только в <see cref="cts"/> этой страницы и обрывалась в
        /// <see cref="GamePage_Unloaded"/>, стоило уйти на главную — а в самой очереди её было не
        /// видно вовсе, потому что страница никогда её не пополняла.
        /// </summary>
        /// <param name="kind">Качать или проверять.</param>
        private void StartQueuedSync(Core.Game.QueueTaskKind kind = Core.Game.QueueTaskKind.Download) {
            var gid = this.game.GameId;
            if (!this.downloadQueue!.Enqueue(gid, kind)) {
                // Уже стоит в очереди (например, добавили с главной страницы или из
                // контекстного меню списка) — подхватываем ту работу, а не заводим вторую
                // по тем же файлам.
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

                // Чип состояния следует за позицией: «Ждёт очереди» → «Обновляется», как
                // только очередь до неё дошла, а не до следующего открытия страницы.
                this.ApplyBusyLook(item.State == Core.Game.QueueItemState.Waiting);

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

            this.UnsubscribeQueue();

            // Статическое событие переживёт страницу — отписываемся, иначе утечёт ссылка
            this.UnsubscribeMaintenance();
        }

        // Подписка на статическое событие живёт ровно столько, сколько страница показана.
        private bool maintenanceSubscribed;

        // То же для очереди: журнал навигации возвращает ту же страницу, и подписку,
        // снятую при уходе, надо восстанавливать при возврате.
        private bool queueSubscribed;

        /// <summary>Страница слушает очередь, пока показана; повторный вызов ничего не ломает.</summary>
        private void SubscribeQueue() {
            if (this.queueSubscribed || this.downloadQueue == null) {
                return;
            }

            this.downloadQueue.ItemAdded += this.OnQueueItemChanged;
            this.downloadQueue.ItemProgress += this.OnQueueItemChanged;
            this.downloadQueue.ItemCompleted += this.OnQueueItemFinished;
            this.downloadQueue.ItemRemoved += this.OnQueueItemFinished;
            this.queueSubscribed = true;

            // Страницу могли открыть (или вернуться на неё), пока эта игра уже качается, —
            // подхватываем состояние сразу, а не ждём следующего события.
            this.SyncFromQueueSnapshot();
        }

        /// <summary>Снимает подписку на очередь: страница ушла с экрана.</summary>
        private void UnsubscribeQueue() {
            if (!this.queueSubscribed || this.downloadQueue == null) {
                return;
            }

            this.downloadQueue.ItemAdded -= this.OnQueueItemChanged;
            this.downloadQueue.ItemProgress -= this.OnQueueItemChanged;
            this.downloadQueue.ItemCompleted -= this.OnQueueItemFinished;
            this.downloadQueue.ItemRemoved -= this.OnQueueItemFinished;
            this.queueSubscribed = false;
        }

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
                //
                // Строку «Статус» текстом баннера не заполняем: страница, открытая уже во время
                // работ, её и не заполняла — текст появлялся только у того, кто застал начало
                // работ на странице. Причина и срок висят баннером в шапке, кнопка объясняет
                // запрет подписью; одного этого достаточно и одинаково в обоих случаях.
                this.ApplyState(this.currentState);
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

        /// <summary>
        /// Чем нажатие кнопки действия является для пользователя. У установленной и свежей
        /// игры кнопка называется «Проверить файлы» — та же сверка с манифестом, но не
        /// установка и не обновление, и в статистике она проходит проверкой целостности.
        /// </summary>
        /// <param name="state">Текущее состояние игры на диске.</param>
        /// <returns>Вид операции для <see cref="GameSyncRequest"/>.</returns>
        private static SyncKind SyncKindFor(GameState state) => state switch {
            GameState.NotInstalled => SyncKind.Install,
            GameState.Installed => SyncKind.Repair,
            _ => SyncKind.Update,
        };

        private async Task StartSyncAsync(string version, SyncKind kind) {
            var gid = this.game.GameId;

            // Спрашиваем перед удалением лишнего ровно там, где пользователь просил
            // проверку: при установке и обновлении удалять нечего или незачем спрашивать.
            var confirmDeletions = kind == SyncKind.Repair;

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
                    confirmDeletions,
                    kind,
                    Game: this.game);
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
                if (busy) {
                    this.ApplyBusyLook();
                    this.StatusText.ToolTip = null;
                }
                else {
                    this.ActionBtn.IsEnabled = true;
                }
            }
            catch (Exception ex) {
                Core.Logging.Logger.Error(ex, $"GamePage.SetBusy({busy})");
            }
        }

        /// <summary>
        /// Вид страницы, пока игра ставится или обновляется: кнопка — красная «Отмена»
        /// (а не та же фиолетовая заливка, что и «Установить»), чип состояния — «Устанавливается»/«Обновляется»
        /// или «Ждёт очереди», а не «Состояние неизвестно», которым он встречал открытие
        /// страницы посреди закачки: <see cref="ApplyState"/> выходил до записи чипа.
        /// </summary>
        private void ApplyBusyLook(bool? waitingInQueue = null) {
            this.ActionBtn.Content = "Отмена";
            this.ActionBtn.IsEnabled = true;
            if (this.TryFindResource("Style.Button.Danger") is Style danger) {
                this.ActionBtn.Style = danger;
            }

            var waiting = waitingInQueue ?? (this.viaQueue && this.downloadQueue?.Snapshot()
                .FirstOrDefault(i => string.Equals(i.GameId, this.game.GameId, StringComparison.OrdinalIgnoreCase))
                ?.State == Core.Game.QueueItemState.Waiting);
            this.StateText.Text = waiting
                ? "Ждёт очереди"
                : this.currentState == GameState.NotInstalled ? "Устанавливается" : "Обновляется";
        }
    }
}
