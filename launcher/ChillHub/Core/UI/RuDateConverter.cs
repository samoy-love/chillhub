// <copyright file="RuDateConverter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI
{
    using System;
    using System.Globalization;
    using System.Windows.Data;

    public class RuDateConverter : IValueConverter
    {
        private static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dt)
            {
                return dt.ToString("d MMMM yyyy", Ru);
            }

            if (value is DateTimeOffset dto)
            {
                // Use date component to avoid timezone-related day shifts
                return dto.Date.ToString("d MMMM yyyy", Ru);
            }

            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDto))
                {
                    return parsedDto.Date.ToString("d MMMM yyyy", Ru);
                }

                if (DateTime.TryParse(s, out var parsedDt))
                {
                    return parsedDt.Date.ToString("d MMMM yyyy", Ru);
                }

                // Fallback: return original string if parsing fails
                return s;
            }

            return string.Empty;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
