// <copyright file="DiffPlan.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System.Collections.Generic;

    public class DiffPlan {
        public string GameId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string LocalRoot { get; set; } = string.Empty;

        public long TotalDownloadBytes { get; set; }

        public int TotalFilesToDownload { get; set; }

        public List<FileTask> Downloads { get; set; } = new();

        public List<string> ToDelete { get; set; } = new();

        public List<string> EmptyDirsToCreate { get; set; } = new();
    }
}
