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
    /// Объём обновления: сделано из общего, а рядом — сколько из этого пришло по сети.
    /// Отдельно от скорости и остатка (<see cref="QueueItemSpeedConverter"/>), потому
    /// что в карточке очереди они стоят двумя строками друг под другом.
    /// <para>
    /// Про сеть здесь сказано не для полноты. Обновление, где почти всё взято из старой
    /// копии файлов на диске, показывало «8,1 ГБ / 49,3 ГБ» — и это читалось как «мне
    /// катят 49 гигабайт», хотя по сети шло два.
    /// </para>
    /// </summary>
    public class QueueItemSizeConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not QueueItem item || item.TotalBytes <= 0) {
                return string.Empty;
            }

            var done = $"{HomeFormat.FormatSize(item.BytesDownloaded)} / {HomeFormat.FormatSize(item.TotalBytes)}";

            // Пока разница невелика (обычная загрузка), второе число — только шум.
            return item.NetworkBytes > 0 && item.NetworkBytes < item.BytesDownloaded * 0.9
                ? $"{done} · по сети {HomeFormat.FormatSize(item.NetworkBytes)}"
                : done;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Скорость и остаток времени. Пусто, пока скорость неизвестна: «0,0 МБ/с» на первых
    /// секундах закачки — не сведения, а шум, и остаток по такой скорости бесконечен.
    /// <para>
    /// Скорость — сетевая, остаток — по скорости работы. Обновление, собранное из кусков
    /// старой копии, идёт быстрее своей сетевой части в десятки раз, и «осталось» по
    /// скорости сети обещало бы часы там, где работы на десять минут.
    /// </para>
    /// </summary>
    public class QueueItemSpeedConverter : IValueConverter {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is not QueueItem item || item.TotalBytes <= 0 || item.BytesPerSecond <= 0) {
                return string.Empty;
            }

            // Остановку уже попросили — считать по этой скорости остаток нечего: закачка
            // не доедет до конца, а «осталось 4 мин» под надписью «Останавливаем…»
            // противоречит само себе.
            if (item.Cancelling) {
                return string.Empty;
            }

            var speed = $"{item.BytesPerSecond / 1024.0 / 1024.0:0.0} МБ/с";
            var remaining = item.TotalBytes - item.BytesDownloaded;
            var rate = item.WorkBytesPerSecond > 0 ? item.WorkBytesPerSecond : item.BytesPerSecond;
            return remaining > 0
                ? $"{speed} · осталось {HomeFormat.FormatEta(remaining / rate)}"
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

            // ОСТАНОВКУ ВИДНО И НА КАРТОЧКЕ, А НЕ ТОЛЬКО В СПИСКЕ ИГР.
            //
            // Движок встаёт не мгновенно и всё это время продолжает слать отчёты, а
            // каждый отчёт переписывает StatusText (см. DownloadQueue.RaiseProgress).
            // Поставленное отменой «Останавливаем…» держалось на карточке доли секунды,
            // дальше строка снова писала «Скачивание обновления…» с растущими
            // процентами — нажатие «Отмена» выглядело как не сработавшее, и его
            // повторяли ещё несколько раз. В строке списка игр это уже учтено
            // (см. QueueRowLabel), а на карточке — самом видном месте — ещё нет.
            if (item.Cancelling) {
                return "Останавливаем…";
            }

            if (item.State != QueueItemState.Waiting) {
                // «Скачивание обновления…» не говорит, велика ли работа и сколько её
                // осталось. Файлы отвечают на это короче любых байт: «12 из 92» видно,
                // как шкалу, и по ней понятно, стоит ли ждать у экрана.
                return item.FilesTotal > 0
                    ? $"{item.StatusText} · файлы {item.FilesDone} из {item.FilesTotal}"
                    : item.StatusText;
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
