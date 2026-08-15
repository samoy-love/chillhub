// <copyright file="RuDateConverter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Дата по-русски: «сегодня», «вчера» или «5 января 2026». Свежие записи — а это
    /// почти все новости в момент выхода — читаются короче и не пестрят одинаковыми
    /// «15 августа 2026» подряд.
    /// </summary>
    public class RuDateConverter : IValueConverter {
        private static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        /// <summary>Источник «сегодня»; подменяется в тестах, чтобы они не зависели от календаря.</summary>
        internal static Func<DateTime> Today { get; set; } = () => DateTime.Today;

        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is DateTime dt) {
                return Format(dt.Date);
            }

            if (value is DateTimeOffset dto) {
                // Use date component to avoid timezone-related day shifts
                return Format(dto.Date);
            }

            if (value is string s && !string.IsNullOrWhiteSpace(s)) {
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDto)) {
                    return Format(parsedDto.Date);
                }

                if (DateTime.TryParse(s, out var parsedDt)) {
                    return Format(parsedDt.Date);
                }

                // Fallback: return original string if parsing fails
                return s;
            }

            return string.Empty;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }

        private static string Format(DateTime date) {
            var today = Today().Date;
            if (date == today) {
                return "сегодня";
            }

            if (date == today.AddDays(-1)) {
                return "вчера";
            }

            return date.ToString("d MMMM yyyy", Ru);
        }
    }
}
