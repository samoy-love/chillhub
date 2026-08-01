// <copyright file="ISyncService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public interface ISyncService {
        /// <summary>
        /// Загружает манифест и проверяет его структуру.
        /// </summary>
        /// <param name="manifestUrl">URL манифеста.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Проверенный манифест.</returns>
        /// <exception cref="ManifestValidationException">
        /// Манифест содержит опасный путь, дубликат записи или запись без хешей.
        /// Реализация обязана бросить ДО того, как что-либо скачано: манифест
        /// определяет, какие исполняемые файлы окажутся на диске.
        /// </exception>
        Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct);

        Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct);

        /// <summary>
        /// Строит план различий с дополнительными настройками
        /// (принудительный пересчёт хешей, отчёт о прогрессе).
        /// </summary>
        /// <param name="manifest">Манифест эталонной версии.</param>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <param name="contentBaseUrl">База URL для скачивания файлов.</param>
        /// <param name="options">Настройки построения плана.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>План различий.</returns>
        Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct);

        Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct);
    }
}
