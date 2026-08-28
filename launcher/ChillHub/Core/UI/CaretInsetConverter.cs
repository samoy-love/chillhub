// <copyright file="CaretInsetConverter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;

    /// <summary>
    /// Padding поля плюс собственный отступ каретки — куда на самом деле встанет первый
    /// символ.
    /// <para>
    /// WPF рисует текст поля не по краю Padding: внутри него живёт <c>TextBoxView</c> со
    /// своим отступом в 2px с каждой стороны — место под каретку, чтобы она не срезалась
    /// о рамку. Подсказка, поставленная ровно по Padding, оказывается левее набора на эти
    /// два пикселя. Здесь они прибавляются, и подсказка встаёт на место первого символа.
    /// </para>
    /// </summary>
    public class CaretInsetConverter : IValueConverter {
        /// <summary>Отступ TextBoxView внутри области содержимого, по 2px с боков.</summary>
        public const double CaretInset = 2;

        /// <summary>Прибавляет отступ каретки к отступам поля.</summary>
        /// <param name="padding">Padding поля.</param>
        /// <returns>Отступы для подсказки.</returns>
        public static Thickness WithCaretInset(Thickness padding)
            => new(padding.Left + CaretInset, padding.Top, padding.Right + CaretInset, padding.Bottom);

        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => WithCaretInset(value is Thickness t ? t : default);

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
