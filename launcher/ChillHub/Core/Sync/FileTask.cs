// <copyright file="FileTask.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync
{
    public class FileTask
    {
        public string RelativePath { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Url { get; set; } = string.Empty;

        public string Blake3 { get; set; } = string.Empty;

        public string? Sha256 { get; set; }

        public bool Executable { get; set; }
    }
}
