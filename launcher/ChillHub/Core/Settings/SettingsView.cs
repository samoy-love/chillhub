// <copyright file="SettingsView.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Settings {
    using System;
    using System.IO;

    using ChillHub.Core.Home;

    /// <summary>
    /// Что страница настроек показывает по текущей конфигурации. Собирается целиком,
    /// без обращения к контролам, — иначе проверить содержимое страницы можно только
    /// подняв окно, а окно в прогоне тестов не поднимается.
    /// </summary>
    internal sealed class SettingsDisplay {
        /// <summary>Путь к папке игр в читаемом виде.</summary>
        internal required string GamesPath { get; init; }

        /// <summary>Положение ползунка числа потоков.</summary>
        internal required int DownloadThreads { get; init; }

        /// <summary>Число потоков подписью рядом с ползунком.</summary>
        internal required string DownloadThreadsText { get; init; }

        /// <summary>Положение ползунка ограничения скорости, МБ/с (0 — без лимита).</summary>
        internal required int SpeedLimitMbps { get; init; }

        /// <summary>Подпись рядом с ползунком ограничения скорости.</summary>
        internal required string SpeedLimitText { get; init; }

        /// <summary>Отправлять обезличенную статистику.</summary>
        internal required bool SendUsageMetrics { get; init; }

        /// <summary>Отправлять отчёты об ошибках автоматически.</summary>
        internal required bool AutoErrorReports { get; init; }

        /// <summary>Сворачивать окно в трей вместо закрытия.</summary>
        internal required bool MinimizeToTray { get; init; }

        /// <summary>Версия лаунчера в подвале страницы.</summary>
        internal required string VersionText { get; init; }
    }

    /// <summary>
    /// Наполнение страницы настроек: перевод конфигурации в то, что видит пользователь.
    /// </summary>
    internal static class SettingsView {
        /// <summary>
        /// Собирает всё, что страница показывает при открытии.
        /// </summary>
        /// <param name="cfg">Текущая конфигурация; null — берутся значения по умолчанию.</param>
        /// <returns>Значения для полей страницы.</returns>
        internal static SettingsDisplay Build(AppConfig? cfg) {
            cfg ??= new AppConfig();

            // Отображаем путь с одинарными обратными слешами для читаемости.
            // Ведущий \\ сетевого пути при этом обязан уцелеть — см. NormalizeWindowsPath.
            var p = cfg.GamesPath ?? string.Empty;

            return new SettingsDisplay {
                GamesPath = HomeFormat.NormalizeWindowsPath(p),
                DownloadThreads = cfg.DownloadThreads,
                DownloadThreadsText = cfg.DownloadThreads.ToString(),
                SpeedLimitMbps = cfg.SpeedLimitMbps,
                SpeedLimitText = FormatSpeedLimit(cfg.SpeedLimitMbps),
                SendUsageMetrics = cfg.SendUsageMetrics,
                AutoErrorReports = cfg.AutoErrorReports,
                MinimizeToTray = cfg.MinimizeToTray,
                VersionText = GetLauncherVersion(),
            };
        }

        /// <summary>
        /// Подпись рядом с ползунком ограничения скорости: «без лимита» при 0, иначе «N МБ/с».
        /// </summary>
        /// <param name="mbps">Значение из конфига.</param>
        /// <returns>Текст подписи.</returns>
        internal static string FormatSpeedLimit(int mbps) => mbps <= 0 ? "без лимита" : $"{mbps} МБ/с";

        /// <summary>
        /// Версия лаунчера: сначала маркер launcher.version рядом с exe (его пишет апдейтер),
        /// иначе — версия сборки. Своя маленькая копия логики, чтобы не тянуть зависимость от UpdateWindow.
        /// </summary>
        /// <returns>Версия для показа пользователю.</returns>
        internal static string GetLauncherVersion() {
            try {
                var markerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.version");
                if (File.Exists(markerPath)) {
                    var marker = (File.ReadAllText(markerPath) ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(marker)) {
                        return marker;
                    }
                }
            }
            catch (Exception ex) {
                // Маркера может не быть или он недоступен — ниже возьмём версию сборки
                ChillHub.Core.Logging.Logger.Warn($"SettingsPage.GetLauncherVersion: маркер launcher.version не прочитан: {ex.Message}");
            }

            try {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) {
                    return $"{v.Major}.{v.Minor}.{v.Build}";
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"SettingsPage.GetLauncherVersion: версия сборки недоступна: {ex.Message}");
            }

            return "неизвестно";
        }
    }
}
