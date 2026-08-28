// <copyright file="GameStatusVerifier.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using static ChillHub.Core.Home.GameLocalState;
    using static ChillHub.Core.Home.SyncPlanLog;

    /// <summary>
    /// Фактическая проверка статуса игры по манифесту: сравнивает файлы на диске с эталоном
    /// и решает, установлена ли игра и требуется ли докачка.
    /// <para>
    /// Отсюда берётся надпись на кнопке действия. Ошибка в пользу «не установлено» стоит
    /// пользователю повторной закачки сборки целиком, поэтому при любом сбое проверки
    /// прежний статус остаётся нетронутым.
    /// </para>
    /// </summary>
    internal sealed class GameStatusVerifier {
        private readonly ISyncService sync;
        private readonly Func<string> baseApi;
        private readonly SpaceHint spaceHint;
        private readonly VerifiedGames verified;

        /// <summary>Initializes a new instance of the <see cref="GameStatusVerifier"/> class.</summary>
        /// <param name="sync">Служба синхронизации: манифест и план различий.</param>
        /// <param name="baseApi">База адреса сервера; читается на каждый вызов — её меняют в настройках.</param>
        /// <param name="spaceHint">Кеш оценок требуемого объёма скачивания.</param>
        /// <param name="verified">Набор игр с уже известным статусом.</param>
        internal GameStatusVerifier(ISyncService sync, Func<string> baseApi, SpaceHint spaceHint, VerifiedGames verified) {
            this.sync = sync;
            this.baseApi = baseApi;
            this.spaceHint = spaceHint;
            this.verified = verified;
        }

        /// <summary>
        /// Проверяет одну игру и проставляет ей <see cref="GameInfo.IsInstalled"/> и
        /// <see cref="GameInfo.NeedsUpdate"/>.
        /// </summary>
        /// <param name="game">Игра из списка.</param>
        /// <returns>Задача проверки.</returns>
        internal async Task VerifyAsync(GameInfo game) {
            if (game == null) {
                return;
            }

            try {
                // Если нет latest версии или идентификатора — определим по наличию локальных файлов
                if (string.IsNullOrWhiteSpace(game.GameId)) {
                    return;
                }

                var gid = game.GameId;
                var latest = game.LatestVersion;
                var hasLatest = !string.IsNullOrWhiteSpace(latest);
                var localRoot = GameLocalRoot(gid);
                var hasLocalFiles = HasAnyLocalGameFiles(localRoot);
                var unfinished = SimpleSyncService.HasUpdateMarker(localRoot);
                if (unfinished) {
                    // Обновление прерывали посередине: часть файлов новая, часть старая.
                    // Игру нельзя считать готовой — предлагаем докатить обновление.
                    game.IsInstalled = hasLocalFiles;
                    game.NeedsUpdate = true;
                    Logging.Logger.Warn($"VerifyGameStatusAsync gid={gid} найден маркер незавершённого обновления: {SimpleSyncService.ReadUpdateMarker(localRoot)}");

                    return;
                }

                if (!hasLatest) {
                    // Нет эталона для сравнения — считаем не установленной, если нет локальных файлов; иначе установленной без статуса обновления
                    game.IsInstalled = hasLocalFiles;

                    // Сборки игры на сервере нет, а модпак может быть: это игра,
                    // которую игрок держит своей копией из Steam. Сравнить нечего —
                    // кроме модпака, и вот его сравнить можно.
                    game.NeedsUpdate = hasLocalFiles && GameStatus.ModsOutOfDate(game);
                    Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} latest=<none> hasLocalFiles={hasLocalFiles} -> IsInstalled={game.IsInstalled} NeedsUpdate={game.NeedsUpdate}");

                    // Отложим Refresh до завершения всех проверок, чтобы не трясти UI на каждую игру
                    return;
                }

                // БЫСТРЫЙ ПУТЬ: версии сошлись и папку с прошлой проверки не трогали.
                //
                // Длинный путь ниже качает манифест и строит полный план различий — обходит
                // все его файлы, сверяет размеры и время, при промахе кеша считает хеши. Для
                // сборки в пятнадцать тысяч файлов это секунды дисковой работы на КАЖДУЮ игру
                // при КАЖДОМ запуске лаунчера, притом что ответ почти всегда один: ничего не
                // изменилось.
                //
                // Здесь тот же ответ собирается из трёх дешёвых: версия сборки на диске равна
                // серверной, модпак не отстал, а слепок папки совпадает с тем, каким мы его
                // запомнили после прошлой успешной проверки. Слепок снимается обходом
                // каталогов без чтения содержимого и расходится от любого практического
                // повреждения — удалённого файла, подменённого, оборванного обновления.
                // Разошёлся или его нет вовсе — идём длинным путём, как раньше.
                if (hasLocalFiles
                    && string.Equals(GameLocalState.ReadLocalVersion(gid).Trim(), latest!.Trim(), StringComparison.OrdinalIgnoreCase)
                    && !GameStatus.ModsOutOfDate(game)
                    && InstallFingerprint.Matches(localRoot)) {
                    game.IsInstalled = true;
                    game.NeedsUpdate = false;

                    // Качать нечего — подсказка о требуемом месте должна об этом знать,
                    // иначе она осталась бы от прошлого обновления.
                    this.spaceHint.Remember(gid, 0);
                    Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} слепок папки совпал — полная сверка не нужна");

                    return;
                }

                // Получаем манифест latest и план сравнения
                var manifestUrl = IntegrityChecker.ManifestUrl(this.baseApi(), gid, latest);
                Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} fetching manifest {manifestUrl}");
                var manifest = await this.sync.GetManifestAsync(manifestUrl, CancellationToken.None);
                var contentBase = IntegrityChecker.ContentBaseUrl(this.baseApi(), gid, latest);
                var plan = await SyncPlanner.PlanOffUiThreadAsync(this.sync, manifest, localRoot, contentBase, CancellationToken.None);
                Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} plan: downloads={plan.Downloads.Count} bytes={plan.TotalDownloadBytes} toDelete={plan.ToDelete.Count} emptyDirs={plan.EmptyDirsToCreate.Count}");
                LogPlanDownloads(gid, "verify", plan, localRoot);

                // Обновим кэш требуемого объёма скачивания
                this.spaceHint.Remember(gid, plan.TotalDownloadBytes);

                // Для статуса учитываем только недостающие/изменённые файлы.
                // Удаления (лишние локальные файлы, например логи/кэш) не считаем признаком "требуется обновление".
                var upToDate = plan.Downloads.Count == 0;
                if (!hasLocalFiles) {
                    // Пустая локальная папка — как не установлено, даже если план пуст (маловероятно)
                    game.IsInstalled = false;
                    game.NeedsUpdate = false;
                }
                else if (upToDate) {
                    game.IsInstalled = true;
                    game.NeedsUpdate = false;

                    // Файлы только что сверены с манифестом — запоминаем слепок папки, чтобы
                    // следующий запуск обошёлся без этого прохода. Игры, поставленные до
                    // появления слепков, получают его здесь же, на первой проверке.
                    InstallFingerprint.Save(localRoot);
                }
                else {
                    game.IsInstalled = true;
                    game.NeedsUpdate = true;
                }

                // У ИГРЫ С МОДАМИ ДВЕ ВЕРСИИ, И СРАВНИВАТЬ НАДО ОБЕ.
                //
                // План выше построен по манифесту СБОРКИ ИГРЫ. Активация модпака в
                // админке её не трогает, поэтому план выходил пустым, статус —
                // «свежая», и на карточке оставалось «Играть»: обновление до игрока
                // не доезжало вовсе. Заметить его можно было только вручную, из
                // «Об игре», и это ровно то, о чём пришла жалоба.
                if (game.IsInstalled && GameStatus.ModsOutOfDate(game)) {
                    game.NeedsUpdate = true;
                    Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} модпак на диске отличается от активного на сервере — нужно обновление");
                }

                Logging.Logger.Info($"VerifyGameStatusAsync gid={gid} result: IsInstalled={game.IsInstalled} NeedsUpdate={game.NeedsUpdate}");

                // Отложим Refresh до завершения всех проверок
            }
            catch (ManifestValidationException ex) {
                // Фоновая проверка статуса: молча статус не меняем, но в логе фиксируем именно
                // отклонённый манифест, а не «какую-то ошибку сети». Пользователь увидит явный
                // текст, когда нажмёт «Установить»/«Обновить» — см. StartUpdateAsync.
                Logging.Logger.Error(ex, $"VerifyGameStatusAsync({game?.GameId}): манифест не прошёл проверку");
            }
            catch (Exception ex) {
                // В случае ошибки проверки — не меняем текущий статус, только логируем
                Logging.Logger.Error(ex, $"VerifyGameStatusAsync({game?.GameId})");
            }
            finally {
                // Статус игры считается известным даже при ошибке проверки:
                // иначе кнопка действия останется заблокированной навсегда (C4)
                this.verified.MarkKnown(game?.GameId);
            }
        }
    }
}
