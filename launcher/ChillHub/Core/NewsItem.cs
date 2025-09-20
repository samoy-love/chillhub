// <copyright file="NewsItem.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    public class NewsItem {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Slug { get; set; } = string.Empty;

        public string CreatedAt { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public string CoverUrl { get; set; } = string.Empty;

        /// <inheritdoc/>
        public override string ToString() => this.Title;
    }
}
