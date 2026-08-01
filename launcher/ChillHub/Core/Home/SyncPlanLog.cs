// <copyright file="SyncPlanLog.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.IO;

    using ChillHub.Core.Sync;

    /// <summary>
    /// Диагностический дамп плана синхронизации в лог: что скачиваем, что удаляем,
    /// какие пустые каталоги создаём. Нужен для разбора жалоб «обновление скачивает всё заново».
    /// </summary>
    internal static class SyncPlanLog {
        private const int MaxItemsPerSection = 10;

        /// <summary>Пишет в лог краткую выжимку плана (не более 10 строк на секцию).</summary>
        internal static void LogPlanDownloads(string gid, string stage, DiffPlan plan, string localRoot) {
            if (plan == null) {
                return;
            }

            try {
                int total = plan.Downloads.Count;
                int limit = Math.Min(total, MaxItemsPerSection);
                for (int i = 0; i < limit; i++) {
                    var t = plan.Downloads[i];
                    var rel = t.RelativePath;
                    var hasSha = !string.IsNullOrWhiteSpace(t.Sha256);
                    var hasB3 = !string.IsNullOrWhiteSpace(t.Blake3);
                    var localPath = Path.Combine(localRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    bool exists = File.Exists(localPath);
                    long len = exists ? TryGetLength(localPath) : 0;
                    Logging.Logger.Info($"Plan[{stage}] gid={gid} file='{rel}' size={t.Size} hasSha={hasSha} hasB3={hasB3} localExists={exists} localLen={len}");
                }

                if (total > limit) {
                    Logging.Logger.Info($"Plan[{stage}] gid={gid} ... and {total - limit} more files");
                }

                LogPaths(gid, stage, plan.ToDelete, localRoot, "toDelete", isDirectory: false);
                LogPaths(gid, stage, plan.EmptyDirsToCreate, localRoot, "emptyDir", isDirectory: true);
            }
            catch (Exception ex) {
                // Диагностика не должна ломать установку — только фиксируем сам факт сбоя.
                Logging.Logger.Warn($"SyncPlanLog gid={gid} stage={stage}: не удалось выгрузить план: {ex.Message}");
            }
        }

        private static void LogPaths(string gid, string stage, System.Collections.Generic.IReadOnlyList<string> items, string localRoot, string label, bool isDirectory) {
            int total = items.Count;
            int limit = Math.Min(total, MaxItemsPerSection);
            for (int i = 0; i < limit; i++) {
                var rel = items[i];
                var path = Path.Combine(localRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                bool exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
                Logging.Logger.Info($"Plan[{stage}] gid={gid} {label}='{rel}' localExists={exists}");
            }

            if (total > limit) {
                Logging.Logger.Info($"Plan[{stage}] gid={gid} ... and {total - limit} more {label} entries");
            }
        }

        private static long TryGetLength(string path) {
            try {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"SyncPlanLog: размер '{path}' недоступен: {ex.Message}");
                return 0;
            }
        }
    }
}
