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

        /// <summary>
        /// Gets or sets полный вес сборки по манифесту — сколько весила бы та же
        /// операция, если качать всё целиком.
        /// <para>
        /// Ради этого числа лаунчер и существует: <see cref="TotalDownloadBytes"/>
        /// сам по себе говорит «скачали 40 МБ», и только рядом с полным весом
        /// видно, что вместо 12 ГБ. Заполняется всегда, даже без подписки на
        /// прогресс, — иначе метрика зависела бы от того, открыт ли экран.
        /// </para>
        /// </summary>
        public long TotalManifestBytes { get; set; }

        /// <summary>Gets or sets число файлов в сборке целиком.</summary>
        public int TotalManifestFiles { get; set; }

        /// <summary>
        /// Gets or sets сколько файлов не сошлись по хешу с манифестом.
        /// <para>
        /// Отличается от «файл отсутствует» и от «размер не тот»: расхождение
        /// хеша при совпадающем размере — это либо порча на диске, либо сборка,
        /// собранная не из того, что лежит в манифесте. И то и другое стоит
        /// увидеть на графике раньше, чем об этом напишут в обратную связь.
        /// </para>
        /// </summary>
        public int HashMismatches { get; set; }

        public List<FileTask> Downloads { get; set; } = new();

        public List<string> ToDelete { get; set; } = new();

        public List<string> EmptyDirsToCreate { get; set; } = new();
    }
}
