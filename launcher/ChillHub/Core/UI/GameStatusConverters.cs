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
    /// Статус игры словом: «Установлена», «Обновление», «Не установлена» — вход: сам <see cref="GameInfo"/>.
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

            return game.IsInstalled ? "Установлена" : "Не установлена";
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
        private static readonly SolidColorBrush Absent = Freeze("#80809A");

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

        /// <summary>Акцент — игра прямо сейчас в очереди загрузок.</summary>
        internal static SolidColorBrush Queued { get; } = Freeze("#7C5CFF");

        private static SolidColorBrush Freeze(string hex) {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Подпись строки списка с учётом очереди: входы — <see cref="GameInfo"/> и его
    /// <see cref="GameInfo.QueueLabel"/>. Метка очереди важнее статуса на диске: пока игра
    /// качается, «Обновление» рядом с ней — вчерашняя новость.
    /// <para>
    /// MultiBinding, а не обычный конвертер по объекту: привязка к объекту целиком не
    /// узнаёт об изменении его свойства, а перерисовывать весь список на каждый тик
    /// прогресса — дорого и мигает.
    /// </para>
    /// </summary>
    public class GameRowStatusTextConverter : IMultiValueConverter {
        /// <inheritdoc/>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            var game = values.Length > 0 ? values[0] as GameInfo : null;
            var label = values.Length > 1 ? values[1] as string : null;
            return !string.IsNullOrEmpty(label) ? label! : GameStatusTextConverter.TextFor(game);
        }

        /// <inheritdoc/>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Цвет подписи строки списка: акцент, пока игра в очереди, иначе — по статусу на диске.</summary>
    public class GameRowStatusBrushConverter : IMultiValueConverter {
        private static readonly GameStatusBrushConverter ByStatus = new();

        /// <inheritdoc/>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            var label = values.Length > 1 ? values[1] as string : null;
            if (!string.IsNullOrEmpty(label)) {
                return GameStatusBrushConverter.Queued;
            }

            return ByStatus.Convert(values.Length > 0 ? values[0] : null!, targetType, parameter, culture);
        }

        /// <inheritdoc/>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
