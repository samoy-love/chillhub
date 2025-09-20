// <copyright file="SyncProgress.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync
{
    public class SyncProgress
    {
        public int FilesDownloaded { get; set; }

        public int TotalFiles { get; set; }

        public long BytesDownloaded { get; set; }

        public long TotalBytes { get; set; }

        public string Stage { get; set; } = string.Empty; // Checking, Downloading, Verifying, Activating
    }
}
