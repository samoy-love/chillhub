using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChillHub.Core.Sync
{
    public class DiffPlan
    {
        public string GameId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string LocalRoot { get; set; } = string.Empty;
        public long TotalDownloadBytes { get; set; }
        public int TotalFilesToDownload { get; set; }
        public List<FileTask> Downloads { get; set; } = new();
        public List<string> ToDelete { get; set; } = new();
        public List<string> EmptyDirsToCreate { get; set; } = new();
    }

    public class FileTask
    {
        public string RelativePath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Blake3 { get; set; } = string.Empty;
        public string? Sha256 { get; set; }
        public bool Executable { get; set; }
    }

    public interface ISyncService
    {
        Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct);
        Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct);
        Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct);
    }

    public class SyncProgress
    {
        public int FilesDownloaded { get; set; }
        public int TotalFiles { get; set; }
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public string Stage { get; set; } = string.Empty; // Checking, Downloading, Verifying, Activating
    }
}
