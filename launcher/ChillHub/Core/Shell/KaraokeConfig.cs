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

        // Пауза после завершения строки перед переходом (мс)
        internal int PauseAfterLineMs { get; init; } = 380;

        // Длительность затухания текущей строки (мс)
        internal int FadeOutMs { get; init; } = 50;

        // Длительность появления следующей строки (мс)
        internal int FadeInMs { get; init; } = 70;

        // Доп. задержка после анимации (мс)
        internal int AfterTransitionDelayMs { get; init; } = 0;

        // Ограничение на макс. число символов, добавляемых за один тик (чтобы не "перескакивало" строку)
        internal int MaxAdvanceCharsPerTick { get; init; } = 1;

        // Интервал тиков таймера (мс) — немного чаще печати, чтобы не пропускать символы
        internal int TimerTickMs => Math.Max(10, this.CharIntervalMs / 2);
    }
}
