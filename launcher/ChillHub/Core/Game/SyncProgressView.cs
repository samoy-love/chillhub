// <copyright file="SyncProgressView.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;

    using ChillHub.Core.Sync;

    using static ChillHub.Core.Home.HomeFormat;

    /// <summary>
    /// Что страница должна показать по очередному отчёту о прогрессе.
    /// Null в поле означает «этого не касаемся»: разные стадии трогают разный набор строк.
    /// </summary>
    /// <param name="Status">Строка состояния.</param>
    /// <param name="Indeterminate">Режим прогресс-бара.</param>
    /// <param name="Value">Значение прогресс-бара, 0..100.</param>
    /// <param name="SpeedEta">Строка «Скорость … • Осталось …».</param>
    /// <param name="FilesSize">Строка «файлов • байт».</param>
    internal readonly record struct SyncProgressDisplay(
        string? Status,
        bool? Indeterminate,
        double? Value,
        string? SpeedEta,
        string? FilesSize);

    /// <summary>
    /// Переводит отчёты синхронизации в подписи страницы и держит сглаженную скорость.
    /// Скорость считается по EMA, иначе цифра прыгает на каждом отчёте.
    /// </summary>
    internal sealed class SyncProgressView {
        /// <summary>Чувствительность EMA для сглаживания скорости скачивания.</summary>
        private const double EmaAlpha = 0.2;

        private double emaSpeedMBs;

        /// <summary>Сбрасывает сглаживание перед новой операцией.</summary>
        internal void Reset() => this.emaSpeedMBs = 0.0;

        /// <summary>Считает, что показать по одному отчёту о прогрессе.</summary>
        /// <param name="p">Отчёт от службы синхронизации.</param>
        /// <param name="elapsedSeconds">Сколько секунд прошло с начала операции.</param>
        /// <returns>Подписи для страницы.</returns>
        internal SyncProgressDisplay Describe(SyncProgress p, double elapsedSeconds) {
            switch (p.Stage) {
                case "Checking":
                    return new SyncProgressDisplay("Проверка файлов…", true, null, null, null);
                case "Downloading":
                    if (p.TotalBytes > 0) {
                        var value = Math.Min(100, Math.Max(0, p.BytesDownloaded * 100.0 / p.TotalBytes));
                        var instant = elapsedSeconds > 0 ? (p.BytesDownloaded / 1024.0 / 1024.0) / elapsedSeconds : 0;
                        this.emaSpeedMBs = this.emaSpeedMBs <= 0 ? instant : ((EmaAlpha * instant) + ((1 - EmaAlpha) * this.emaSpeedMBs));
                        var remain = p.TotalBytes - p.BytesDownloaded;
                        var eta = this.emaSpeedMBs > 0 ? (remain / 1024.0 / 1024.0) / this.emaSpeedMBs : 0;
                        return new SyncProgressDisplay(
                            "Скачивание…",
                            false,
                            value,
                            $"Скорость: {this.emaSpeedMBs:0.0} МБ/с • Осталось: {FormatEta(eta)}",
                            $"{p.FilesDownloaded}/{p.TotalFiles} • {FormatSize(p.BytesDownloaded)}/{FormatSize(p.TotalBytes)}");
                    }

                    return new SyncProgressDisplay("Скачивание…", false, null, null, null);
                case "Verifying":
                    return new SyncProgressDisplay("Проверка скачанного…", true, 100, string.Empty, null);
                case "Activating":
                    return new SyncProgressDisplay("Применение…", true, 100, string.Empty, null);
                case "Completed":
                    return new SyncProgressDisplay("Готово", false, 100, string.Empty, null);
                default:
                    return new SyncProgressDisplay(p.Stage, null, null, null, null);
            }
        }
    }
}
