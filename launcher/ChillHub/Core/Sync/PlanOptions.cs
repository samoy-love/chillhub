// <copyright file="PlanOptions.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;

    /// <summary>
    /// Настройки построения плана различий.
    /// </summary>
    public sealed class PlanOptions {
        /// <summary>
        /// Настройки по умолчанию: с кешем хешей, без отчёта о прогрессе.
        /// </summary>
        public static readonly PlanOptions Default = new PlanOptions();

        /// <summary>
        /// Gets or sets a value indicating whether игнорировать кеш хешей и перечитать каждый файл с диска.
        /// Обычной синхронизации это не нужно, а вот проверке целостности — обязательно:
        /// кеш считает файл валидным по совпадению размера и времени модификации,
        /// поэтому повреждённый «на месте» файл он подтвердил бы как исправный.
        /// </summary>
        public bool ForceRehash { get; set; }

        /// <summary>
        /// Gets or sets отчёт о прогрессе сравнения (этап "Checking").
        /// Пересчёт хешей всей игры занимает минуты, без прогресса UI выглядит зависшим.
        /// </summary>
        public IProgress<SyncProgress>? Progress { get; set; }
    }
}
