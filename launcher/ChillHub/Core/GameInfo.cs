// <copyright file="GameInfo.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System.ComponentModel;
    using System.Runtime.CompilerServices;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Игра в списке главного экрана.
    /// <para>
    /// СТРОКА СПИСКА ОБНОВЛЯЕТСЯ САМА. Раньше уведомление было ровно одно — про очередь,
    /// — а «установлена», «требует обновления» и название менялись молча, и после каждой
    /// такой правки список пересобирался целиком через <c>Items.Refresh()</c>. Пересборка
    /// заново создаёт все строки: значки перезагружаются, выделение и прокрутка
    /// дёргаются, а сам вызов приходил и на проверку статусов, и на выбор игры, и на
    /// каждую завершённую закачку. Отсюда и мерцание списка на ровном месте.
    /// </para>
    /// </summary>
    public class GameInfo : INotifyPropertyChanged {
        private string queueLabel = string.Empty;
        private string runLabel = string.Empty;
        private string title = string.Empty;
        private string iconUrl = string.Empty;
        private string installedVersion = string.Empty;
        private bool isInstalled;
        private bool needsUpdate;

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;

        public string GameId { get; set; } = string.Empty;

        /// <summary>Gets or sets название игры — первая строка карточки в списке.</summary>
        public string Title {
            get => this.title;
            set => this.SetField(ref this.title, value ?? string.Empty);
        }

        public bool HasLatest { get; set; }

        public string LatestVersion { get; set; } = string.Empty;

        public string ManifestUrl { get; set; } = string.Empty;

        public string ExeRelativePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets адрес значка. Пока он пуст, строка списка показывает скелет —
        /// то есть от уведомления зависит не подпись, а вид всей карточки.
        /// </summary>
        public string IconUrl {
            get => this.iconUrl;
            set => this.SetField(ref this.iconUrl, value ?? string.Empty);
        }

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

        /// <summary>Gets or sets a value indicating whether игра установлена на диске.</summary>
        public bool IsInstalled {
            get => this.isInstalled;
            set => this.SetField(ref this.isInstalled, value);
        }

        /// <summary>Gets or sets версию, которая сейчас лежит на диске.</summary>
        public string InstalledVersion {
            get => this.installedVersion;
            set => this.SetField(ref this.installedVersion, value ?? string.Empty);
        }

        /// <summary>Gets or sets a value indicating whether сборка на диске отличается от эталона.</summary>
        public bool NeedsUpdate {
            get => this.needsUpdate;
            set => this.SetField(ref this.needsUpdate, value);
        }

        /// <summary>
        /// Что происходит с игрой в очереди загрузок — «Скачивание · 38%», «В очереди»;
        /// пусто, если игры в очереди нет.
        /// <para>
        /// Появилось потому, что список игр не знал про очередь: качающаяся на 38% игра и
        /// игра, которая только ждёт, обе подписывались одинаковым янтарным «Обновление»,
        /// и понять, что вообще происходит, можно было только по нижней панели.
        /// </para>
        /// </summary>
        [JsonIgnore]
        public string QueueLabel {
            get => this.queueLabel;
            set => this.SetField(ref this.queueLabel, value ?? string.Empty);
        }

        /// <summary>
        /// Gets or sets подпись запущенной игры — «Играет», «Запускается…»; пусто, если
        /// игра не запущена.
        /// <para>
        /// Свернуть лаунчер на время партии — обычное дело, и, вернувшись, игрок видел
        /// список, ничем не отличающийся от вчерашнего: про открытую прямо сейчас игру в
        /// нём не было ни слова.
        /// </para>
        /// </summary>
        [JsonIgnore]
        public string RunLabel {
            get => this.runLabel;
            set => this.SetField(ref this.runLabel, value ?? string.Empty);
        }

        /// <inheritdoc/>
        public override string ToString() => this.Title;

        /// <summary>
        /// Меняет поле и сообщает об этом, если значение действительно другое.
        /// <para>
        /// Проверка на равенство здесь не экономия, а условие тишины: статусы
        /// переписываются пачками при каждой проверке файлов, и уведомление на
        /// «то же самое» перерисовывало бы список ровно так же часто, как раньше это
        /// делал <c>Items.Refresh()</c>.
        /// </para>
        /// </summary>
        /// <typeparam name="T">Тип поля.</typeparam>
        /// <param name="field">Само поле.</param>
        /// <param name="value">Новое значение.</param>
        /// <param name="name">Имя свойства; подставляется компилятором.</param>
        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) {
                return;
            }

            field = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
