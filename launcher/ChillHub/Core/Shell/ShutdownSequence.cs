// <copyright file="ShutdownSequence.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;

    using ChillHub.Core.Logging;

    /// <summary>
    /// Что лаунчер обязан сделать при выходе. Шаги защищены по отдельности: сбой одного
    /// не должен отменить другой.
    /// </summary>
    internal sealed class ShutdownSequence {
        /// <summary>Останавливает опрос режима технических работ (задача 25).</summary>
        internal Action StopMaintenancePoll { get; set; } = ChillHub.Core.Maintenance.MaintenanceService.Stop;

        /// <summary>Проходит шаги выхода по порядку.</summary>
        internal void Run() {
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
