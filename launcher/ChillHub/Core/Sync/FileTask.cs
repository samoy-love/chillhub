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
    }
}
