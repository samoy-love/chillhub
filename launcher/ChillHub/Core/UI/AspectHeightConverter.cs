// <copyright file="AspectHeightConverter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Высота из ширины по заданной пропорции: вход — ширина в пикселях, параметр —
    /// отношение высоты к ширине.
    /// <para>
    /// Нужна витрине на главной. WPF не умеет задавать соотношение сторон, и витрина
    /// тянулась по ширине окна при почти постоянной высоте: от 5:1 до 13:1 в зависимости
    /// от того, как пользователь растянул окно. Обложка при этом обрезалась по-разному
    /// каждый раз, и предсказуемого размера картинки не существовало в принципе —
    /// художнику нечего было назвать.
    /// </para>
    /// <para>
    /// С фиксированной пропорцией размер объявляется один раз и совпадает со стандартной
    /// Steam Library Hero (1920×620): её же отдаёт SteamGridBD в разделе Heroes.
    /// </para>
    /// </summary>
    public class AspectHeightConverter : IValueConverter {
        /// <summary>Отношение высоты к ширине у Steam Library Hero — 620 / 1920.</summary>
        public const double SteamHeroRatio = 620.0 / 1920.0;

        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var width = value as double? ?? 0;

            // Ширины ещё нет (первый проход разметки) или она невменяемая: возвращаем
            // Auto вместо нуля, иначе витрина схлопнется в полоску и мигнёт при старте.
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) {
                return double.NaN;
            }

            return width * Ratio(parameter);
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        /// <summary>Пропорция из параметра разметки; без него — пропорция Steam.</summary>
        private static double Ratio(object parameter) {
            if (parameter is double d && d > 0) {
                return d;
            }

            if (parameter is string s
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0) {
                return parsed;
            }

            return SteamHeroRatio;
        }
    }
}
