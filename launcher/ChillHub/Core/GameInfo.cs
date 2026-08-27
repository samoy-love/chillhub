// <copyright file="GameInfo.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System.ComponentModel;
    using System.Text.Json.Serialization;

    public class GameInfo : INotifyPropertyChanged {
        private string queueLabel = string.Empty;

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;

        public string GameId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public bool HasLatest { get; set; }

        public string LatestVersion { get; set; } = string.Empty;

        public string ManifestUrl { get; set; } = string.Empty;

        public string ExeRelativePath { get; set; } = string.Empty;

        public string IconUrl { get; set; } = string.Empty;

        /// <summary>
        /// Активный модпак игры или null, если модов у неё нет.
        /// <para>
        /// Приходит вложенным объектом в том же ответе <c>/api/games</c>: лаунчер
        /// узнаёт про моды одним запросом и ничего не выбирает — активный модпак на
        /// игру ровно один, и назначается он в админке.
        /// </para>
        /// </summary>
        public ModsInfo? Mods { get; set; }

        // UI state (client-side only)
        public bool IsInstalled { get; set; } = false;

        public string InstalledVersion { get; set; } = string.Empty;

        public bool NeedsUpdate { get; set; } = false;

        /// <summary>
        /// Что происходит с игрой в очереди загрузок — «Скачивание · 38%», «В очереди»;
        /// пусто, если игры в очереди нет. Единственное свойство с уведомлением: строка
        /// списка обязана обновляться по ходу закачки без пересборки всего списка, а
        /// остальные поля меняются вместе с перечитыванием каталога.
        /// <para>
        /// Появилось потому, что список игр не знал про очередь: качающаяся на 38% игра и
        /// игра, которая только ждёт, обе подписывались одинаковым янтарным «Обновление»,
        /// и понять, что вообще происходит, можно было только по нижней панели.
        /// </para>
        /// </summary>
        [JsonIgnore]
        public string QueueLabel {
            get => this.queueLabel;
            set {
                var next = value ?? string.Empty;
                if (string.Equals(this.queueLabel, next, System.StringComparison.Ordinal)) {
                    return;
                }

                this.queueLabel = next;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.QueueLabel)));
            }
        }

        /// <inheritdoc/>
        public override string ToString() => this.Title;
    }
}
