// <copyright file="NewsColumnsConverter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Число колонок ленты новостей по её ширине: до порога — одна, дальше — две.
    /// <para>
    /// Карточка новости — обложка 168px и три строки текста; на 2K лента шириной
    /// 1700px оставляла две трети каждой карточки пустыми, три пустых полосы одна над
    /// другой. Вторая колонка заполняет ширину той же плотностью, что и на ноутбуке.
    /// </para>
    /// </summary>
    public class NewsColumnsConverter : IValueConverter {
        /// <summary>Ширина ленты, с которой карточки идут в две колонки.</summary>
        public const double TwoColumnsFrom = 1200;

        /// <summary>Число колонок для ширины ленты.</summary>
        /// <param name="width">Ширина ленты в пикселях.</param>
        /// <returns>1 или 2.</returns>
        public static int ColumnsFor(double width)
            => !double.IsNaN(width) && width >= TwoColumnsFrom ? 2 : 1;

        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => ColumnsFor(value as double? ?? 0);

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
