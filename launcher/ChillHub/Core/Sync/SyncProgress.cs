// <copyright file="SyncProgress.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    public class SyncProgress {
        public int FilesDownloaded { get; set; }

        public int TotalFiles { get; set; }

        public long BytesDownloaded { get; set; }

        public long TotalBytes { get; set; }

        public string Stage { get; set; } = string.Empty; // Checking, Downloading, Verifying, Activating

        /// <summary>
        /// Что именно синхронизируется: пусто — сама игра, «Моды» — модпак.
        /// <para>
        /// Проход по файлам один и тот же, а вот «Скачивание… 1.8 ГБ» без пометки
        /// читается как «качается игра» — и это ровно то, что видел игрок, пока
        /// установка модов шла молча.
        /// </para>
        /// </summary>
        public string Scope { get; set; } = string.Empty;
    }
}
