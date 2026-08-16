// <copyright file="KaraokeConfig.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;

    /// <summary>
    /// Централизованная конфигурация караоке — все параметры поведения в одном месте.
    /// </summary>
    internal sealed class KaraokeConfig {
        // Интервал печати одного символа (мс): меньше -> быстрее
        internal int CharIntervalMs { get; init; } = 60;

        // Пауза после завершения строки перед переходом (мс): строку нужно успеть
        // дочитать, 380 мс на это не хватало
        internal int PauseAfterLineMs { get; init; } = 700;

        // Длительность угасания дописанной строки (мс)
        internal int FadeOutMs { get; init; } = 180;

        // Ограничение на макс. число символов, добавляемых за один тик (чтобы не "перескакивало" строку)
        internal int MaxAdvanceCharsPerTick { get; init; } = 1;

        // Интервал тиков таймера (мс) — немного чаще печати, чтобы не пропускать символы
        internal int TimerTickMs => Math.Max(10, this.CharIntervalMs / 2);
    }
}
