// <copyright file="SyncPlanner.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    /// <summary>Построение плана различий так, чтобы обход папки игры не выполнялся на UI-потоке.</summary>
    internal static class SyncPlanner {
        /// <summary>
        /// Строит план различий, гарантированно не занимая UI-поток.
        /// <see cref="ISyncService.PlanAsync(Manifest, string, string, CancellationToken)"/> только выглядит
        /// асинхронным: внутри полный обход папки игры с пересчётом хешей, а результат возвращается
        /// через уже завершённый Task. При вызове с UI-потока окно замирает на всё время обхода
        /// (гигабайты SHA-256/BLAKE3), Windows рисует «Не отвечает», и даже «Отмена» не нажимается.
        /// Тот же приём применён в <see cref="IntegrityChecker"/>.
        /// </summary>
        /// <param name="sync">Служба синхронизации.</param>
        /// <param name="manifest">Манифест эталонной версии.</param>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <param name="contentBaseUrl">База URL для скачивания файлов.</param>
        /// <param name="token">Токен отмены.</param>
        /// <returns>План различий.</returns>
        internal static Task<DiffPlan> PlanOffUiThreadAsync(
            ISyncService sync, Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken token) =>
            Task.Run(() => sync.PlanAsync(manifest, localRoot, contentBaseUrl, token), token);
    }
}
