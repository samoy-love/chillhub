// <copyright file="IntegrityChecker.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Ошибка проверки целостности, текст которой можно показать пользователю как есть
    /// (игра не выбрана, не установлена, нет опубликованной версии и т.п.).
    /// </summary>
    public sealed class IntegrityCheckException : Exception {
        public IntegrityCheckException(string message)
            : base(message) {
        }

        public IntegrityCheckException(string message, Exception inner)
            : base(message, inner) {
        }
    }

    /// <summary>
    /// Результат сверки локальных файлов с манифестами: отдельно по игре, отдельно по модпаку.
    /// <para>
    /// Части раздельные не ради красоты отчёта. В одной папке лежат два независимых
    /// набора файлов с разными версиями и разными хозяевами, и «повреждено 3» без
    /// указания части не отвечает ни что чинить, ни в каком порядке. Поля без приставки
    /// <c>Mods</c> относятся к ИГРЕ и значат ровно то же, что значили до появления модов.
    /// </para>
    /// <para>
    /// Запись, а не класс: второй проход достраивает готовый отчёт игры через <c>with</c>,
    /// и ручное переписывание полей теряло бы новое поле при каждом расширении отчёта.
    /// </para>
    /// </summary>
    public sealed record IntegrityReport {
        /// <summary>
        /// Gets план восстановления ИГРЫ: его можно передать в <see cref="ISyncService.ExecuteAsync"/>.
        /// </summary>
        public DiffPlan Plan { get; init; } = new DiffPlan();

        /// <summary>
        /// Gets версия, с которой сверялись.
        /// </summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>
        /// Gets всего файлов в манифесте.
        /// </summary>
        public int TotalFiles { get; init; }

        /// <summary>
        /// Gets сколько файлов отсутствует локально.
        /// </summary>
        public int MissingFiles { get; init; }

        /// <summary>
        /// Gets сколько файлов есть, но их содержимое не совпадает с манифестом.
        /// </summary>
        public int CorruptedFiles { get; init; }

        /// <summary>
        /// Gets сколько лишних файлов найдено (есть локально, нет в манифесте).
        /// </summary>
        public int ExtraFiles { get; init; }

        /// <summary>
        /// Gets a value indicating whether обновление игры было прервано (в корне остался маркер .updating).
        /// </summary>
        public bool HasUnfinishedUpdate { get; init; }

        /// <summary>
        /// Gets план восстановления МОДПАКА; null — второго прохода не было (у игры нет
        /// модпака либо он не установлен). Отдельный план, а не дополнение к плану игры:
        /// у него другой хозяин корня, другая база контента и другое правило удаления.
        /// </summary>
        public DiffPlan? ModsPlan { get; init; }

        /// <summary>
        /// Gets манифест модпака, с которым сверялись; null — второго прохода не было.
        /// <para>
        /// Нужен починке: она обязана переписать копию установленного манифеста (иначе
        /// принадлежность файлов останется от прошлой версии) и защитить свежие файлы
        /// модов от плана игры, который строился ещё до починки модов.
        /// </para>
        /// </summary>
        public Manifest? ModsManifest { get; init; }

        /// <summary>Gets версия модпака, с которой сверялись.</summary>
        public string ModsVersion { get; init; } = string.Empty;

        /// <summary>Gets всего файлов в манифесте модпака.</summary>
        public int ModsTotalFiles { get; init; }

        /// <summary>Gets сколько файлов модпака отсутствует локально.</summary>
        public int ModsMissingFiles { get; init; }

        /// <summary>Gets сколько файлов модпака есть, но их содержимое не совпадает с манифестом.</summary>
        public int ModsCorruptedFiles { get; init; }

        /// <summary>
        /// Gets сколько файлов осталось от ПРЕДЫДУЩЕЙ версии модпака и выбыло из новой.
        /// Файлы игры сюда не попадают: модпак владеет только тем, что положил сам.
        /// </summary>
        public int ModsExtraFiles { get; init; }

        /// <summary>
        /// Gets a value indicating whether второй проход по манифесту модпака состоялся.
        /// false — у игры нет модпака или он не установлен, и это не ошибка.
        /// </summary>
        public bool HasMods => this.ModsPlan != null;

        /// <summary>
        /// Gets a value indicating whether всё в порядке и восстанавливать нечего.
        /// <para>
        /// Обе части сразу: половина ответа здесь хуже, чем никакого. Сказать «всё в
        /// порядке» при трёх пропавших файлах мода — значит отправить игрока искать
        /// причину вылета где угодно, только не там, где она есть.
        /// </para>
        /// </summary>
        public bool IsOk => this.MissingFiles == 0
            && this.CorruptedFiles == 0
            && this.ModsMissingFiles == 0
            && this.ModsCorruptedFiles == 0
            && !this.HasUnfinishedUpdate;

        /// <summary>
        /// Gets a value indicating whether есть что чинить — в игре, в модпаке или в обоих.
        /// </summary>
        public bool NeedsRepair => this.Plan.Downloads.Count > 0
            || this.Plan.ToDelete.Count > 0
            || (this.ModsPlan != null && (this.ModsPlan.Downloads.Count > 0 || this.ModsPlan.ToDelete.Count > 0));
    }

    /// <summary>
    /// Проверка целостности установленной игры: сверяет локальные файлы с манифестом
    /// версии, пересчитывая хеши с диска (кеш хешей намеренно обходится).
    /// Общая логика вынесена сюда, чтобы её могли использовать и страница настроек,
    /// и главная страница, не дублируя код.
    /// </summary>
    public static class IntegrityChecker {
        /// <summary>
        /// Имя файла-маркера с установленной версией игры. Единственное объявление на весь клиент:
        /// <c>Core.Home.GameLocalState</c> ссылается сюда, чтобы имя не разъезжалось.
        /// </summary>
        public const string VersionMarkerFileName = ".version";

        /// <summary>
        /// Имя файла-маркера с версией установленного модпака — второй маркер в ТОМ ЖЕ корне.
        /// <para>
        /// Модпак ставится прямо в папку игры (BepInEx работает только так), поэтому у
        /// одной папки два независимых манифеста и, соответственно, две независимые
        /// версии. Один общий маркер их не описывает: моды обновляются, не трогая сборку,
        /// и наоборот.
        /// </para>
        /// </summary>
        public const string ModsVersionMarkerFileName = ".mods.version";

        /// <summary>
        /// Отпечаток содержимого установленного модпака.
        /// <para>
        /// Отдельно от версии, потому что отвечает на другой вопрос. Версия говорит,
        /// КАКОЙ пакет стоит, отпечаток — КАКОЕ у него дерево: админка умеет
        /// пересобрать тот же пакет и опубликовать другое дерево под тем же именем.
        /// </para>
        /// </summary>
        public const string ModsRevisionMarkerFileName = ".mods.revision";

        /// <summary>
        /// Имя файла с копией УСТАНОВЛЕННОГО манифеста модпака.
        /// <para>
        /// Он отвечает на единственный вопрос, ответа на который иначе нет: какими путями
        /// в этой папке владеет модпак. Из него берётся «предыдущий набор путей» для
        /// ограниченного удаления при обновлении модов и список неприкосновенных путей
        /// для синхронизации игры.
        /// </para>
        /// </summary>
        public const string ModsManifestFileName = ".mods.manifest.json";

        /// <summary>
        /// URL манифеста конкретной версии игры.
        /// </summary>
        /// <param name="apiBaseUrl">База API.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия.</param>
        /// <returns>Полный URL манифеста.</returns>
        public static string ManifestUrl(string apiBaseUrl, string gameId, string version)
            => $"{(apiBaseUrl ?? string.Empty).TrimEnd('/')}/manifests/{gameId}/{version}.json";

        /// <summary>
        /// База URL для скачивания файлов конкретной версии игры.
        /// </summary>
        /// <param name="apiBaseUrl">База API.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия.</param>
        /// <returns>Базовый URL контента.</returns>
        public static string ContentBaseUrl(string apiBaseUrl, string gameId, string version)
            => $"{(apiBaseUrl ?? string.Empty).TrimEnd('/')}/content/{gameId}/{version}/files";

        /// <summary>
        /// Путь к локальной папке игры внутри общей папки игр.
        /// </summary>
        /// <param name="gamesPath">Общая папка игр.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Полный путь к корню игры.</returns>
        public static string GameLocalRoot(string? gamesPath, string? gameId)
            => Path.Combine(gamesPath ?? string.Empty, gameId ?? string.Empty);

        /// <summary>
        /// Есть ли в папке игры хоть один настоящий файл ИГРЫ
        /// (служебные .staging/.version/.updating/.mods.* не считаются).
        /// <para>
        /// Файлы установленного модпака тоже не считаются, хотя лежат в той же папке:
        /// «поставить моды, не ставя игру» — законный сценарий (моды можно поставить в
        /// Steam-копию), и без этой проверки полторы тысячи файлов BepInEx выдавали бы
        /// игру за установленную. Дальше по этому ответу лаунчер решает, писать на
        /// кнопке «Играть» или «Установить», а проверка целостности — отказывать ли
        /// со словами «игра не установлена».
        /// </para>
        /// <para>
        /// СНАЧАЛА СМОТРИМ КОРЕНЬ, И ТОЛЬКО ПОТОМ ЛЕЗЕМ ВГЛУБЬ. Ответ от этого не меняется —
        /// он всё тот же «есть ли хоть один файл игры где угодно», — но у установленной
        /// игры он находится с первого чтения каталога: исполняемый файл и библиотеки
        /// движка лежат именно в корне. Полный обход остаётся запасным путём для редкого
        /// случая, когда в корне одни служебные файлы и модпак.
        /// </para>
        /// <para>
        /// Разница не теоретическая: вопрос задаётся при каждой проверке статусов (то есть
        /// на каждом запуске лаунчера, по разу на игру) и при каждом пересчёте вариантов
        /// запуска — а тот считается на UI-потоке. Обход дерева сборки в пятнадцать тысяч
        /// файлов ради ответа «да» стоил заметно дороже самого ответа.
        /// </para>
        /// </summary>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <returns>true, если игра выглядит установленной.</returns>
        public static bool HasAnyLocalGameFiles(string localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot)) {
                    return false;
                }

                // Пути модпака берём из его же установленного манифеста, а не из списка
                // «известных папок модов»: у каждой игры они свои.
                var modPack = new HashSet<string>(
                    Home.GameLocalState.ReadInstalledModPackPaths(localRoot),
                    StringComparer.OrdinalIgnoreCase);

                return HasGameFileIn(localRoot, modPack, SearchOption.TopDirectoryOnly)
                    || HasGameFileIn(localRoot, modPack, SearchOption.AllDirectories);
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"IntegrityChecker.HasAnyLocalGameFiles({localRoot})");
            }

            return false;
        }

        /// <summary>Есть ли файл игры среди перечисленных этим обходом.</summary>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <param name="modPack">Пути установленного модпака — они за файлы игры не считаются.</param>
        /// <param name="scope">Только корень или всё дерево.</param>
        /// <returns>true, если нашёлся хоть один файл игры.</returns>
        private static bool HasGameFileIn(string localRoot, HashSet<string> modPack, SearchOption scope) {
            foreach (var path in Directory.EnumerateFiles(localRoot, "*", scope)) {
                var rel = Path.GetRelativePath(localRoot, path).Replace('\\', '/');
                if (rel.StartsWith(".staging/", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // Один список служебных файлов на весь клиент: .updating, .version
                // и оба файла состояния модпака.
                if (SimpleSyncService.IsServiceRelFile(rel)) {
                    continue;
                }

                if (modPack.Contains(rel)) {
                    continue;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Сверяет локальные файлы игры с манифестом указанной версии.
        /// Хеши считаются заново с диска, поэтому вызов долгий — запускается в пуле потоков.
        /// </summary>
        /// <param name="sync">Сервис синхронизации.</param>
        /// <param name="apiBaseUrl">База API.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия для сверки (обычно latest).</param>
        /// <param name="gamesPath">Общая папка игр.</param>
        /// <param name="progress">Отчёт о прогрессе (этап "Checking").</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Отчёт о целостности.</returns>
        /// <exception cref="IntegrityCheckException">Проверку выполнить нельзя, текст пригоден для показа пользователю.</exception>
        public static async Task<IntegrityReport> CheckAsync(
            ISyncService sync,
            string apiBaseUrl,
            string gameId,
            string version,
            string gamesPath,
            IProgress<SyncProgress>? progress,
            CancellationToken ct) {
            if (sync == null) {
                throw new ArgumentNullException(nameof(sync));
            }

            if (string.IsNullOrWhiteSpace(gameId)) {
                throw new IntegrityCheckException("Игра не выбрана.");
            }

            if (string.IsNullOrWhiteSpace(version)) {
                throw new IntegrityCheckException("У этой игры нет опубликованной версии — не с чем сравнивать файлы.");
            }

            var localRoot = GameLocalRoot(gamesPath, gameId);
            if (!HasAnyLocalGameFiles(localRoot)) {
                // Путь — в лог: в сообщении пользователю он ничего не объясняет,
                // зато утекает в скриншоты и в отчёты вместе с именем пользователя Windows.
                ChillHub.Core.Logging.Logger.Warn($"IntegrityCheck gid={gameId}: в папке '{localRoot}' нет файлов игры");
                throw new IntegrityCheckException("Игра не установлена: в папке игры нет файлов. Сначала установите игру.");
            }

            Manifest manifest;
            try {
                manifest = await sync.GetManifestAsync(ManifestUrl(apiBaseUrl, gameId, version), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                throw new IntegrityCheckException($"Не удалось получить манифест версии {version}: {ex.Message}", ex);
            }

            var contentBase = ContentBaseUrl(apiBaseUrl, gameId, version);

            // ForGame, а не «пустые настройки»: проверка целостности сверяет папку с
            // манифестом ИГРЫ, а в той же папке живёт модпак. Без списка его файлов
            // отчёт объявил бы «лишними» все моды разом, а «Восстановить» их удалило бы.
            var options = PlanOptions.ForGame(localRoot);
            options.ForceRehash = true;
            options.Progress = progress;

            // PlanAsync внутри синхронный и упирается в чтение диска — уводим в пул потоков,
            // иначе UI встанет на всё время пересчёта хешей.
            var plan = await Task.Run(() => sync.PlanAsync(manifest, localRoot, contentBase, options, ct), ct).ConfigureAwait(false);

            var (missing, corrupted) = CountProblems(plan, localRoot, ct);
            var totalFiles = CountManifestFiles(manifest);
            var report = new IntegrityReport {
                Plan = plan,
                Version = version,
                TotalFiles = totalFiles,
                MissingFiles = missing,
                CorruptedFiles = corrupted,
                ExtraFiles = plan.ToDelete.Count,
                HasUnfinishedUpdate = SimpleSyncService.HasUpdateMarker(localRoot),
            };

            try {
                ChillHub.Core.Logging.Logger.Info(
                    $"IntegrityCheck gid={gameId} ver={version} total={totalFiles} missing={missing} corrupted={corrupted} extra={report.ExtraFiles} unfinished={report.HasUnfinishedUpdate}");
            }
            catch {
            }

            // Проверку целостности запускает сам пользователь, и запускает её
            // тогда, когда игра уже ведёт себя странно. Частота этих запусков и
            // доля неудачных — единственный сигнал о порче файлов, который
            // приходит раньше жалобы в обратную связь.
            ChillHub.Core.Metrics.MetricsService.IntegrityCheck(
                gameId, version, report.IsOk, totalFiles, plan.HashMismatches);

            return report;
        }

        /// <summary>
        /// Второй проход проверки — по манифесту МОДПАКА, и дополнение им отчёта игры.
        /// <para>
        /// Первый проход сверяет папку с манифестом игры и о модах ничего не знает: он
        /// видит их как чужие файлы и молча пропускает. Без второго прохода «Проверить
        /// файлы» на игре с модами отвечала бы «всё в порядке» на половине сборки —
        /// а вылетает игра как раз из-за второй половины.
        /// </para>
        /// <para>
        /// Модпака у игры нет или он не установлен — прохода просто не происходит, и это
        /// НЕ ошибка: моды ставятся по желанию, и их отсутствие не делает игру битой.
        /// </para>
        /// </summary>
        /// <param name="sync">Сервис синхронизации.</param>
        /// <param name="apiBaseUrl">База API: адреса модпака приходят от сервера относительными.</param>
        /// <param name="mods">Описание модпака из карточки игры; null — модов нет.</param>
        /// <param name="localRoot">Корень папки игры — тот же, что и у первого прохода.</param>
        /// <param name="gameReport">Отчёт первого прохода, который дополняем.</param>
        /// <param name="progress">Отчёт о прогрессе (этап "Checking").</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Отчёт, дополненный частью про моды.</returns>
        /// <exception cref="IntegrityCheckException">Манифест модпака недоступен или отвергнут.</exception>
        public static async Task<IntegrityReport> CheckModsAsync(
            ISyncService sync,
            string apiBaseUrl,
            ModsInfo? mods,
            string localRoot,
            IntegrityReport gameReport,
            IProgress<SyncProgress>? progress,
            CancellationToken ct) {
            if (sync == null) {
                throw new ArgumentNullException(nameof(sync));
            }

            if (gameReport == null) {
                throw new ArgumentNullException(nameof(gameReport));
            }

            if (mods is not { HasLatest: true } || string.IsNullOrWhiteSpace(mods.ManifestUrl)) {
                ChillHub.Core.Logging.Logger.Info("[mods] у игры нет опубликованного модпака — второй проход не нужен");
                return gameReport;
            }

            // Установленность спрашиваем у маркера, а не у наличия файлов: BepInEx мог
            // приехать внутри старой сборки игры, и без маркера мы не знаем ни версии,
            // ни того, какими путями модпак владеет.
            var installed = Home.GameLocalState.ReadModsVersionAt(localRoot);
            if (string.IsNullOrWhiteSpace(installed)) {
                ChillHub.Core.Logging.Logger.Info("[mods] модпак не установлен — второй проход не нужен");
                return gameReport;
            }

            // Сервер отдаёт адреса модпака относительными (см. ModsInfo): база API живёт
            // в настройках клиента и в карточку игры не уезжает.
            var baseUrl = (apiBaseUrl ?? string.Empty).TrimEnd('/');
            var manifestUrl = baseUrl + mods.ManifestUrl;
            var contentBase = baseUrl + mods.ContentBaseUrl;
            ChillHub.Core.Logging.Logger.Info(
                $"[mods] проверка целостности модпака: последняя='{mods.Version}' установлена='{installed}'");

            Manifest manifest;
            try {
                manifest = await sync.GetManifestAsync(manifestUrl, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, "[mods] манифест модпака не получен");
                throw new IntegrityCheckException($"Не удалось получить манифест модов {mods.Version}: {ex.Message}", ex);
            }

            // ForModPack, а не ForGame: манифест модов владеет только своими файлами.
            // С правилом «лишнее — всё, чего нет в манифесте» проверка объявила бы
            // лишними десять гигабайт игры и предложила бы их удалить.
            var options = PlanOptions.ForModPack(localRoot);
            options.ForceRehash = true;
            options.Progress = progress;

            var plan = await Task.Run(() => sync.PlanAsync(manifest, localRoot, contentBase, options, ct), ct).ConfigureAwait(false);

            var (missing, corrupted) = CountProblems(plan, localRoot, ct);
            var totalFiles = CountManifestFiles(manifest);
            ChillHub.Core.Logging.Logger.Info(
                $"[mods] сверено: всего={totalFiles} отсутствует={missing} повреждено={corrupted} выбыло={plan.ToDelete.Count}");

            return gameReport with {
                ModsPlan = plan,
                ModsManifest = manifest,
                ModsVersion = mods.Version ?? string.Empty,
                ModsTotalFiles = totalFiles,
                ModsMissingFiles = missing,
                ModsCorruptedFiles = corrupted,
                ModsExtraFiles = plan.ToDelete.Count,
            };
        }

        /// <summary>
        /// Чинит найденное: СНАЧАЛА модпак, ПОТОМ игру.
        /// <para>
        /// Порядок обязателен, и он не косметический. Файлы модов уже лежат на диске:
        /// модпак засчитает их, скачает ноль байт и запишет, что владеет ими, — после
        /// чего синхронизация игры их не тронет. В обратном порядке игра сначала удалит
        /// несколько гигабайт «лишних» файлов, а модпак тут же скачает их обратно.
        /// </para>
        /// </summary>
        /// <param name="sync">Сервис синхронизации.</param>
        /// <param name="report">Отчёт проверки — источник обоих планов.</param>
        /// <param name="progress">Отчёт о прогрессе; null — прогресс никому не нужен.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Задача починки.</returns>
        public static async Task RepairAsync(
            ISyncService sync,
            IntegrityReport report,
            IProgress<SyncProgress>? progress,
            CancellationToken ct) {
            if (sync == null) {
                throw new ArgumentNullException(nameof(sync));
            }

            if (report == null) {
                throw new ArgumentNullException(nameof(report));
            }

            // ExecuteAsync объявляет progress без «?», хотя работает и с пустым:
            // подставляем заглушку, чтобы не менять подпись общего интерфейса.
            var sink = progress ?? new Progress<SyncProgress>();

            var modsPlan = report.ModsPlan;
            if (modsPlan != null) {
                if (NeedsWork(modsPlan)) {
                    ChillHub.Core.Logging.Logger.Info(
                        $"[mods] починка модпака '{report.ModsVersion}': скачать {modsPlan.Downloads.Count} файл(ов), удалить {modsPlan.ToDelete.Count}");
                    await sync.ExecuteAsync(modsPlan, sink, ct).ConfigureAwait(false);
                    RecordModsOwnership(report);
                    ChillHub.Core.Logging.Logger.Info("[mods] починка модпака завершена");
                }
                else {
                    ChillHub.Core.Logging.Logger.Info("[mods] в модпаке чинить нечего");
                }

                ProtectModsFilesFromGameRepair(report);
            }

            if (!NeedsWork(report.Plan)) {
                ChillHub.Core.Logging.Logger.Info($"IntegrityRepair gid={report.Plan.GameId}: в файлах игры чинить нечего");
                return;
            }

            ChillHub.Core.Logging.Logger.Info(
                $"IntegrityRepair gid={report.Plan.GameId} ver={report.Version}: скачать {report.Plan.Downloads.Count} файл(ов), удалить {report.Plan.ToDelete.Count}");
            await sync.ExecuteAsync(report.Plan, sink, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Человекочитаемое описание результата проверки.
        /// </summary>
        /// <param name="report">Отчёт о целостности.</param>
        /// <returns>Текст для показа пользователю.</returns>
        public static string Describe(IntegrityReport report) {
            if (report == null) {
                return string.Empty;
            }

            if (report.HasMods) {
                // Части названы поимённо только когда их две. Приставка «Игра:» перед
                // единственной строкой ничего не различает, а отчёт без модов читают все
                // игры без модпака — им текст менять незачем.
                var both = DescribePart("Игра", report.Version, report.TotalFiles, report.MissingFiles, report.CorruptedFiles, report.ExtraFiles)
                    + " "
                    + DescribePart("Моды", report.ModsVersion, report.ModsTotalFiles, report.ModsMissingFiles, report.ModsCorruptedFiles, report.ModsExtraFiles);
                if (report.HasUnfinishedUpdate) {
                    both += " Кроме того, предыдущее обновление было прервано.";
                }

                return both;
            }

            if (report.IsOk && report.ExtraFiles == 0) {
                return $"Всё в порядке: проверено файлов — {report.TotalFiles}, повреждённых нет (версия {report.Version}).";
            }

            var parts = new List<string>();
            if (report.MissingFiles > 0) {
                parts.Add($"отсутствует — {report.MissingFiles}");
            }

            if (report.CorruptedFiles > 0) {
                parts.Add($"повреждено — {report.CorruptedFiles}");
            }

            if (report.ExtraFiles > 0) {
                parts.Add($"лишних — {report.ExtraFiles}");
            }

            var summary = parts.Count > 0 ? string.Join(", ", parts) : "расхождений в файлах нет";
            var text = $"Проверено файлов: {report.TotalFiles} (версия {report.Version}). Проблемы: {summary}.";
            if (report.HasUnfinishedUpdate) {
                text += " Кроме того, предыдущее обновление было прервано.";
            }

            return text;
        }

        // Есть ли в плане хоть одно действие. Пустой план прогонять через ExecuteAsync
        // не нужно и вредно: он ставит маркер незавершённого обновления и проверяет
        // свободное место ради нуля работы.
        private static bool NeedsWork(DiffPlan? plan)
            => plan != null && (plan.Downloads.Count > 0 || plan.ToDelete.Count > 0);

        // Раскладывает загрузки плана на «файла нет» и «файл есть, но содержимое другое».
        // Различие видно человеку: «повреждено 300» после отключения питания — совсем не
        // то же самое, что «отсутствует 300» после чистки антивирусом.
        private static (int Missing, int Corrupted) CountProblems(DiffPlan plan, string localRoot, CancellationToken ct) {
            var missing = 0;
            var corrupted = 0;
            foreach (var t in plan.Downloads) {
                ct.ThrowIfCancellationRequested();
                var localPath = Path.Combine(localRoot, t.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(localPath)) {
                    corrupted++;
                }
                else {
                    missing++;
                }
            }

            return (missing, corrupted);
        }

        /// <summary>
        /// Записывает, что модпак снова принадлежит себе: копию манифеста и маркер версии.
        /// <para>
        /// Без этого починка модов оставляет на диске файлы НОВОЙ версии при списке
        /// владения от СТАРОЙ — и следующая же синхронизация игры вынесет их как лишние.
        /// </para>
        /// </summary>
        /// <param name="report">Отчёт: в нём и манифест модпака, и его версия, и корень.</param>
        private static void RecordModsOwnership(IntegrityReport report) {
            var root = report.ModsPlan?.LocalRoot;
            if (report.ModsManifest == null || string.IsNullOrWhiteSpace(root)) {
                return;
            }

            if (!Home.GameLocalState.WriteInstalledModPackManifest(root, report.ModsManifest)) {
                ChillHub.Core.Logging.Logger.Warn("[mods] не удалось записать копию манифеста модпака после починки");
            }

            if (!Home.GameLocalState.WriteModsVersionAt(root, report.ModsVersion)) {
                ChillHub.Core.Logging.Logger.Warn("[mods] не удалось записать маркер версии модпака после починки");
            }

            if (!Home.GameLocalState.WriteModsRevisionAt(root, Mods.ModPackDigest.Of(report.ModsManifest))) {
                ChillHub.Core.Logging.Logger.Warn("[mods] не удалось записать отпечаток модпака после починки");
            }
        }

        /// <summary>
        /// Выводит файлы модпака из-под плана ИГРЫ перед его выполнением.
        /// <para>
        /// План игры строился ДО починки модов, и список чужих файлов в нём — от той
        /// версии модпака, что лежала на диске тогда. Файл, появившийся в модах только
        /// что, для него посторонний: он его либо удалит как лишний, либо перезапишет
        /// содержимым из сборки. Хозяин у пути один, и это модпак.
        /// </para>
        /// </summary>
        /// <param name="report">Отчёт: источник манифеста модпака и плана игры.</param>
        private static void ProtectModsFilesFromGameRepair(IntegrityReport report) {
            var manifest = report.ModsManifest;
            if (manifest?.Files == null || manifest.Files.Count == 0) {
                return;
            }

            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files) {
                var rel = SimpleSyncService.NormalizeRel(f?.Path);
                if (rel.Length > 0) {
                    owned.Add(rel);
                }
            }

            var plan = report.Plan;
            var removedDeletes = plan.ToDelete.RemoveAll(rel => owned.Contains(SimpleSyncService.NormalizeRel(rel)));
            var removedDownloads = plan.Downloads.RemoveAll(t => owned.Contains(SimpleSyncService.NormalizeRel(t?.RelativePath)));

            var known = new HashSet<string>(plan.ForeignPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var rel in owned) {
                if (known.Add(rel)) {
                    plan.ForeignPaths.Add(rel);
                }
            }

            if (removedDeletes > 0 || removedDownloads > 0) {
                ChillHub.Core.Logging.Logger.Info(
                    $"[mods] из плана игры убрано после починки модов: удалений {removedDeletes}, загрузок {removedDownloads}");
            }
        }

        // Одна часть отчёта: «Игра: 2809 файлов (версия 1.0.7), повреждений нет».
        private static string DescribePart(string name, string version, int total, int missing, int corrupted, int extra) {
            var parts = new List<string>();
            if (missing > 0) {
                parts.Add($"отсутствует — {missing}");
            }

            if (corrupted > 0) {
                parts.Add($"повреждено — {corrupted}");
            }

            if (extra > 0) {
                parts.Add($"лишних — {extra}");
            }

            var problems = parts.Count > 0 ? string.Join(", ", parts) : "повреждений нет";
            var ver = string.IsNullOrWhiteSpace(version) ? string.Empty : $" (версия {version})";
            return $"{name}: {total} {PluralizeFileRu(total)}{ver}, {problems}.";
        }

        // Русское склонение слова «файл» по числу: «2404 файла», а не «2404 файлов».
        private static string PluralizeFileRu(int n) {
            var n10 = n % 10;
            var n100 = n % 100;
            if (n10 == 1 && n100 != 11) {
                return "файл";
            }

            if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) {
                return "файла";
            }

            return "файлов";
        }

        // Считает файлы манифеста так же, как их считает построитель плана:
        // без служебного маркера и без спецфайла FreeTP/.hash.
        private static int CountManifestFiles(Manifest manifest) {
            var n = 0;
            foreach (var mf in manifest.Files) {
                var rel = (mf.Path ?? string.Empty).Replace('\\', '/').TrimStart('/');
                if (rel.Equals(SimpleSyncService.UpdateMarkerFileName, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (rel.Equals("freetp/.hash", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                n++;
            }

            return n;
        }
    }
}
