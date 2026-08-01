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
    /// Результат сверки локальных файлов игры с манифестом версии.
    /// </summary>
    public sealed class IntegrityReport {
        /// <summary>
        /// Gets план восстановления: его можно передать в <see cref="ISyncService.ExecuteAsync"/>.
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
        /// Gets a value indicating whether всё в порядке и восстанавливать нечего.
        /// </summary>
        public bool IsOk => this.MissingFiles == 0 && this.CorruptedFiles == 0 && !this.HasUnfinishedUpdate;

        /// <summary>
        /// Gets a value indicating whether есть что чинить.
        /// </summary>
        public bool NeedsRepair => this.Plan.Downloads.Count > 0 || this.Plan.ToDelete.Count > 0;
    }

    /// <summary>
    /// Проверка целостности установленной игры: сверяет локальные файлы с манифестом
    /// версии, пересчитывая хеши с диска (кеш хешей намеренно обходится).
    /// Общая логика вынесена сюда, чтобы её могли использовать и страница настроек,
    /// и главная страница, не дублируя код.
    /// </summary>
    public static class IntegrityChecker {
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
        public static string GameLocalRoot(string gamesPath, string gameId)
            => Path.Combine(gamesPath ?? string.Empty, gameId ?? string.Empty);

        /// <summary>
        /// Есть ли в папке игры хоть один настоящий файл игры
        /// (служебные .staging/.version/.updating не считаются).
        /// </summary>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <returns>true, если игра выглядит установленной.</returns>
        public static bool HasAnyLocalGameFiles(string localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot)) {
                    return false;
                }

                foreach (var path in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)) {
                    var rel = Path.GetRelativePath(localRoot, path).Replace('\\', '/');
                    if (rel.StartsWith(".staging/", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (string.Equals(rel, ".version", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (string.Equals(rel, SimpleSyncService.UpdateMarkerFileName, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    return true;
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"IntegrityChecker.HasAnyLocalGameFiles({localRoot})");
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
                throw new IntegrityCheckException($"Игра не установлена: в папке «{localRoot}» нет файлов. Сначала установите игру.");
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
            var options = new PlanOptions { ForceRehash = true, Progress = progress };

            // PlanAsync внутри синхронный и упирается в чтение диска — уводим в пул потоков,
            // иначе UI встанет на всё время пересчёта хешей.
            var plan = await Task.Run(() => sync.PlanAsync(manifest, localRoot, contentBase, options, ct), ct).ConfigureAwait(false);

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

            return report;
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
