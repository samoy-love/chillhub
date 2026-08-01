// <copyright file="DiffPlan.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System.Collections.Generic;

    public class DiffPlan {
        public string GameId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string LocalRoot { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets каталог, куда файлы попадут В ИТОГЕ, если это не <see cref="LocalRoot"/>.
        /// <para>
        /// Для игр совпадает с <see cref="LocalRoot"/> и остаётся пустым. А самообновление
        /// качает во временную папку в %TEMP%, тогда как файлы применяются в каталог
        /// установки лаунчера — это может быть другой диск. Без этого поля проверка
        /// свободного места смотрела только на диск с %TEMP% и пропускала случай
        /// «в TEMP место есть, а на системном диске нет».
        /// </para>
        /// </summary>
        public string ApplyRoot { get; set; } = string.Empty;

        public long TotalDownloadBytes { get; set; }

        public int TotalFilesToDownload { get; set; }

        public List<FileTask> Downloads { get; set; } = new();

        public List<string> ToDelete { get; set; } = new();

        public List<string> EmptyDirsToCreate { get; set; } = new();
    }
}
