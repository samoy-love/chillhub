// <copyright file="ModsService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    /// <summary>Чем закончилась установка модпака.</summary>
    internal enum ModsSyncOutcome {
        /// <summary>Модпака у игры нет — делать нечего.</summary>
        NoModpack,

        /// <summary>Всё уже на месте, скачивать было нечего.</summary>
        UpToDate,

        /// <summary>Модпак установлен или обновлён.</summary>
        Installed,

        /// <summary>Установка сорвалась.</summary>
        Failed,
    }

    /// <summary>
    /// Итог установки модпака.
    /// </summary>
    /// <param name="Outcome">Чем закончилось.</param>
    /// <param name="Version">Версия модпака на диске после вызова.</param>
    /// <param name="Downloaded">Сколько байт скачано.</param>
    /// <param name="Removed">Сколько файлов удалено.</param>
    /// <param name="Message">Текст для пользователя, если что-то пошло не так.</param>
    internal sealed record ModsSyncResult(
        ModsSyncOutcome Outcome,
        string Version,
        long Downloaded,
        int Removed,
        string Message) {
        /// <summary>Успешна ли установка.</summary>
        internal bool Ok => this.Outcome is ModsSyncOutcome.UpToDate or ModsSyncOutcome.Installed or ModsSyncOutcome.NoModpack;
    }

    /// <summary>
    /// Установка модпака в папку игры тем же диффовым механизмом, что и сборки.
    /// <para>
    /// Модпак — это обычная версия на сервере: манифест с путями, размерами и хешами.
    /// Отличий от синхронизации игры ровно два, и оба живут в <see cref="PlanOptions"/>:
    /// модпак удаляет только СВОИ выбывшие файлы (иначе снёс бы игру из того же
    /// корня), а синхронизация игры не трогает файлы модпака.
    /// </para>
    /// <para>
    /// ПОРЯДОК ВАЖЕН: сначала модпак, потом игра. При переходе со старых сборок, где
    /// моды лежали внутри ZIP, файлы BepInEx уже на диске с теми же хешами — модпак
    /// засчитает их и скачает ноль байт, а чистая сборка игры их не тронет, потому что
    /// они уже принадлежат модпаку. В обратном порядке игрок сначала скачал бы
    /// удаление 5.8 ГБ, а следом их же обратно.
    /// </para>
    /// </summary>
    internal static class ModsService {
        /// <summary>Имя, под которым отчёты модпака показываются игроку.</summary>
        internal const string ScopeName = "Моды";

        /// <summary>
        /// Пересылает отчёты синхронизации дальше, пометив их как «Моды».
        /// <para>
        /// Не <see cref="Progress{T}"/>: тот доставляет отчёт через контекст
        /// синхронизации, а внешний получатель обычно и есть <see cref="Progress{T}"/>,
        /// созданный на UI-потоке. Второй такой же хоп добавил бы задержку и порядок
        /// доставки, которого никто не просил.
        /// </para>
        /// </summary>
        private sealed class ScopedProgress : IProgress<SyncProgress> {
            private readonly IProgress<SyncProgress> inner;

            internal ScopedProgress(IProgress<SyncProgress> inner) => this.inner = inner;

            /// <inheritdoc/>
            public void Report(SyncProgress value) {
                if (value == null) {
                    return;
                }

                // Копия, а не правка на месте: объект принадлежит службе
                // синхронизации и переиспользуется между отчётами.
                this.inner.Report(new SyncProgress {
                    FilesDownloaded = value.FilesDownloaded,
                    TotalFiles = value.TotalFiles,
                    BytesDownloaded = value.BytesDownloaded,
                    TotalBytes = value.TotalBytes,
                    Stage = value.Stage,
                    Scope = ScopeName,
                });
            }
        }

        /// <summary>
        /// Устанавливает или обновляет модпак в указанной папке.
        /// </summary>
        /// <param name="game">Игра из каталога, вместе с настройками модов.</param>
        /// <param name="targetDir">Папка игры: Steam-копия или сборка с сервера.</param>
        /// <param name="apiBaseUrl">База API сервера.</param>
        /// <param name="sync">Механизм синхронизации.</param>
        /// <param name="progress">Куда сообщать прогресс.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <param name="forceRehash">
        /// Пересчитывать хеши всех файлов, не доверяя размеру и дате. Нужно для
        /// «Проверить файлы»: испорченный мод обычно сохраняет размер, и без пересчёта
        /// проверка объявит его целым — то есть ровно не сделает того, ради чего её
        /// запускали.
        /// </param>
        /// <returns>Что получилось.</returns>
        internal static async Task<ModsSyncResult> EnsureAsync(
            GameInfo game,
            string targetDir,
            string apiBaseUrl,
            ISyncService sync,
            IProgress<SyncProgress>? progress,
            CancellationToken ct,
            bool forceRehash = false) {
            var mods = game?.Mods;
            if (game == null || mods is not { HasLatest: true }) {
                Logging.Logger.Info($"[mods] у игры '{game?.GameId}' нет активного модпака — установка не требуется");
                return new ModsSyncResult(ModsSyncOutcome.NoModpack, string.Empty, 0, 0, string.Empty);
            }

            if (string.IsNullOrWhiteSpace(targetDir)) {
                return new ModsSyncResult(ModsSyncOutcome.Failed, mods.Version, 0, 0, "Не выбрана папка игры.");
            }

            var baseUrl = (apiBaseUrl ?? string.Empty).TrimEnd('/');
            var manifestUrl = baseUrl + mods.ManifestUrl;
            var contentUrl = baseUrl + mods.ContentBaseUrl;
            var installed = GameLocalState.ReadModsVersionAt(targetDir);

            Logging.Logger.Info(
                $"[mods] установка модпака: игра='{game.GameId}' версия='{mods.Version}' " +
                $"установлено='{installed}' папка='{targetDir}'");

            try {
                var manifest = await sync.GetManifestAsync(manifestUrl, ct).ConfigureAwait(false);

                // Отчёты помечаются здесь, а не у вызывающих: их трое — очередь
                // загрузок, страница игры и установка в копию Steam, — и каждый
                // показывал бы «Скачивание…» без единого намёка, что качается модпак.
                var scoped = progress == null ? null : new ScopedProgress(progress);

                var options = PlanOptions.ForModPack(targetDir);
                options.Progress = scoped;
                options.ForceRehash = forceRehash;

                var plan = await sync.PlanAsync(manifest, targetDir, contentUrl, options, ct).ConfigureAwait(false);
                var toDownload = plan.Downloads?.Count ?? 0;
                var toDelete = plan.ToDelete?.Count ?? 0;
                Logging.Logger.Info(
                    $"[mods] план: скачать {toDownload} файлов ({plan.TotalDownloadBytes} байт), удалить {toDelete}");

                if (toDownload == 0 && toDelete == 0) {
                    // Всё совпало по хешам. Маркеры всё равно переписываем: именно этот
                    // случай — переход со старой сборки, где файлы уже лежат на диске,
                    // но принадлежность им ещё не назначена.
                    RecordInstallation(targetDir, manifest, mods.Version);
                    Logging.Logger.Info($"[mods] модпак '{mods.Version}' уже установлен, скачивать нечего");
                    return new ModsSyncResult(ModsSyncOutcome.UpToDate, mods.Version, 0, 0, string.Empty);
                }

                // ExecuteAsync объявляет progress без «?», хотя работает и с пустым:
                // подставляем заглушку, чтобы не спорить с анализатором и не менять
                // подпись общего интерфейса ради одного вызывающего.
                await sync.ExecuteAsync(plan, scoped ?? (IProgress<SyncProgress>)new Progress<SyncProgress>(), ct).ConfigureAwait(false);
                RecordInstallation(targetDir, manifest, mods.Version);

                Logging.Logger.Info(
                    $"[mods] модпак '{mods.Version}' установлен: скачано {plan.TotalDownloadBytes} байт, удалено {toDelete} файлов");
                return new ModsSyncResult(ModsSyncOutcome.Installed, mods.Version, plan.TotalDownloadBytes, toDelete, string.Empty);
            }
            catch (OperationCanceledException) {
                Logging.Logger.Info("[mods] установка модпака отменена");
                throw;
            }
            catch (ManifestValidationException ex) {
                // Манифест не прошёл проверку — это отказ сервера, а не сбой сети, и
                // повторять его бессмысленно.
                Logging.Logger.Error(ex, "[mods] манифест модпака отвергнут");
                Metrics.MetricsService.Error("mods_manifest_invalid", game.GameId);
                return new ModsSyncResult(
                    ModsSyncOutcome.Failed, mods.Version, 0, 0,
                    "Сервер прислал некорректный манифест модпака. Сообщите об этом — подробности уже в журнале.");
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, "[mods] ModsService.EnsureAsync");
                Metrics.MetricsService.Error("mods_sync_failed", game.GameId);
                return new ModsSyncResult(
                    ModsSyncOutcome.Failed, mods.Version, 0, 0,
                    "Не удалось установить моды. Попробуйте ещё раз.");
            }
        }

        /// <summary>
        /// Записывает, что именно установлено: версию и копию манифеста.
        /// <para>
        /// Копия манифеста — не украшение: из неё берётся ответ на вопрос «какие файлы
        /// в этой папке принадлежат модпаку». Без неё следующая синхронизация игры
        /// посчитает моды мусором, а следующая синхронизация модпака не будет знать,
        /// что удалять.
        /// </para>
        /// </summary>
        /// <param name="targetDir">Папка игры.</param>
        /// <param name="manifest">Установленный манифест.</param>
        /// <param name="version">Версия модпака.</param>
        private static void RecordInstallation(string targetDir, Manifest manifest, string version) {
            if (!GameLocalState.WriteInstalledModPackManifest(targetDir, manifest)) {
                Logging.Logger.Warn($"[mods] не удалось записать копию манифеста модпака в '{targetDir}'");
            }

            if (!GameLocalState.WriteModsVersionAt(targetDir, version)) {
                Logging.Logger.Warn($"[mods] не удалось записать маркер версии модпака в '{targetDir}'");
            }
        }

        /// <summary>
        /// Снимает модпак с папки игры: выключает Doorstop и забывает про установку.
        /// <para>
        /// Файлы намеренно НЕ удаляются. Это операция «перестать грузить моды», а не
        /// «освободить место»: удаление полутора гигабайт занимает минуты, а
        /// выключенный Doorstop и так их не тронет. Полное удаление — отдельное
        /// действие, через удаление игры.
        /// </para>
        /// </summary>
        /// <param name="targetDir">Папка игры.</param>
        /// <returns>true, если Doorstop удалось выключить.</returns>
        internal static bool Disable(string targetDir) {
            var ok = DoorstopConfig.SetEnabled(targetDir, false);
            Logging.Logger.Info($"[mods] моды выключены в '{targetDir}': {ok}");
            return ok;
        }
    }
}
