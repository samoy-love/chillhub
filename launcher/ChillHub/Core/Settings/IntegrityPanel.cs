// <copyright file="IntegrityPanel.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Settings {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    /// <summary>
    /// Проверка целостности игры и восстановление файлов — как их видит страница настроек.
    /// <para>
    /// Про кнопки, список и полосу прогресса не знает: всё, что попадает в интерфейс,
    /// уходит через колбэки. Иначе проверить сценарии («игра не установлена», «нашли
    /// расхождения», «пользователь отменил») можно было бы только подняв окно, а именно
    /// эти сценарии пользователь и запускает, когда игра уже не работает.
    /// </para>
    /// </summary>
    internal sealed class IntegrityPanel {
        private readonly ISyncService sync;

        private CancellationTokenSource? integrityCts;
        private IntegrityReport? lastReport;
        private bool integrityBusy;
        private bool integrityRepairing;

        internal IntegrityPanel(ISyncService sync) => this.sync = sync;

        /// <summary>Показывает строку состояния под списком игр.</summary>
        internal Action<string> ShowStatus { get; set; } = _ => { };

        /// <summary>Переводит панель в занятое состояние и обратно.</summary>
        internal Action<bool> ShowBusy { get; set; } = _ => { };

        /// <summary>Показывает или прячет кнопку восстановления.</summary>
        internal Action<bool> ShowRepairButton { get; set; } = _ => { };

        /// <summary>Показывает прогресс: процент и строку состояния к нему.</summary>
        internal Action<double, string> ShowProgress { get; set; } = (_, _) => { };

        /// <summary>Идёт проверка или восстановление.</summary>
        internal bool Busy => this.integrityBusy;

        /// <summary>Отчёт последней проверки; null — проверки не было либо она не удалась.</summary>
        internal IntegrityReport? LastReport => this.lastReport;

        /// <summary>
        /// Выбирает игру, которую подставить в список: последнюю запускавшуюся, иначе первую
        /// установленную, иначе первую в списке.
        /// </summary>
        /// <param name="games">Список игр с сервера.</param>
        /// <param name="gamesPath">Общая папка игр.</param>
        /// <param name="lastId">Идентификатор последней запускавшейся игры.</param>
        /// <returns>Игра для подстановки; null — список пуст.</returns>
        internal static GameInfo? Preselect(IReadOnlyList<GameInfo> games, string? gamesPath, string? lastId) {
            if (games == null || games.Count == 0) {
                return null;
            }

            return games.FirstOrDefault(g => string.Equals(g.GameId, lastId, StringComparison.OrdinalIgnoreCase))
                   ?? games.FirstOrDefault(g => IntegrityChecker.HasAnyLocalGameFiles(IntegrityChecker.GameLocalRoot(gamesPath, g.GameId)))
                   ?? games[0];
        }

        /// <summary>
        /// Сверяет файлы выбранной игры с манифестом её последней версии.
        /// </summary>
        /// <param name="game">Выбранная в списке игра.</param>
        /// <returns>Задача проверки.</returns>
        internal async Task CheckAsync(GameInfo? game) {
            if (this.integrityBusy) {
                return;
            }

            if (game == null || string.IsNullOrWhiteSpace(game.GameId)) {
                this.ShowStatus("Выберите игру для проверки.");
                return;
            }

            this.lastReport = null;
            this.SetBusy(true, repairing: false);
            this.ShowRepairButton(false);
            this.ShowStatus("Проверка файлов…");

            var cts = new CancellationTokenSource();
            this.integrityCts = cts;
            var progress = new Progress<SyncProgress>(p => this.ReportProgress(p, "Проверено"));

            try {
                var report = await IntegrityChecker.CheckAsync(
                    this.sync,
                    ConfigService.Current.ApiBaseUrl,
                    game.GameId,
                    game.LatestVersion,
                    ConfigService.Current.GamesPath,
                    progress,
                    cts.Token);

                this.lastReport = report;
                this.ShowStatus(IntegrityChecker.Describe(report));
                this.ShowRepairButton(report.NeedsRepair);
            }
            catch (OperationCanceledException) {
                this.ShowStatus("Проверка отменена.");
            }
            catch (IntegrityCheckException ex) {
                this.ShowStatus(ex.Message);
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, "SettingsPage.IntegrityCheck");
                this.ShowStatus($"Не удалось проверить целостность: {ex.Message}");
            }
            finally {
                this.SetBusy(false, repairing: false);
                this.integrityCts = null;
                cts.Dispose();
            }
        }

        /// <summary>
        /// Докачивает недостающие и испорченные файлы по плану последней проверки.
        /// </summary>
        /// <returns>Задача восстановления.</returns>
        internal async Task RepairAsync() {
            if (this.integrityBusy) {
                return;
            }

            var report = this.lastReport;
            if (report == null || !report.NeedsRepair) {
                this.ShowStatus("Восстанавливать нечего — сначала выполните проверку.");
                return;
            }

            var confirmed = SettingsDialogs.Confirm(
                $"Будет перекачано файлов: {report.Plan.Downloads.Count}, удалено лишних: {report.Plan.ToDelete.Count}.\n\nПродолжить восстановление?",
                "Восстановление файлов игры");
            if (!confirmed) {
                return;
            }

            this.SetBusy(true, repairing: true);
            this.ShowRepairButton(false);
            this.ShowStatus("Восстановление…");

            var cts = new CancellationTokenSource();
            this.integrityCts = cts;
            var progress = new Progress<SyncProgress>(p => this.ReportProgress(p, StageToRu(p.Stage)));

            try {
                // Маркер .updating ставится и снимается внутри ExecuteAsync
                await this.sync.ExecuteAsync(report.Plan, progress, cts.Token);
                this.lastReport = null;
                this.ShowStatus("Восстановление завершено. Рекомендуем проверить целостность ещё раз.");
            }
            catch (OperationCanceledException) {
                this.ShowStatus("Восстановление отменено. Игра может остаться в незавершённом состоянии — повторите восстановление.");
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, "SettingsPage.IntegrityRepair");
                this.ShowStatus($"Не удалось восстановить файлы: {ex.Message}");
            }
            finally {
                this.SetBusy(false, repairing: false);
                this.integrityCts = null;
                cts.Dispose();
            }
        }

        /// <summary>Отменяет текущую проверку или восстановление по кнопке.</summary>
        internal void Cancel() {
            try {
                // Источник отмены живёт ровно столько, сколько идёт работа, и другого
                // признака «есть что отменять» у панели нет. Без этой проверки нажатая
                // в покое кнопка писала «Отмена…» поверх результата только что
                // закончившейся проверки — человек терял ответ, ради которого её и запускал.
                var cts = this.integrityCts;
                if (cts == null) {
                    return;
                }

                cts.Cancel();
                this.ShowStatus("Отмена…");
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"SettingsPage.IntegrityCancel: {ex.Message}");
            }
        }

        /// <summary>
        /// Уходим со страницы — отменяем незавершённую проверку, чтобы она не читала диск впустую.
        /// Восстановление НЕ трогаем: обрыв на фазе активации оставит маркер .updating
        /// и наполовину обновлённую игру, поэтому доводим его до конца в фоне.
        /// </summary>
        internal void LeavePage() {
            try {
                if (!this.integrityRepairing) {
                    this.integrityCts?.Cancel();
                }
            }
            catch (Exception ex) {
                // Проверка могла уже завершиться и освободить источник отмены
                ChillHub.Core.Logging.Logger.Warn($"SettingsPage.Unloaded: отмена проверки целостности: {ex.Message}");
            }
        }

        /// <summary>Этап синхронизации по-русски — так его видит пользователь в строке состояния.</summary>
        /// <param name="stage">Этап из <see cref="SyncProgress.Stage"/>.</param>
        /// <returns>Подпись для строки состояния.</returns>
        internal static string StageToRu(string stage) => stage switch {
            "Checking" => "Подготовка",
            "Downloading" => "Скачано",
            "Verifying" => "Проверка",
            "Activating" => "Установка",
            "Completed" => "Готово",
            _ => "Обработано",
        };

        private void ReportProgress(SyncProgress p, string label) {
            if (p == null) {
                return;
            }

            var percent = p.TotalFiles > 0 ? p.FilesDownloaded * 100.0 / p.TotalFiles : 0;
            this.ShowProgress(Math.Clamp(percent, 0, 100), $"{label}: {p.FilesDownloaded} из {p.TotalFiles}…");
        }

        private void SetBusy(bool busy, bool repairing) {
            this.integrityBusy = busy;
            this.integrityRepairing = busy && repairing;
            this.ShowBusy(busy);
        }
    }
}
