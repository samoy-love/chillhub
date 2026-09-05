// <copyright file="GameStatusConverters.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Globalization;
    using System.Windows;
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
        // Краски берутся из темы, а не выписываются здесь. Выписанные однажды уже
        // разошлись с палитрой молча: разметку перекрасили, а список игр остался
        // красить статусы старыми цветами — включая отменённый фиолетовый у очереди.
        // Запасные значения — на случай, когда темы ещё нет (тесты конвертеров).
        private static SolidColorBrush Ready => Themed("Brush.Success", "#7DAB71");

        private static SolidColorBrush Update => Themed("Brush.Warning", "#BF9439");

        private static SolidColorBrush Absent => Themed("Brush.TextMuted", "#8A949D");

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
        internal static SolidColorBrush Queued => Themed("Brush.Accent", "#E5825B");

        /// <summary>Обрыв закачки — тем же цветом, что и остальные беды.</summary>
        internal static SolidColorBrush Interrupted => Themed("Brush.Danger", "#D47A70");

        /// <summary>
        /// Игра открыта прямо сейчас. Тот же зелёный, что у готовой к запуску: это
        /// её же состояние, доведённое до конца, — и лишний цвет в списке из трёх
        /// подписей делит внимание, а не направляет его.
        /// </summary>
        internal static SolidColorBrush Playing => Themed("Brush.Success", "#7DAB71");

        /// <summary>Кисть из темы по ключу; запасной цвет — когда темы в процессе нет.</summary>
        private static SolidColorBrush Themed(string key, string fallback) {
            try {
                if (Application.Current?.Resources[key] is SolidColorBrush brush) {
                    return brush;
                }
            }
            catch (Exception) {
                // Тема ещё не подключена — рисуем запасным.
            }

            return Freeze(fallback);
        }

        private static SolidColorBrush Freeze(string hex) {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Подпись строки списка с учётом очереди и запущенной игры: входы — <see cref="GameInfo"/>,
    /// его <see cref="GameInfo.QueueLabel"/> и <see cref="GameInfo.RunLabel"/>. Метка очереди
    /// важнее статуса на диске: пока игра качается, «Обновление» рядом с ней — вчерашняя
    /// новость. «Играет» — важнее статуса, но не важнее процентов закачки.
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
            if (!string.IsNullOrEmpty(label)) {
                return label!;
            }

            // «Играет» — после очереди, но раньше статуса на диске: у качающейся игры
            // важнее проценты, а «Установлена» под открытой игрой — вчерашняя новость.
            var run = values.Length > 2 ? values[2] as string : null;
            return !string.IsNullOrEmpty(run) ? run! : GameStatusTextConverter.TextFor(game);
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
                // Обрыв — не «идёт работа», и красить его акцентом очереди нельзя:
                // строка выглядела бы как ещё одна качающаяся игра.
                return string.Equals(label, QueueRowLabel.Interrupted, StringComparison.Ordinal)
                    ? GameStatusBrushConverter.Interrupted
                    : GameStatusBrushConverter.Queued;
            }

            var run = values.Length > 2 ? values[2] as string : null;
            if (!string.IsNullOrEmpty(run)) {
                return GameStatusBrushConverter.Playing;
            }

            return ByStatus.Convert(values.Length > 0 ? values[0] : null!, targetType, parameter, culture);
        }

        /// <inheritdoc/>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
