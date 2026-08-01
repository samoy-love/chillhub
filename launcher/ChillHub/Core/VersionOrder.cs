// <copyright file="VersionOrder.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Сравнение версий сборок по смыслу, а не по алфавиту.
    /// Список сборок приходит с сервера в произвольном порядке, и брать из него
    /// первый элемент как «последнюю версию» нельзя: на проде так выбиралась 1.0.2
    /// при доступной 1.1.10. Строковое сравнение тоже не годится — "1.1.9" &gt; "1.1.10".
    /// </summary>
    public static class VersionOrder {
        /// <summary>
        /// Сравнивает две версии: числовые сегменты — как числа, остаток — как текст.
        /// </summary>
        /// <param name="a">Первая версия.</param>
        /// <param name="b">Вторая версия.</param>
        /// <returns>Отрицательное, если a меньше b; 0 при равенстве; положительное иначе.</returns>
        public static int Compare(string? a, string? b) {
            var left = SplitSegments(a);
            var right = SplitSegments(b);
            var count = Math.Max(left.Count, right.Count);
            for (var i = 0; i < count; i++) {
                // Отсутствующий сегмент считаем нулём: 1.2 и 1.2.0 — одна и та же версия
                var l = i < left.Count ? left[i] : "0";
                var r = i < right.Count ? right[i] : "0";
                var cmp = CompareSegment(l, r);
                if (cmp != 0) {
                    return cmp;
                }
            }

            return 0;
        }

        /// <summary>
        /// Максимальная версия из списка. Пустые значения игнорируются.
        /// </summary>
        /// <param name="versions">Список версий (в любом порядке).</param>
        /// <returns>Наибольшая версия либо null, если выбирать не из чего.</returns>
        public static string? SelectLatest(IEnumerable<string?>? versions) {
            if (versions == null) {
                return null;
            }

            string? best = null;
            foreach (var raw in versions) {
                if (string.IsNullOrWhiteSpace(raw)) {
                    continue;
                }

                var candidate = raw.Trim();
                if (best == null || Compare(candidate, best) > 0) {
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>Разбивает версию на сегменты по точкам, дефисам и подчёркиваниям.</summary>
        private static List<string> SplitSegments(string? version) {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(version)) {
                return result;
            }

            var current = new System.Text.StringBuilder();
            foreach (var ch in version.Trim()) {
                if (ch == '.' || ch == '-' || ch == '_' || ch == '+') {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else {
                    current.Append(ch);
                }
            }

            result.Add(current.ToString());
            return result;
        }

        /// <summary>
        /// Сравнивает один сегмент. Полностью числовые сравниваем численно (10 &gt; 9),
        /// иначе — порядковым сравнением строк, чтобы поведение не зависело от локали.
        /// </summary>
        private static int CompareSegment(string a, string b) {
            var aNum = long.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var an);
            var bNum = long.TryParse(b, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bn);
            if (aNum && bNum) {
                return an.CompareTo(bn);
            }

            // Числовой сегмент считаем старше текстового: «2» новее, чем «rc».
            if (aNum != bNum) {
                return aNum ? 1 : -1;
            }

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
