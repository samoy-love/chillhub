// <copyright file="PillCornerConverter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;

    /// <summary>
    /// Скругление пилюли: ровно половина меньшей стороны. Вход — ActualWidth и
    /// ActualHeight самого элемента (стиль <c>Border.Pill</c>).
    /// <para>
    /// Число «с запасом» (CornerRadius="999") пилюлю не даёт. WPF не обрезает такой
    /// радиус до половины высоты, а ужимает его пропорционально по каждой стороне
    /// отдельно: у бейджа 80×20 скругление выходит 40 по горизонтали и 10 по
    /// вертикали, и вместо пилюли рисуется эллипс. Подобрать одно число тоже нельзя:
    /// высота бейджа зависит от шрифта и масштаба экрана.
    /// </para>
    /// </summary>
    public class PillCornerConverter : IMultiValueConverter {
        /// <summary>Скругление пилюли для прямоугольника со сторонами width и height.</summary>
        /// <param name="width">Ширина элемента.</param>
        /// <param name="height">Высота элемента.</param>
        /// <returns>Половина меньшей стороны; ноль, пока размеров нет.</returns>
        public static CornerRadius For(double width, double height) {
            var side = Math.Min(Valid(width), Valid(height));
            return new CornerRadius(side / 2);
        }

        /// <inheritdoc/>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            var width = values.Length > 0 && values[0] is double w ? w : 0;
            var height = values.Length > 1 && values[1] is double h ? h : 0;
            return For(width, height);
        }

        /// <inheritdoc/>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        // Первый проход разметки отдаёт нули и NaN: скругления тогда нет, а не исключение.
        private static double Valid(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? 0 : v;
    }
}
