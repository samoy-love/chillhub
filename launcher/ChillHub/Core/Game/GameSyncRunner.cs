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
    using ChillHub.Core.Mods;
    using ChillHub.Core.Sync;

    using static ChillHub.Core.Home.HomeFormat;

    /// <summary>
    /// Чем операция является для пользователя. Технически все три случая — это один
    /// и тот же проход «сравнить с манифестом и докачать разницу», но в статистике
    /// они отвечают на разные вопросы, и складывать их в один счётчик нельзя:
    /// «Проверить файлы» у давно установленной игры не событие установки.
    /// </summary>
    internal enum SyncKind {
        /// <summary>Игры на диске не было.</summary>
        Install,

        /// <summary>Игра есть, но отличается от эталона (в том числе после обрыва).</summary>
        Update,

        /// <summary>Игра установлена и свежая: пользователь сам попросил сверить файлы.</summary>
        Repair,
    }

    /// <summary>
    /// Чем кончилась операция — всё, что об этом уходит в статистику.
    /// <para>
    /// Отдельный тип, а не восемь аргументов подряд: половина из них — числа одного
    /// типа, и перепутанные местами «скачано» и «весило бы целиком» дали бы не ошибку
    /// сборки, а тихо перевёрнутую экономию трафика в отчёте.
    /// </para>
    /// </summary>
    /// <param name="Kind">Установка, обновление или проверка файлов.</param>
    /// <param name="GameId">Идентификатор игры.</param>
    /// <param name="Version">Версия сборки.</param>
    /// <param name="Result">ok, fail или cancel.</param>
    /// <param name="DurationMs">Сколько ждал пользователь — от нажатия кнопки, а не от начала закачки.</param>
    /// <param name="Bytes">Сколько байт операция собиралась скачать.</param>
    /// <param name="FilesDownloaded">Сколько файлов операция собиралась скачать.</param>
    /// <param name="FilesTotal">Сколько файлов в сборке целиком.</param>
    /// <param name="FullBytes">Сколько весила бы та же операция полной загрузкой.</param>
    /// <param name="HashMismatches">Сколько файлов не сошлись по хешу.</param>
    /// <param name="ErrorCode">Код ошибки или null.</param>
    internal readonly record struct SyncOutcome(
        SyncKind Kind,
        string GameId,
        string Version,
        string Result,
        long DurationMs,
        long Bytes,
        long FilesDownloaded,
        long FilesTotal,
        long FullBytes,
        long HashMismatches,
        string? ErrorCode);

    /// <summary>Что именно нужно установить.</summary>
    /// <param name="GameId">Идентификатор игры.</param>
    /// <param name="Version">Версия, к которой приводим файлы.</param>
    /// <param name="BaseApi">База API из конфига.</param>
    /// <param name="LocalRoot">Папка игры на диске.</param>
    /// <param name="ExeRelativePath">Путь к exe игры внутри папки — по нему проверяется, не запущена ли игра.</param>
    /// <param name="ConfirmDeletions">Спросить перед удалением файлов, которых нет в версии.</param>
    /// <param name="Kind">
    /// Чем операция является для пользователя. Знает об этом только вызывающий: сам
    /// проход одинаков для установки, обновления и проверки, а различить их по плану
    /// нельзя — у свежеустановленной игры и у нетронутой он одинаково пуст.
    /// </param>
    internal sealed record GameSyncRequest(
        string GameId,
        string Version,
        string BaseApi,
        string LocalRoot,
        string? ExeRelativePath,
        bool ConfirmDeletions,
        SyncKind Kind = SyncKind.Install,
        GameInfo? Game = null);

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
        /// Запоминает слепок папки игры после успешной синхронизации. Шов тот же и по той
        /// же причине, что у записи маркера версии: настоящая реализация пишет на диск.
        /// </summary>
        internal Action<string?> SaveFingerprint { get; set; } = root => Sync.InstallFingerprint.Save(root);

        /// <summary>
        /// Gets or sets отправку исхода операции в статистику. Подставляется по той же
        /// причине, что и <see cref="WriteLocalVersion"/>: настоящая уходит в сеть, и без
        /// шва проверить, что отмена не считается провалом, можно было бы только
        /// поднятым сервером.
        /// </summary>
        internal Action<SyncOutcome> ReportOutcome { get; set; } = DefaultReportOutcome;

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

            // Отсчёт идёт от нажатия кнопки, а не от начала закачки: пользователь ждёт
            // и загрузку манифеста, и обход папки с пересчётом хешей — на большой игре
            // это половина времени операции.
            var opStart = DateTime.UtcNow;

            // План нужен и в catch: он единственный знает, сколько байт и файлов
            // операция собиралась тронуть, а без этого сорвавшаяся установка уходит
            // в статистику голым фактом «не получилось».
            DiffPlan? plan = null;
            try {
                // Игра запущена — файлы менять нельзя. Метрики нет намеренно: операция
                // не начиналась и не срывалась, лаунчер даже не ходил на сервер.
                if (GameDiskInfo.IsGameRunning(request.ExeRelativePath, out var exeName)) {
                    this.ui.SetStatus($"Игра запущена ({exeName}). Закройте игру и повторите.");
                    return;
                }

                // МОДПАК ИДЁТ ПЕРВЫМ, и это не косметика.
                //
                // При переходе со старых сборок, где моды лежали внутри ZIP игры, файлы
                // BepInEx уже на диске с теми же хешами: модпак засчитает их, скачает ноль
                // байт и запишет, что они принадлежат ему. Синхронизация игры следом их не
                // тронет. В обратном порядке игрок сначала скачал бы удаление почти шести
                // гигабайт, а сразу за ним — их же обратно.
                if (request.Game?.Mods is { HasLatest: true }) {
                    // Прогресс модпака идёт ТЕМ ЖЕ путём, что и прогресс игры.
                    //
                    // Раньше сюда передавался null, и полтора гигабайта модов уезжали
                    // при неподвижной полосе и одной строке «Установка модов…»: со
                    // стороны — зависший лаунчер. Установка тех же модов в копию Steam
                    // при этом прогресс показывала, и два пути расходились на ровном
                    // месте.
                    this.ui.SetStatus("Установка модов…");
                    this.ui.SetIndeterminate(true);
                    var modsStart = DateTime.UtcNow;
                    var modsProgress = new Progress<SyncProgress>(p => this.ui.ReportProgress(p, modsStart));
                    var mods = await ModsService.EnsureAsync(
                        request.Game, request.LocalRoot, request.BaseApi, this.sync, modsProgress, token,
                        // «Проверить файлы» обязана пересчитать хеши и у модов: битая
                        // DLL мода почти всегда сохраняет размер, и без пересчёта
                        // проверка объявит её целой.
                        forceRehash: request.Kind == SyncKind.Repair)
                        .ConfigureAwait(true);
                    if (!mods.Ok) {
                        this.ui.SetStatus(mods.Message);

                        // Об исходе отчитываемся: в отличие от ветки «игра запущена»,
                        // здесь лаунчер уже сходил на сервер и операция именно
                        // сорвалась. Без этой строки неудачное обновление исчезает из
                        // метрик целиком — ни успеха, ни ошибки.
                        this.Report(request, null, "fail", opStart, "mods_sync_failed");
                        return;
                    }

                    // Строки модпака стираются перед игрой: «Скорость» и «файлов • байт»
                    // от закончившегося шага, висящие над начавшимся, врут об объёме.
                    this.ui.SetSpeedEta(string.Empty);
                    this.ui.SetFilesSize(string.Empty);
                }

                Logging.Logger.Info($"GamePage.StartSync gid={gid} version={version}");
                this.ui.SetStatus("Загрузка манифеста…");
                this.ui.SetIndeterminate(true);

                var manifestUrl = IntegrityChecker.ManifestUrl(request.BaseApi, gid, version);
                var contentBase = IntegrityChecker.ContentBaseUrl(request.BaseApi, gid, version);
                var manifest = await this.sync.GetManifestAsync(manifestUrl, token).ConfigureAwait(true);

                this.ui.SetStatus("Сравнение файлов…");

                // PlanAsync только выглядит асинхронным: внутри полный обход папки игры с пересчётом
                // хешей, а Task возвращается уже завершённым. С UI-потока это подвешивает окно
                // на всё время обхода — уводим в пул потоков (как в IntegrityChecker).
                //
                // Через SyncPlanner, а не голым Task.Run: там же строятся настройки плана
                // для ИГРЫ, а в них — список файлов установленного модпака. Он лежит в той
                // же папке, и без этого списка обновление игры вынесло бы все моды как
                // «лишние файлы».
                plan = await SyncPlanner.PlanOffUiThreadAsync(this.sync, manifest, request.LocalRoot, contentBase, token)
                    .ConfigureAwait(true);
                Logging.Logger.Info($"GamePage plan gid={gid} downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count}");

                // Проверка целостности удаляет всё, чего нет в манифесте: моды, скриншоты,
                // сохранения, положенные в папку игры. Спрашиваем до того, как это произойдёт,
                // и называем число файлов — как в диалоге переключения версии.
                if (request.ConfirmDeletions && plan.ToDelete.Count > 0) {
                    if (!this.ui.Confirm(DeletionConfirmText(version, plan.ToDelete.Count), "Проверка файлов")) {
                        this.ui.SetStatus("Проверка отменена.");
                        Report(request, plan, "cancel", opStart);
                        return;
                    }
                }

                // Свободного места может не хватить — предупреждаем до начала закачки
                var free = this.FreeSpaceFor(gid);
                if (plan.TotalDownloadBytes > 0) {
                    this.ui.SetFilesSize($"Нужно: {FormatSize(plan.TotalDownloadBytes)} ({FormatSize(free)} доступно)");
                    if (free > 0 && free < plan.TotalDownloadBytes) {
                        this.ui.SetStatus("Недостаточно свободного места.");

                        // Именно та ошибка, о которой пишут в обратную связь словами
                        // «ничего не качается»: без кода в статистике её видно только
                        // по чужому скриншоту.
                        Report(request, plan, "fail", opStart, "no_disk_space");
                        return;
                    }
                }

                var start = DateTime.UtcNow;
                var progress = new Progress<SyncProgress>(p => this.ui.ReportProgress(p, start));
                await this.sync.ExecuteAsync(plan, progress, token).ConfigureAwait(true);

                // Маркер версии обязан соответствовать тому, что реально установлено (в т.ч. после отката)
                this.WriteLocalVersion(gid, version);

                // Слепок папки — сразу за маркером: файлы только что приведены к манифесту,
                // и с этого момента любое расхождение означает, что их трогали снаружи.
                // Пока слепка нет, проверка статуса ходит длинным путём (см. InstallFingerprint).
                this.SaveFingerprint(request.LocalRoot);
                GameLocalStateChanges.MarkChanged();

                // Размер в «Установка и удаление программ» считается вместе с папкой игр
                // (Core/Shell/InstalledAppsEntry.cs), а только что она изменилась на
                // несколько гигабайт. Без этого число там осталось бы прежним до
                // следующего запуска лаунчера. Обход папки уходит в фон.
                Shell.InstalledAppsEntry.RefreshInBackground();

                this.ui.SetStatus("Готово.");
                this.ui.SetSpeedEta(string.Empty);
                Logging.Logger.Info($"GamePage.StartSync done gid={gid} version={version}");
                Report(request, plan, "ok", opStart);
            }
            catch (OperationCanceledException) {
                this.ui.SetStatus("Операция отменена.");
                this.ui.SetSpeedEta(string.Empty);
                Logging.Logger.Info($"GamePage.StartSync cancelled gid={gid} version={version}");

                // Отмена — не ошибка: отдельный результат как раз затем и существует,
                // чтобы брошенные закачки не портили ни долю неудач, ни среднее время.
                Report(request, plan, "cancel", opStart);
            }
            catch (ManifestValidationException ex) {
                // Манифест отклонён проверкой структуры: опасный путь, дубликат или
                // запись без хешей. Файлы игры не тронуты — говорим об этом прямо,
                // а не общей фразой «попробуйте ещё раз».
                this.ui.ShowUserError(ManifestValidator.UserMessage, ex, $"GamePage.StartSyncAsync.ManifestValidation(gid={gid}, version={version})");
                Report(request, plan, "fail", opStart, "manifest_invalid");
            }
            catch (Exception ex) {
                var message = ex is IOException
                    ? "Не удалось записать файлы игры. Проверьте свободное место и права доступа."
                    : "Не удалось завершить операцию. Попробуйте ещё раз.";
                this.ui.ShowUserError(message, ex, $"GamePage.StartSyncAsync(gid={gid}, version={version})");

                // Код классифицирует проблему и только её: текст исключения содержит
                // пути и имена файлов пользователя, а метрика — публичная сводка.
                Report(request, plan, "fail", opStart, ex is IOException ? "sync_io" : "sync_failed");
            }
        }

        /// <summary>
        /// Отправляет исход операции в статистику.
        /// <para>
        /// Живёт здесь, а не у вызывающих, ровно потому, что вызывающих двое: страница
        /// игры и очередь загрузок. Пока метрики не было вовсе, админка показывала ноль
        /// установок при живых установках — и починить это в одном из двух мест значило
        /// бы получить половину правды.
        /// </para>
        /// <para>
        /// Ничего не бросает: <see cref="Metrics.MetricsService.Report"/> и так глушит
        /// свои ошибки, но зовут этот метод в том числе из catch — исключение отсюда
        /// подменило бы собой настоящую причину сбоя.
        /// </para>
        /// </summary>
        /// <param name="request">Операция, о которой отчитываемся.</param>
        /// <param name="plan">План: null, если сорвались до его построения.</param>
        /// <param name="result">ok, fail или cancel.</param>
        /// <param name="opStart">Момент нажатия кнопки (UTC).</param>
        /// <param name="errorCode">Код ошибки — только для result=fail.</param>
        private void Report(
            GameSyncRequest request, DiffPlan? plan, string result, DateTime opStart, string? errorCode = null) {
            try {
                // Объём берём из плана, а не из отчётов о прогрессе: при отмене и при
                // ошибке последний отчёт мог не прийти вовсе, а размер работы всё равно
                // известен — и «сорвалось на 12 ГБ» отличается от «сорвалось на 12 МБ».
                this.ReportOutcome(new SyncOutcome(
                    request.Kind,
                    request.GameId,
                    request.Version,
                    result,
                    DurationMs: (long)(DateTime.UtcNow - opStart).TotalMilliseconds,
                    Bytes: plan?.TotalDownloadBytes ?? 0,
                    FilesDownloaded: plan?.TotalFilesToDownload ?? 0,
                    FilesTotal: plan?.TotalManifestFiles ?? 0,
                    FullBytes: plan?.TotalManifestBytes ?? 0,
                    HashMismatches: plan?.HashMismatches ?? 0,
                    ErrorCode: errorCode));
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"GameSyncRunner: метрика операции не отправлена: {ex.Message}");
            }
        }

        /// <summary>
        /// Раскладывает исход по видам событий сервера. Отдельный метод, потому что это
        /// единственное место, где вид операции превращается в вид события: «Проверить
        /// файлы» — не установка, и складывать их в один счётчик значило бы вписать в
        /// отчёт установки, которых не было.
        /// </summary>
        /// <param name="o">Исход операции.</param>
        private static void DefaultReportOutcome(SyncOutcome o) {
            switch (o.Kind) {
                case SyncKind.Repair:
                    Metrics.MetricsService.IntegrityCheck(
                        o.GameId, o.Version, o.Result == "ok", o.FilesTotal, o.HashMismatches);
                    break;
                case SyncKind.Update:
                    Metrics.MetricsService.GameUpdate(
                        o.GameId, o.Version, o.Result, o.DurationMs, o.Bytes,
                        o.FilesDownloaded, o.FilesTotal, o.FullBytes);
                    break;
                default:
                    Metrics.MetricsService.GameInstall(
                        o.GameId, o.Version, o.Result, o.DurationMs, o.Bytes,
                        o.FilesDownloaded, o.FilesTotal, o.FullBytes);
                    break;
            }

            if (!string.IsNullOrEmpty(o.ErrorCode)) {
                // Отдельным событием, а не полем внутри предыдущего: «Топ ошибок» и
                // раскрытие кода в конкретные события сервер собирает только по событиям
                // вида error, а поле errorCode внутри установки не читает никто.
                Metrics.MetricsService.Error(o.ErrorCode, o.GameId);
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
