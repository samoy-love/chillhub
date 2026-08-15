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
    /// Строка с числами под статусом карточки: сколько скачано из скольки, с какой
    /// скоростью и сколько осталось.
    /// <para>
    /// Без неё карточка показывала только название и полосу, а на сборке в 16 ГБ полоса
    /// за минуту сдвигается на волосок — работающая закачка выглядела зависшей. Прежняя
    /// нижняя панель эти числа показывала, и при переносе очереди вниз они потерялись.
    /// </para>
    /// </summary>
    public class QueueItemDetailConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not QueueItem item || item.TotalBytes <= 0) {
                return string.Empty;
            }

            var parts = new List<string> {
                $"{HomeFormat.FormatSize(item.BytesDownloaded)} / {HomeFormat.FormatSize(item.TotalBytes)}",
            };

            if (item.BytesPerSecond > 0) {
                parts.Add($"{item.BytesPerSecond / 1024.0 / 1024.0:0.0} МБ/с");

                var remaining = item.TotalBytes - item.BytesDownloaded;
                if (remaining > 0) {
                    parts.Add("осталось " + HomeFormat.FormatEta(remaining / item.BytesPerSecond));
                }
            }

            return string.Join(" · ", parts);
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
        /// <summary>Подпись для позиции очереди; null-позиция — пустая строка.</summary>
        internal static string For(QueueItem? item) {
            if (item is null) {
                return string.Empty;
            }

            switch (item.State) {
                case QueueItemState.Running:
                    if (item.TotalBytes > 0) {
                        var percent = Math.Clamp(item.BytesDownloaded * 100.0 / item.TotalBytes, 0, 100);
                        return $"Скачивание · {percent:0}%";
                    }

                    return "Скачивание";
                case QueueItemState.Waiting:
                    return "В очереди";
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
