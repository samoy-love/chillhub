// <copyright file="MaintenanceState.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Maintenance {
    using System;
    using System.Text.Json.Serialization;

    /// <summary>Флаги блокировок из ответа сервера (вложенный объект <c>blocks</c>).</summary>
    public sealed class MaintenanceBlocks {
        /// <summary>Gets or sets a value indicating whether установка запрещена.</summary>
        [JsonPropertyName("install")]
        public bool? Install { get; set; }

        /// <summary>Gets or sets a value indicating whether обновление запрещено.</summary>
        [JsonPropertyName("update")]
        public bool? Update { get; set; }

        /// <summary>Gets or sets a value indicating whether запуск игры запрещён.</summary>
        [JsonPropertyName("launch")]
        public bool? Launch { get; set; }
    }

    /// <summary>
    /// Состояние режима технических работ, как его отдаёт сервер.
    /// <para>
    /// Контракт сверен с серверной реализацией (<c>server/internal/maintenance</c>).
    /// Ответ <c>GET {ApiBaseUrl}/api/maintenance</c> (см. <see cref="MaintenanceService.EndpointPath"/>):
    /// </para>
    /// <code>
    /// {
    ///   "enabled": true,
    ///   "reason": "Меняем диск на сервере раздачи",
    ///   "startsAt": "2026-08-01T10:00:00Z",
    ///   "endsAt": "2026-08-01T12:00:00Z",
    ///   "blocks": { "install": true, "update": true, "launch": false },
    ///   "serverTime": "2026-08-01T10:31:02Z"
    /// }
    /// </code>
    /// <para>
    /// Сервер всегда отвечает 200 и всегда присылает <c>blocks</c>; 404 не бывает — отсутствие
    /// файла состояния это <c>enabled:false</c>. Окно работ сервер считает сам: вне окна
    /// приходит <c>enabled:false</c> со всеми флагами <c>false</c>, поэтому клиенту не нужен
    /// собственный таймер — следующий опрос сам снимет баннер.
    /// </para>
    /// <para>
    /// Флаги всё равно объявлены как <see cref="bool"/>? — на случай ответа от более старого
    /// или частично реализованного сервера: надо отличать «явно разрешил» от «поля не было».
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

        /// <summary>Gets or sets время начала работ. Информационное поле.</summary>
        [JsonPropertyName("startsAt")]
        public DateTimeOffset? StartsAt { get; set; }

        /// <summary>Gets or sets ожидаемое время окончания работ. Null — сервер не назвал срок.</summary>
        [JsonPropertyName("endsAt")]
        public DateTimeOffset? EndsAt { get; set; }

        /// <summary>Gets or sets флаги блокировок.</summary>
        [JsonPropertyName("blocks")]
        public MaintenanceBlocks? Blocks { get; set; }

        /// <summary>
        /// Gets or sets время на сервере в момент ответа. Нужно, чтобы считать обратный отсчёт
        /// по серверным часам: у пользователя они бывают сбиты на часы, и тогда «осталось 20 минут»
        /// превращается во «время вышло» на ровном месте.
        /// </summary>
        [JsonPropertyName("serverTime")]
        public DateTimeOffset? ServerTime { get; set; }

        /// <summary>
        /// Gets a value indicating whether установка запрещена.
        /// Умолчание при отсутствии поля — запрещено: работы обычно означают, что раздача
        /// контента недоступна, и качать всё равно нечего.
        /// </summary>
        [JsonIgnore]
        public bool BlocksInstall => this.Enabled && (this.Blocks?.Install ?? true);

        /// <summary>Gets a value indicating whether обновление запрещено. Умолчание — запрещено, причина та же.</summary>
        [JsonIgnore]
        public bool BlocksUpdate => this.Enabled && (this.Blocks?.Update ?? true);

        /// <summary>
        /// Gets a value indicating whether запуск игры запрещён.
        /// Умолчание при отсутствии поля — разрешено: игра уже лежит на диске и обычно
        /// работает без сервера, а отбирать её без явного указания сервера незачем.
        /// </summary>
        [JsonIgnore]
        public bool BlocksPlay => this.Enabled && (this.Blocks?.Launch ?? false);

        /// <summary>Текст баннера: причина плюс ожидаемое время окончания.</summary>
        /// <returns>Готовая строка для показа в шапке.</returns>
        public string BuildBannerText() {
            var reason = this.BuildReasonText();
            var eta = this.BuildEtaText();
            return string.IsNullOrEmpty(eta) ? reason : $"{reason} {eta}";
        }

        /// <summary>
        /// Причина работ как законченное предложение. Администратор пишет её в админке
        /// без точки («Меняем диск на сервере раздачи»), а следом баннер приписывает срок —
        /// без знака на стыке два предложения склеивались в одно: «…раздачи Ожидаемое окончание…».
        /// </summary>
        /// <returns>Причина с завершающим знаком препинания; общий текст, если причины нет.</returns>
        public string BuildReasonText() {
            if (string.IsNullOrWhiteSpace(this.Reason)) {
                return "На сервере идут технические работы.";
            }

            var reason = this.Reason.Trim();
            return reason[^1] is '.' or '!' or '?' or '…' ? reason : reason + ".";
        }

        /// <summary>Человекочитаемое «до …» по <see cref="EndsAt"/>. Пусто, если срок не назван или уже прошёл.</summary>
        /// <returns>Текст об ожидаемом окончании работ.</returns>
        public string BuildEtaText() {
            if (this.EndsAt is not DateTimeOffset until) {
                return string.Empty;
            }

            try {
                var local = until.ToLocalTime();

                // Остаток считаем по часам СЕРВЕРА: у пользователя они бывают сбиты,
                // и тогда корректный срок выглядел бы истёкшим (или наоборот).
                var now = this.ServerTime ?? DateTimeOffset.Now;
                var left = until - now;
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
                Logging.Logger.Warn($"MaintenanceState.BuildEtaText({this.EndsAt}): {ex.Message}");
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
                && Nullable.Equals(this.EndsAt, other.EndsAt)
                && this.BlocksInstall == other.BlocksInstall
                && this.BlocksUpdate == other.BlocksUpdate
                && this.BlocksPlay == other.BlocksPlay;
        }
    }
}
