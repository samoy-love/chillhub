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
    /// </summary>
    internal readonly struct MaintenanceBannerView {
        private MaintenanceBannerView(bool visible, string text) {
            this.Visible = visible;
            this.Text = text;
        }

        /// <summary>Показывать ли баннер.</summary>
        internal bool Visible { get; }

        /// <summary>Текст баннера; пусто, когда работ нет.</summary>
        internal string Text { get; }

        /// <summary>
        /// Строит вид баннера по состоянию с сервера.
        /// </summary>
        /// <param name="state">Состояние режима работ; null — сервер ничего не сказал.</param>
        /// <returns>Что показать в шапке.</returns>
        internal static MaintenanceBannerView For(MaintenanceState? state)
            => state is not { Enabled: true }
                ? new MaintenanceBannerView(false, string.Empty)
                : new MaintenanceBannerView(true, state.BuildBannerText());
    }
}
