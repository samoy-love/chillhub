// <copyright file="MaintenanceBannerView.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using ChillHub.Core.Maintenance;

    /// <summary>
    /// Решение оболочки о баннере технических работ: показывать ли его и с каким текстом.
    /// <para>
    /// Баннер обязан не только появляться, но и ИСЧЕЗАТЬ: сервер сообщает об окончании
    /// работ тем же опросом, и оставшийся висеть баннер уверял бы пользователя, что
    /// установка по-прежнему запрещена, когда всё уже работает.
    /// </para>
    /// <para>
    /// Причина и срок отданы порознь: баннер набирает причину основным начертанием, а
    /// срок — вторичным, как карточка очереди набирает название и цифры. Одной строкой
    /// одинакового веса «Меняем диск на сервере раздачи. Ожидаемое окончание — сегодня
    /// в 23:30.» читалась как абзац, а не как сообщение с главным и второстепенным.
    /// </para>
    /// </summary>
    internal readonly struct MaintenanceBannerView {
        private MaintenanceBannerView(bool visible, string reason, string eta) {
            this.Visible = visible;
            this.Reason = reason;
            this.Eta = eta;
        }

        /// <summary>Показывать ли баннер.</summary>
        internal bool Visible { get; }

        /// <summary>Причина работ — главная строка баннера; пусто, когда работ нет.</summary>
        internal string Reason { get; }

        /// <summary>Срок окончания — вторичная строка; пусто, если сервер срок не назвал или работ нет.</summary>
        internal string Eta { get; }

        /// <summary>Полный текст баннера одной строкой; пусто, когда работ нет.</summary>
        internal string Text => string.IsNullOrEmpty(this.Eta) ? this.Reason : $"{this.Reason} {this.Eta}";

        /// <summary>
        /// Строит вид баннера по состоянию с сервера.
        /// </summary>
        /// <param name="state">Состояние режима работ; null — сервер ничего не сказал.</param>
        /// <returns>Что показать в шапке.</returns>
        internal static MaintenanceBannerView For(MaintenanceState? state)
            => state is not { Enabled: true }
                ? new MaintenanceBannerView(false, string.Empty, string.Empty)
                : new MaintenanceBannerView(true, state.BuildReasonText(), state.BuildEtaText());
    }
}
