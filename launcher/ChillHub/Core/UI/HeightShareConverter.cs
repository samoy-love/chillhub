// <copyright file="HeightShareConverter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Доля высоты окна как предел для блока: вход — высота страницы, параметр — доля
    /// (0.34 — «не больше трети»). Пока высоты нет — предела тоже нет.
    /// <para>
    /// Нужна главному экрану: витрина и очередь загрузок ограничивались числами (340 и
    /// 240 пикселей), подобранными под один монитор. На невысоком окне те же числа
    /// съедали половину высоты вдвоём, и ленте новостей не оставалось ничего — под
    /// вкладками была пустота. Доля от окна растёт и сжимается вместе с ним.
    /// </para>
    /// </summary>
    public class HeightShareConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var height = value as double? ?? 0;
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) {
                // Первый проход разметки: MaxHeight=NaN недопустим, «без предела» — да.
                return double.PositiveInfinity;
            }

            return height * Share(parameter);
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        private static double Share(object parameter) {
            if (parameter is double d && d > 0) {
                return d;
            }

            if (parameter is string s
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0) {
                return parsed;
            }

            return 1.0;
        }
    }
}
