// <copyright file="QueueItemConverters.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;

    using ChillHub.Core.Game;
    using ChillHub.Core.Home;

    /// <summary>Доля скачанного (0–100) для карточки очереди — вход: сам <see cref="QueueItem"/>.</summary>
    public class QueueItemPercentConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is QueueItem item && item.TotalBytes > 0) {
                return Math.Min(100.0, Math.Max(0.0, (item.BytesDownloaded * 100.0) / item.TotalBytes));
            }

            return 0.0;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Прогресс-бар карточки очереди «крутится» неопределённо, пока план не посчитан
    /// (TotalBytes ещё 0) — вход: сам <see cref="QueueItem"/>.
    /// </summary>
    public class QueueItemIndeterminateConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is QueueItem item && item.TotalBytes <= 0;

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Объём закачки: скачано из общего. Отдельно от скорости и остатка
    /// (<see cref="QueueItemSpeedConverter"/>), потому что в карточке очереди они стоят
    /// двумя строками друг под другом: сверху — сколько всего, снизу — как быстро идёт.
    /// Одной строкой они занимали ширину, которой у правой колонки нет.
    /// </summary>
    public class QueueItemSizeConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is not QueueItem item || item.TotalBytes <= 0
                ? string.Empty
                : $"{HomeFormat.FormatSize(item.BytesDownloaded)} / {HomeFormat.FormatSize(item.TotalBytes)}";

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Скорость и остаток времени. Пусто, пока скорость неизвестна: «0,0 МБ/с» на первых
    /// секундах закачки — не сведения, а шум, и остаток по такой скорости бесконечен.
    /// </summary>
    public class QueueItemSpeedConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not QueueItem item || item.TotalBytes <= 0 || item.BytesPerSecond <= 0) {
                return string.Empty;
            }

            var speed = $"{item.BytesPerSecond / 1024.0 / 1024.0:0.0} МБ/с";
            var remaining = item.TotalBytes - item.BytesDownloaded;
            return remaining > 0
                ? $"{speed} · осталось {HomeFormat.FormatEta(remaining / item.BytesPerSecond)}"
                : speed;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Подпись под названием в карточке очереди. У качающейся позиции — её собственный
    /// статус, у ожидающей — её место в очереди.
    /// <para>
    /// Все ожидающие показывали одно и то же «Ждёт очереди…», и по трём одинаковым
    /// карточкам нельзя было понять, какая пойдёт следующей — при том что порядок
    /// переставляется стрелками прямо здесь.
    /// </para>
    /// </summary>
    public class QueueItemStatusConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not QueueItem item) {
                return string.Empty;
            }

            if (item.State != QueueItemState.Waiting) {
                return item.StatusText;
            }

            return item.QueuePosition > 1 ? $"В очереди · {item.QueuePosition}-я" : "Следующая в очереди";
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Доля скачанного словами — «43%». Полоса на карточке тонкая, и на сборке в
    /// несколько гигабайт её сдвиг за минуту не читается; проценты читаются сразу.
    /// </summary>
    public class QueueItemPercentTextConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not QueueItem item || item.TotalBytes <= 0) {
                return string.Empty;
            }

            var percent = (item.BytesDownloaded * 100.0) / item.TotalBytes;
            return $"{Math.Clamp(percent, 0, 100):0}%";
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Метка очереди для строки списка игр (см. <see cref="GameInfo.QueueLabel"/>):
    /// качается — «Скачивание · 38%», ждёт — «В очереди»; для позиции вне очереди — пусто.
    /// Процент — целый, чтобы строка списка менялась сотню раз за закачку, а не тысячи.
    /// </summary>
    internal static class QueueRowLabel {
        /// <summary>
        /// Закачка оборвалась. Строка остаётся в списке и после того, как позиция ушла из
        /// очереди: молча вернуться к «Не установлена» — значит сделать вид, что ничего не
        /// было, и человек узнает об обрыве только по тому, что игра не запускается.
        /// </summary>
        internal const string Interrupted = "Обрыв загрузки";

        /// <summary>Подпись для позиции очереди; null-позиция — пустая строка.</summary>
        internal static string For(QueueItem? item) {
            if (item is null) {
                return string.Empty;
            }

            // Проверка занимает ту же строку и тот же прогресс, но называется своим
            // именем: «Скачивание» у игры, которая уже установлена, читается как
            // «мне опять что-то катят», хотя игрок просил сверить файлы.
            var work = item.Kind == QueueTaskKind.Verify ? "Проверка" : "Скачивание";

            // Остановку видно и в списке игр: строка продолжала писать «Скачивание · 38%»
            // всё время, пока движок вставал, — и нажатие «Отмена» выглядело как
            // не сработавшее.
            if (item.Cancelling) {
                return "Останавливаем";
            }

            switch (item.State) {
                case QueueItemState.Running:
                    if (item.TotalBytes > 0) {
                        var percent = Math.Clamp(item.BytesDownloaded * 100.0 / item.TotalBytes, 0, 100);
                        return $"{work} · {percent:0}%";
                    }

                    return work;
                case QueueItemState.Waiting:
                    return "В очереди";
                case QueueItemState.Failed:
                    return Interrupted;
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// Видимость по состоянию позиции очереди: вход — <see cref="QueueItemState"/>,
    /// параметр — его имя (например, "Waiting"). Совпало — Visible, иначе Collapsed.
    /// </summary>
    public class QueueItemStateVisibilityConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is QueueItemState state && parameter is string wanted
                && Enum.TryParse<QueueItemState>(wanted, out var wantedState)) {
                return state == wantedState ? Visibility.Visible : Visibility.Collapsed;
            }

            return Visibility.Collapsed;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
