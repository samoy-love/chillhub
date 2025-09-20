// <copyright file="ISyncService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public interface ISyncService {
        Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct);

        Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct);

        Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct);
    }
}
