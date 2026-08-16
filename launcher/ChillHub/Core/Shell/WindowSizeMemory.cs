// <copyright file="WindowSizeMemory.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;

    /// <summary>
    /// Размер главного окна между запусками. По умолчанию окно открывается минимальным:
    /// прежние 1180×760 на ноутбуке с масштабом 125–150 % уходили за край экрана. Если
    /// пользователь окно растянул или развернул — при следующем запуске оно таким и будет.
    /// Про Window не знает: только числа из конфига и обратно.
    /// </summary>
    internal static class WindowSizeMemory {
        /// <summary>Что показать при старте.</summary>
        /// <param name="cfg">Конфиг.</param>
        /// <param name="minWidth">MinWidth окна.</param>
        /// <param name="minHeight">MinHeight окна.</param>
        /// <returns>Ширина, высота и признак «развернуть».</returns>
        internal static (double Width, double Height, bool Maximized) Restore(AppConfig? cfg, double minWidth, double minHeight) {
            var w = cfg?.WindowWidth ?? 0;
            var h = cfg?.WindowHeight ?? 0;

            // Меньше минимума или мусор (NaN, бесконечность) — как будто не запоминали
            var valid = !double.IsNaN(w) && !double.IsNaN(h) && !double.IsInfinity(w) && !double.IsInfinity(h)
                && w >= minWidth && h >= minHeight;
            return valid
                ? (w, h, cfg!.WindowMaximized)
                : (minWidth, minHeight, cfg?.WindowMaximized == true);
        }

        /// <summary>
        /// Запоминает размер, если он отличается от того, что уже в конфиге. Размер — из
        /// нормального состояния окна (RestoreBounds), а не с экрана: у развёрнутого окна
        /// ActualWidth равен ширине монитора, и после «восстановить» оно вернулось бы во
        /// весь экран без рамки.
        /// </summary>
        /// <param name="cfg">Конфиг.</param>
        /// <param name="width">Ширина окна в нормальном состоянии.</param>
        /// <param name="height">Высота окна в нормальном состоянии.</param>
        /// <param name="maximized">Окно развёрнуто.</param>
        /// <returns>True — конфиг изменился, его надо записать.</returns>
        internal static bool Remember(AppConfig cfg, double width, double height, bool maximized) {
            var changed = false;
            if (cfg.WindowMaximized != maximized) {
                cfg.WindowMaximized = maximized;
                changed = true;
            }

            if (width > 0 && height > 0 && !double.IsNaN(width) && !double.IsNaN(height)
                && (Math.Abs(cfg.WindowWidth - width) >= 1 || Math.Abs(cfg.WindowHeight - height) >= 1)) {
                cfg.WindowWidth = width;
                cfg.WindowHeight = height;
                changed = true;
            }

            return changed;
        }
    }
}
