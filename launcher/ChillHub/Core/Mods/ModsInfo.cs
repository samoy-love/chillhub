// <copyright file="ModsInfo.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System.Collections.Generic;

    /// <summary>
    /// Активный модпак игры — то, что сервер прислал в <c>/api/games</c>.
    /// <para>
    /// Выбирать здесь нечего: на игру приходится ровно один активный модпак, и какой
    /// именно — решает админка. Лаунчер получает всё нужное одним ответом и никогда
    /// не ходит на Thunderstore сам.
    /// </para>
    /// </summary>
    public class ModsInfo {
        /// <summary>Есть ли собранный и активированный модпак.</summary>
        public bool HasLatest { get; set; }

        /// <summary>
        /// Имя версии на сервере, вида <c>ASTeam-LethalReloaded-2.2.12</c>. Оно несёт
        /// в себе и пакет: две разные сборки спокойно публикуют «1.0.0», и без имени
        /// пакета отличить их в журнале невозможно.
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Читаемое имя модпака для карточки игры.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Номер версии модпака для карточки игры.</summary>
        public string DisplayVersion { get; set; } = string.Empty;

        /// <summary>
        /// Слаг сообщества Thunderstore («lethal-company»). Из него собирается ссылка
        /// на страницу модпака: по нашему идентификатору игры его не вывести —
        /// «risk-of-rain-2» там зовётся «riskofrain2», — а угаданная ссылка хуже, чем
        /// её отсутствие.
        /// </summary>
        public string Community { get; set; } = string.Empty;

        /// <summary>Адрес манифеста модпака.</summary>
        public string ManifestUrl { get; set; } = string.Empty;

        /// <summary>База для скачивания файлов модпака.</summary>
        public string ContentBaseUrl { get; set; } = string.Empty;

        /// <summary>Загрузчик модов, обычно <c>bepinex</c>.</summary>
        public string Loader { get; set; } = string.Empty;

        /// <summary>Идентификатор игры в Steam — по нему ищется копия игрока.</summary>
        public string SteamAppId { get; set; } = string.Empty;

        /// <summary>
        /// Имя папки под <c>steamapps/common</c>. Иногда вложенное: у How to Fish это
        /// «How to Fish/How to Fish» при installdir «How to Fish».
        /// </summary>
        public string SteamFolder { get; set; } = string.Empty;

        /// <summary>Имена исполняемых файлов игры, в порядке предпочтения.</summary>
        public List<string> ExeNames { get; set; } = new();

        /// <summary>Строка для карточки игры: «Lethal Reloaded 2.2.12».</summary>
        /// <returns>Читаемое описание установленного модпака.</returns>
        public string Describe() {
            if (!this.HasLatest) {
                return string.Empty;
            }

            var name = string.IsNullOrWhiteSpace(this.DisplayName) ? this.Version : this.DisplayName;
            return string.IsNullOrWhiteSpace(this.DisplayVersion) ? name : $"{name} {this.DisplayVersion}";
        }
    }
}
