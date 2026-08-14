// <copyright file="GameStatusConverters.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows.Data;
    using System.Windows.Media;

    /// <summary>
    /// Статус игры словом: «Готова», «Обновление», «Не установлена» — вход: сам <see cref="GameInfo"/>.
    /// <para>
    /// Раньше статус в строке списка рисовался иконкой, а расшифровка иконок стояла отдельной
    /// легендой из трёх строк, прибитой к низу сайдбара навсегда. Слово читается сразу и
    /// освобождает легенде место.
    /// </para>
    /// </summary>
    public class GameStatusTextConverter : IValueConverter {
        /// <summary>Подпись статуса для игры.</summary>
        /// <param name="game">Игра, для которой нужен статус.</param>
        /// <returns>Готовая подпись.</returns>
        public static string TextFor(GameInfo? game) {
            if (game is null) {
                return string.Empty;
            }

            if (game.NeedsUpdate) {
                return "Обновление";
            }

            return game.IsInstalled ? "Готова" : "Не установлена";
        }

        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => TextFor(value as GameInfo);

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Цвет подписи статуса: зелёный — готова, янтарный — нужно обновление, приглушённый —
    /// не установлена. Вход: сам <see cref="GameInfo"/>.
    /// </summary>
    public class GameStatusBrushConverter : IValueConverter {
        private static readonly SolidColorBrush Ready = Freeze("#57C98A");
        private static readonly SolidColorBrush Update = Freeze("#E0A64B");
        private static readonly SolidColorBrush Absent = Freeze("#6E6E80");

        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not GameInfo game) {
                return Absent;
            }

            if (game.NeedsUpdate) {
                return Update;
            }

            return game.IsInstalled ? Ready : Absent;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        private static SolidColorBrush Freeze(string hex) {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
