using System.Collections.Generic;

namespace ChillHub.Core
{
    // API DTOs
    public class GamesResponse { public List<GameInfo> Items { get; set; } = new(); }

    public class GameInfo
    {
        public string GameId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool HasLatest { get; set; }
        public string LatestVersion { get; set; } = string.Empty;
        public string ManifestUrl { get; set; } = string.Empty;
        public string ExeRelativePath { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty; // URL иконки игры (может быть относительным от API)
        // Локальное состояние для UI (заполняется на клиенте)
        public bool IsInstalled { get; set; } = false;
        public string InstalledVersion { get; set; } = string.Empty;
        public bool NeedsUpdate { get; set; } = false; // вычисляется на клиенте: установлено, но версия не latest
        public override string ToString() => Title;
    }

    public class BuildsResponse { public List<string> Items { get; set; } = new(); }

    public class NewsIndex { public List<NewsItem> Items { get; set; } = new(); }

    public class NewsItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
        public override string ToString() => Title;
    }
}
