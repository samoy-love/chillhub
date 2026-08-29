// <copyright file="ManifestModels.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json.Serialization;

    public class Manifest {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("buildId")]
        public string BuildId { get; set; } = string.Empty;

        [JsonPropertyName("gameId")]
        public string GameId { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public List<ManifestFile> Files { get; set; } = new();

        [JsonPropertyName("emptyDirs")]
        public List<string> EmptyDirs { get; set; } = new();

        // Поля "signature" здесь больше нет: подпись манифестов из проекта убрана.
        // В манифестах, выпущенных раньше, оно ещё встречается — десериализатор
        // молча игнорирует неизвестные члены, поэтому старые файлы читаются как есть.
    }

    public class ManifestFile {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("blake3")]
        public string Blake3 { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("executable")]
        public bool Executable { get; set; }
    }

    /// <summary>
    /// Проверить скачанный файл нечем: манифест дал только Blake3, а посчитать его на
    /// этой машине не на чем (нет сборки рядом с лаунчером).
    /// <para>
    /// Отдельный тип, а не просто <see cref="IOException"/>: загрузчик по нему отличает
    /// «не починится повтором» от обычного сбоя сети. Без этого отличия пропавшая
    /// сборка стоила игроку трёх попыток на каждый файл модпака.
    /// </para>
    /// </summary>
    internal sealed class VerificationUnavailableException : IOException {
        /// <summary>Initializes a new instance of the <see cref="VerificationUnavailableException"/> class.</summary>
        /// <param name="message">Что именно проверить не удалось.</param>
        internal VerificationUnavailableException(string message)
            : base(message) {
        }
    }
}
