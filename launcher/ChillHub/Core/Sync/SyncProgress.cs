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

        /// <summary>
        /// Сколько байт пришло ПО СЕТИ с начала операции.
        /// <para>
        /// СКОРОСТЬ СЧИТАЕТСЯ ПО НЕЙ, А НЕ ПО <see cref="BytesDownloaded"/>. Тот меряет
        /// сделанное: в него идут и файлы, взятые из соседней копии игры на диске, и
        /// уцелевший от прошлого запуска кусок .part. Копирование с диска быстрее сети
        /// в разы — и «скорость скачивания» показывала 100+ МБ/с на канале, где больше
        /// шестидесяти не бывает.
        /// </para>
        /// <para>
        /// Перезакачанные байты здесь остаются: по проводу они прошли, и для скорости
        /// это правда. Разница с <see cref="BytesDownloaded"/> — это ровно та работа,
        /// которую пришлось делать дважды.
        /// </para>
        /// </summary>
        public long NetworkBytes { get; set; }

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
