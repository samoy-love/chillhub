// <copyright file="ShutdownSequence.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;

    using ChillHub.Core.Logging;

    /// <summary>
    /// Что лаунчер обязан сделать при выходе. Оба шага независимы и оба защищены:
    /// сбой одного не должен отменить другой, иначе в Discord навсегда останется
    /// висеть статус «сейчас играет …» уже закрытого лаунчера.
    /// </summary>
    internal sealed class ShutdownSequence {
        /// <summary>
        /// Снимает статус в Discord. Метод сам ничего не делает, если интеграция
        /// не настроена или Discord не запущен.
        /// </summary>
        internal Action ShutdownDiscord { get; set; } = ChillHub.Core.DiscordRichPresence.Shutdown;

        /// <summary>Останавливает опрос режима технических работ (задача 25).</summary>
        internal Action StopMaintenancePoll { get; set; } = ChillHub.Core.Maintenance.MaintenanceService.Stop;

        /// <summary>Проходит шаги выхода по порядку.</summary>
        internal void Run() {
            try {
                this.ShutdownDiscord();
            }
            catch (Exception ex) {
                // Logger.Write гасит собственные ошибки, дополнительная защита не нужна
                Logger.Warn("Discord shutdown failed: " + ex.Message);
            }

            try {
                this.StopMaintenancePoll();
            }
            catch (Exception ex) {
                // Logger.Write гасит собственные ошибки, дополнительная защита не нужна
                Logger.Warn("Maintenance poll stop failed: " + ex.Message);
            }
        }
    }
}
