// <copyright file="HomeFormat.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;

    /// <summary>
    /// Чистое форматирование строк для главной страницы: размеры, оставшееся время,
    /// склонение «день/дня/дней», безопасные имена файлов и путей.
    /// Ничего не знает про UI — только текст.
    /// </summary>
    internal static class HomeFormat {
        /// <summary>Человеческий размер: «1,5 ГБ», «230,0 МБ» и т.п.</summary>
        internal static string FormatSize(long bytes) {
            const double KB = 1024.0;
            const double MB = KB * 1024.0;
            const double GB = MB * 1024.0;
            if (bytes >= (long)GB) {
                return $"{bytes / GB:0.0} ГБ";
            }

            if (bytes >= (long)MB) {
                return $"{bytes / MB:0.0} МБ";
            }

            if (bytes >= (long)KB) {
                return $"{bytes / KB:0.0} КБ";
            }

            return $"{bytes} Б";
        }

        /// <summary>
        /// Формат оставшегося времени словами: «40 с», «13 мин», «1 ч 05 мин», «2 дня 3 ч».
        /// <para>
        /// Раньше было «13:22» — часы это или минуты, читатель угадывал по контексту, а
        /// секунды в оценке, которая и так прыгает на ±минуту, только дёргались. Точность
        /// шага: до минуты — секунды, до часа — минуты, до суток — часы и минуты, дальше — дни
        /// и часы; такую оценку читают, а не сверяют с часами.
        /// </para>
        /// </summary>
        internal static string FormatEta(double seconds) {
            // Единственный источник исключений здесь — переполнение TimeSpan на абсурдных значениях;
            // в этом случае показываем прочерк, а не ломаем строку прогресса.
            try {
                if (double.IsNaN(seconds) || double.IsInfinity(seconds)) {
                    return "—";
                }

                var total = Math.Max(0, (long)Math.Ceiling(seconds));
                var ts = TimeSpan.FromSeconds(total);

                if (ts.TotalDays >= 1) {
                    int days = ts.Days;
                    return ts.Hours > 0
                        ? $"{days} {PluralizeDayRu(days)} {ts.Hours} ч"
                        : $"{days} {PluralizeDayRu(days)}";
                }

                if (ts.TotalHours >= 1) {
                    return $"{(int)ts.TotalHours} ч {ts.Minutes:00} мин";
                }

                if (ts.TotalMinutes >= 1) {
                    return $"{(int)Math.Ceiling(ts.TotalMinutes)} мин";
                }

                return $"{ts.Seconds} с";
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"HomeFormat.FormatEta: не удалось отформатировать {seconds} с: {ex.Message}");
                return "—";
            }
        }

        /// <summary>Русское склонение слова «день» по числу.</summary>
        internal static string PluralizeDayRu(int n) {
            int n10 = n % 10;
            int n100 = n % 100;
            if (n10 == 1 && n100 != 11) {
                return "день";
            }

            if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) {
                return "дня";
            }

            return "дней";
        }

        /// <summary>Заменяет недопустимые в имени файла символы на «_».</summary>
        internal static string SanitizeFileName(string name) {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var arr = (name ?? string.Empty).ToCharArray();
            for (int i = 0; i < arr.Length; i++) {
                if (Array.IndexOf(invalid, arr[i]) >= 0) {
                    arr[i] = '_';
                }
            }

            var s = new string(arr).Trim();
            return string.IsNullOrEmpty(s) ? "Game" : s;
        }

        /// <summary>
        /// Путь для показа пользователю: обратные слэши, без задвоенных разделителей.
        /// </summary>
        /// <remarks>
        /// Разделитель обратный, как везде в Windows. Прямой был удобнее нам — он не
        /// требует экранирования в строках, — но человек видит путь не в строке, а рядом
        /// с адресной строкой проводника, где тот же каталог написан иначе, и копирует
        /// он его туда же. Ведущий «\\» сохраняется: у сетевого пути это не дубль, а
        /// синтаксис UNC, и без него путь указывает уже не туда.
        /// </remarks>
        /// <param name="path">Путь с любыми слэшами, в том числе задвоенными.</param>
        /// <returns>Путь для показа; пустая строка, если показывать нечего.</returns>
        internal static string NormalizeDisplayPath(string path) {
            if (string.IsNullOrWhiteSpace(path)) {
                return string.Empty;
            }

            var s = path.Replace('/', '\\');
            var uncPrefix = s.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : string.Empty;
            var rest = s.Substring(uncPrefix.Length);
            while (rest.Contains(@"\\", StringComparison.Ordinal)) {
                rest = rest.Replace(@"\\", @"\");
            }

            return uncPrefix + rest;
        }

        /// <summary>
        /// Приводит windows-путь к нормальной форме: одинарные обратные слеши вместо задвоенных.
        /// Ведущий «\\» сохраняется — это префикс UNC, а не результат экранирования.
        /// </summary>
        internal static string NormalizeWindowsPath(string path) {
            if (string.IsNullOrWhiteSpace(path)) {
                return path ?? string.Empty;
            }

            var uncPrefix = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : string.Empty;
            var rest = path.Substring(uncPrefix.Length);
            while (rest.Contains(@"\\", StringComparison.Ordinal)) {
                rest = rest.Replace(@"\\", @"\");
            }

            return uncPrefix + rest;
        }
    }
}
