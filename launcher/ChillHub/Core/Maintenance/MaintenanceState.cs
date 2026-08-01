// <copyright file="MaintenanceState.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Maintenance {
    using System;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Состояние режима технических работ, как его отдаёт сервер.
    /// <para>
    /// <b>Контракт предположительный</b> — серверная часть задачи 25 делается параллельно.
    /// Ожидаемый ответ <c>GET {ApiBaseUrl}/api/maintenance</c> (см. <see cref="MaintenanceService.EndpointPath"/>):
    /// </para>
    /// <code>
    /// {
    ///   "enabled": true,
    ///   "reason": "Переезд раздачи на новый сервер",
    ///   "until": "2026-08-01T18:30:00Z",
    ///   "blockInstall": true,
    ///   "blockUpdate": true,
    ///   "blockPlay": false
    /// }
    /// </code>
    /// <para>
    /// Все поля кроме <c>enabled</c> необязательны. Флаги блокировок — <see cref="bool"/>?
    /// намеренно: надо отличать «сервер явно разрешил» от «сервер поле не прислал».
    /// Значения по умолчанию см. в <see cref="BlocksInstall"/> и соседях.
    /// </para>
    /// </summary>
    public sealed class MaintenanceState {
        /// <summary>Gets состояние «работы не идут»: то же самое используем при недоступном сервере.</summary>
        public static MaintenanceState Off { get; } = new MaintenanceState();

        /// <summary>Gets or sets a value indicating whether режим технических работ включён.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>Gets or sets причину работ для показа пользователю. Пусто — покажем общий текст.</summary>
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        /// <summary>Gets or sets ожидаемое время окончания работ. Null — сервер не назвал срок.</summary>
        [JsonPropertyName("until")]
        public DateTimeOffset? Until { get; set; }

        /// <summary>Gets or sets флаг запрета установки. Null — поля не было в ответе.</summary>
        [JsonPropertyName("blockInstall")]
        public bool? BlockInstall { get; set; }

        /// <summary>Gets or sets флаг запрета обновления. Null — поля не было в ответе.</summary>
        [JsonPropertyName("blockUpdate")]
        public bool? BlockUpdate { get; set; }

        /// <summary>Gets or sets флаг запрета запуска игры. Null — поля не было в ответе.</summary>
        [JsonPropertyName("blockPlay")]
        public bool? BlockPlay { get; set; }

        /// <summary>
        /// Gets a value indicating whether установка запрещена.
        /// Умолчание при отсутствии поля — запрещено: работы обычно означают, что раздача
        /// контента недоступна, и качать всё равно нечего.
        /// </summary>
        [JsonIgnore]
        public bool BlocksInstall => this.Enabled && (this.BlockInstall ?? true);

        /// <summary>Gets a value indicating whether обновление запрещено. Умолчание — запрещено, причина та же.</summary>
        [JsonIgnore]
        public bool BlocksUpdate => this.Enabled && (this.BlockUpdate ?? true);

        /// <summary>
        /// Gets a value indicating whether запуск игры запрещён.
        /// Умолчание при отсутствии поля — разрешено: игра уже лежит на диске и обычно
        /// работает без сервера, а отбирать её без явного указания сервера незачем.
        /// </summary>
        [JsonIgnore]
        public bool BlocksPlay => this.Enabled && (this.BlockPlay ?? false);

        /// <summary>Текст баннера: причина плюс ожидаемое время окончания.</summary>
        /// <returns>Готовая строка для показа в шапке.</returns>
        public string BuildBannerText() {
            var reason = string.IsNullOrWhiteSpace(this.Reason)
                ? "На сервере идут технические работы."
                : this.Reason.Trim();

            var eta = this.BuildEtaText();
            return string.IsNullOrEmpty(eta) ? reason : $"{reason} {eta}";
        }

        /// <summary>Человекочитаемое «до …» по <see cref="Until"/>. Пусто, если срок не назван или уже прошёл.</summary>
        /// <returns>Текст об ожидаемом окончании работ.</returns>
        public string BuildEtaText() {
            if (this.Until is not DateTimeOffset until) {
                return string.Empty;
            }

            try {
                var local = until.ToLocalTime();
                var left = local - DateTimeOffset.Now;
                if (left <= TimeSpan.Zero) {
                    // Срок вышел, а сервер всё ещё сообщает о работах — обещать время не будем
                    return "Работы затянулись, ждём сообщения от сервера.";
                }

                var when = local.Date == DateTimeOffset.Now.Date
                    ? $"сегодня в {local:HH:mm}"
                    : $"{local:dd.MM} в {local:HH:mm}";
                return $"Ожидаемое окончание — {when}.";
            }
            catch (Exception ex) {
                // Кривая дата от сервера не должна ломать баннер: покажем только причину
                Logging.Logger.Warn($"MaintenanceState.BuildEtaText('{this.Until}'): {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Сравнение по смыслу: нужно, чтобы не дёргать UI и лог на каждом опросе,
        /// когда ответ сервера не изменился.
        /// </summary>
        /// <param name="other">С чем сравниваем.</param>
        /// <returns>True, если состояния эквивалентны.</returns>
        public bool SameAs(MaintenanceState? other) {
            if (other == null) {
                return false;
            }

            return this.Enabled == other.Enabled
                && string.Equals(this.Reason ?? string.Empty, other.Reason ?? string.Empty, StringComparison.Ordinal)
                && Nullable.Equals(this.Until, other.Until)
                && this.BlocksInstall == other.BlocksInstall
                && this.BlocksUpdate == other.BlocksUpdate
                && this.BlocksPlay == other.BlocksPlay;
        }
    }
}
