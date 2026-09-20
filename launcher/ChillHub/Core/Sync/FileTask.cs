// <copyright file="FileTask.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    public class FileTask {
        public string RelativePath { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Url { get; set; } = string.Empty;

        public string Blake3 { get; set; } = string.Empty;

        public string? Sha256 { get; set; }

        public bool Executable { get; set; }

        /// <summary>
        /// Gets or sets полный путь к такому же файлу, уже лежащему на диске в другой
        /// копии этой игры; пусто — качать из сети.
        /// <para>
        /// Копия проходит ту же сверку хешей, что и загрузка, и при расхождении файл
        /// скачивается обычным путём. То есть худшее, чем может обернуться неверный
        /// донор, — лишнее копирование, а не подменённый файл.
        /// </para>
        /// </summary>
        public string LocalSource { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets хеши блоков нового файла; null — собирать по блокам нечего,
        /// файл качается целиком.
        /// <para>
        /// По ним файл собирается из своей старой копии на диске и докачанных
        /// недостающих кусков. Собранное проходит ту же сверку полных хешей, что и
        /// скачанное целиком.
        /// </para>
        /// </summary>
        internal BlockList? Blocks { get; set; }
    }
}
