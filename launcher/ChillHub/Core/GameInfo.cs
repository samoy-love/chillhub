// <copyright file="GameInfo.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core
{
    public class GameInfo
    {
        public string GameId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public bool HasLatest { get; set; }

        public string LatestVersion { get; set; } = string.Empty;

        public string ManifestUrl { get; set; } = string.Empty;

        public string ExeRelativePath { get; set; } = string.Empty;

        public string IconUrl { get; set; } = string.Empty;

        // UI state (client-side only)
        public bool IsInstalled { get; set; } = false;

        public string InstalledVersion { get; set; } = string.Empty;

        public bool NeedsUpdate { get; set; } = false;

        /// <inheritdoc/>
        public override string ToString() => this.Title;
    }
}
