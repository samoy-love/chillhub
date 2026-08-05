// <copyright file="GameSyncRunner.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using static ChillHub.Core.Home.HomeFormat;

    /// <summary>Что именно нужно установить.</summary>
    /// <param name="GameId">Идентификатор игры.</param>
    /// <param name="Version">Версия, к которой приводим файлы.</param>
    /// <param name="BaseApi">База API из конфига.</param>
    /// <param name="LocalRoot">Папка игры на диске.</param>
    /// <param name="ExeRelativePath">Путь к exe игры внутри папки — по нему проверяется, не запущена ли игра.</param>
    /// <param name="IsVersionSwitch">Операция начата из блока переключения версии.</param>
    /// <param name="ConfirmDeletions">Спросить перед удалением файлов, которых нет в версии.</param>
    internal sealed record GameSyncRequest(
        string GameId,
        string Version,
        string BaseApi,
        string LocalRoot,
        string? ExeRelativePath,
        bool IsVersionSwitch,
        bool ConfirmDeletions);

    /// <summary>
    /// Связь установки с экраном: только колбэки, никаких контролов. По умолчанию всё
    /// молчит и на вопрос отвечает «нет» — так тест, забывший подставить колбэк,
    /// не полезет в модальное окно.
    /// </summary>
    internal sealed class GameSyncUi {
        /// <summary>Gets or sets вывод строки состояния.</summary>
        internal Action<string> SetStatus { get; set; } = _ => { };

        /// <summary>Gets or sets вывод строки «Скорость … • Осталось …».</summary>
        internal Action<string> SetSpeedEta { get; set; } = _ => { };

        /// <summary>Gets or sets вывод строки с объёмом закачки.</summary>
        internal Action<string> SetFilesSize { get; set; } = _ => { };

        /// <summary>Gets or sets переключение прогресс-бара в неопределённый режим.</summary>
        internal Action<bool> SetIndeterminate { get; set; } = _ => { };

        /// <summary>Gets or sets пересчёт кнопок под режим технических работ.</summary>
        internal Action ApplyMaintenanceToButtons { get; set; } = () => { };

        /// <summary>Gets or sets отрисовку очередного отчёта о прогрессе: отчёт и момент старта закачки.</summary>
        internal Action<SyncProgress, DateTime> ReportProgress { get; set; } = (_, _) => { };

        /// <summary>Gets or sets вопрос «да/нет» перед удалением файлов: текст, заголовок.</summary>
        internal Func<string, string, bool> Confirm { get; set; } = (_, _) => false;

        /// <summary>Gets or sets показ ошибки: сообщение, исключение, место.</summary>
        internal Action<string, Exception?, string?> ShowUserError { get; set; } = (_, _, _) => { };
    }

    /// <summary>
    /// Установка, обновление и переключение версии игры: от проверки режима технических
    /// работ до записи маркера версии. Всё общение с экраном идёт через <see cref="GameSyncUi"/>,
    /// служба синхронизации подставляется — поэтому сценарий проверяется целиком без окна.
    /// </summary>
    internal sealed class GameSyncRunner {
        private readonly ISyncService sync;
        private readonly GameSyncUi ui;

        /// <summary>Initializes a new instance of the <see cref="GameSyncRunner"/> class.</summary>
        /// <param name="sync">Служба синхронизации файлов.</param>
        /// <param name="ui">Колбэки к экрану страницы.</param>
        internal GameSyncRunner(ISyncService sync, GameSyncUi ui) {
            this.sync = sync;
            this.ui = ui;
        }

        /// <summary>Gets or sets текущее состояние режима технических работ.</summary>
        internal Func<MaintenanceStateView> Maintenance { get; set; } = DefaultMaintenance;

        /// <summary>Gets or sets опрос свободного места на диске игры.</summary>
        internal Func<string?, long> FreeSpaceFor { get; set; } = GameLocalState.GetAvailableFreeSpaceFor;

        /// <summary>Gets or sets запись маркера установленной версии.</summary>
        internal Action<string?, string?> WriteLocalVersion { get; set; } = (gid, version) => GameLocalState.WriteLocalVersion(gid, version);

        /// <summary>
        /// Текст вопроса перед удалением файлов, которых нет в версии. Проверка целостности
        /// удаляет всё, чего нет в манифесте: моды, скриншоты, сохранения, положенные в папку
        /// игры. Число файлов называется прямо — как в диалоге переключения версии.
        /// </summary>
        /// <param name="version">Версия, с которой сверяются файлы.</param>
        /// <param name="toDeleteCount">Сколько файлов будет удалено.</param>
        /// <returns>Текст вопроса.</returns>
        internal static string DeletionConfirmText(string version, int toDeleteCount)
            => $"В папке игры найдено файлов, которых нет в версии {version}: {toDeleteCount}.\n\n"
                + "Проверка удалит их: это могут быть моды, сохранения внутри папки игры и остатки прежних версий.\n\nПродолжить?";

        /// <summary>
        /// Проводит операцию целиком. Исключения наружу не выпускает: всё, что могло пойти
        /// не так, уже превращено в сообщение пользователю и запись в логе.
        /// </summary>
        /// <param name="request">Что устанавливаем.</param>
        /// <param name="token">Токен отмены.</param>
        /// <returns>Задача, завершающаяся вместе с операцией.</returns>
        internal async Task RunAsync(GameSyncRequest request, CancellationToken token) {
            var gid = request.GameId;
            var version = request.Version;
            try {
                // Игра запущена — файлы менять нельзя
                if (GameDiskInfo.IsGameRunning(request.ExeRelativePath, out var exeName)) {
                    this.ui.SetStatus($"Игра запущена ({exeName}). Закройте игру и повторите.");
                    return;
                }

                Logging.Logger.Info($"GamePage.StartSync gid={gid} version={version} switch={request.IsVersionSwitch}");
                this.ui.SetStatus("Загрузка манифеста…");
                this.ui.SetIndeterminate(true);

                var manifestUrl = IntegrityChecker.ManifestUrl(request.BaseApi, gid, version);
                var contentBase = IntegrityChecker.ContentBaseUrl(request.BaseApi, gid, version);
                var manifest = await this.sync.GetManifestAsync(manifestUrl, token).ConfigureAwait(true);

                this.ui.SetStatus("Сравнение файлов…");

                // PlanAsync только выглядит асинхронным: внутри полный обход папки игры с пересчётом
                // хешей, а Task возвращается уже завершённым. С UI-потока это подвешивает окно
                // на всё время обхода — уводим в пул потоков (как в IntegrityChecker).
                var plan = await Task.Run(() => this.sync.PlanAsync(manifest, request.LocalRoot, contentBase, token), token).ConfigureAwait(true);
                Logging.Logger.Info($"GamePage plan gid={gid} downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count}");

                // Проверка целостности удаляет всё, чего нет в манифесте: моды, скриншоты,
                // сохранения, положенные в папку игры. Спрашиваем до того, как это произойдёт,
                // и называем число файлов — как в диалоге переключения версии.
                if (request.ConfirmDeletions && plan.ToDelete.Count > 0) {
                    if (!this.ui.Confirm(DeletionConfirmText(version, plan.ToDelete.Count), "Проверка файлов")) {
                        this.ui.SetStatus("Проверка отменена.");
                        return;
                    }
                }

                // Свободного места может не хватить — предупреждаем до начала закачки
                var free = this.FreeSpaceFor(gid);
                if (plan.TotalDownloadBytes > 0) {
                    this.ui.SetFilesSize($"Нужно: {FormatSize(plan.TotalDownloadBytes)} ({FormatSize(free)} доступно)");
                    if (free > 0 && free < plan.TotalDownloadBytes) {
                        this.ui.SetStatus("Недостаточно свободного места.");
                        return;
                    }
                }

                var start = DateTime.UtcNow;
                var progress = new Progress<SyncProgress>(p => this.ui.ReportProgress(p, start));
                await this.sync.ExecuteAsync(plan, progress, token).ConfigureAwait(true);

                // Маркер версии обязан соответствовать тому, что реально установлено (в т.ч. после отката)
                this.WriteLocalVersion(gid, version);
                GameLocalStateChanges.MarkChanged();

                this.ui.SetStatus(request.IsVersionSwitch ? $"Готово. Установлена версия {version}." : "Готово.");
                this.ui.SetSpeedEta(string.Empty);
                Logging.Logger.Info($"GamePage.StartSync done gid={gid} version={version}");
            }
            catch (OperationCanceledException) {
                this.ui.SetStatus("Операция отменена.");
                this.ui.SetSpeedEta(string.Empty);
                Logging.Logger.Info($"GamePage.StartSync cancelled gid={gid} version={version}");
            }
            catch (ManifestValidationException ex) {
                // Манифест отклонён проверкой структуры: опасный путь, дубликат или
                // запись без хешей. Файлы игры не тронуты — говорим об этом прямо,
                // а не общей фразой «попробуйте ещё раз».
                this.ui.ShowUserError(ManifestValidator.UserMessage, ex, $"GamePage.StartSyncAsync.ManifestValidation(gid={gid}, version={version})");
            }
            catch (Exception ex) {
                var message = ex is IOException
                    ? "Не удалось записать файлы игры. Проверьте свободное место и права доступа."
                    : "Не удалось завершить операцию. Попробуйте ещё раз.";
                this.ui.ShowUserError(message, ex, $"GamePage.StartSyncAsync(gid={gid}, version={version})");
            }
        }

        /// <summary>
        /// Отсекает операцию до её начала, если что-то мешает: не определилась игра или
        /// сервер объявил технические работы. Работы могли начаться уже после отрисовки кнопок.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="isInstalled">Игра уже установлена — тогда смотрим на запрет обновления.</param>
        /// <returns>True, если операцию можно начинать.</returns>
        internal bool TryBegin(string? gameId, bool isInstalled) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                this.ui.SetStatus("Не удалось определить игру");
                return false;
            }

            var state = this.Maintenance();
            if (isInstalled ? state.BlocksUpdate : state.BlocksInstall) {
                this.ui.SetStatus(state.BannerText);
                this.ui.ApplyMaintenanceToButtons();
                return false;
            }

            return true;
        }

        private static MaintenanceStateView DefaultMaintenance() {
            var state = Core.Maintenance.MaintenanceService.Current;
            return new MaintenanceStateView(state.BlocksInstall, state.BlocksUpdate, state.BuildBannerText());
        }
    }

    /// <summary>
    /// Режим технических работ в том объёме, в каком он нужен странице игры.
    /// Отдельный тип, потому что настоящее состояние живёт в статическом
    /// <see cref="Core.Maintenance.MaintenanceService"/> и подменить его в тесте нечем.
    /// </summary>
    /// <param name="BlocksInstall">Установка запрещена.</param>
    /// <param name="BlocksUpdate">Обновление запрещено.</param>
    /// <param name="BannerText">Готовый текст объявления о работах.</param>
    internal readonly record struct MaintenanceStateView(bool BlocksInstall, bool BlocksUpdate, string BannerText);
}
