// <copyright file="ChangelogRelease.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Changelog {
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// Одна запись списка обновлений: версия, дата и то, что игрок в ней увидел.
    /// <para>
    /// Тип публичный, потому что его читает разметка окна через привязки: у
    /// внутреннего типа WPF не видит свойств и молча рисует пустой список.
    /// </para>
    /// </summary>
    public sealed class ChangelogRelease {
        /// <summary>Русские названия месяцев в родительном падеже — «31 августа 2026».</summary>
        private static readonly string[] MonthsGenitive = {
            "января", "февраля", "марта", "апреля", "мая", "июня",
            "июля", "августа", "сентября", "октября", "ноября", "декабря",
        };

        /// <summary>Номер версии, ровно тот же, что показан в настройках.</summary>
        public required string Version { get; init; }

        /// <summary>Дата выпуска в виде ГГГГ-ММ-ДД; из неё собирается <see cref="DateText"/>.</summary>
        public required string Date { get; init; }

        /// <summary>Что изменилось — по строке на пункт, человеческим языком.</summary>
        public required IReadOnlyList<string> Changes { get; init; }

        /// <summary>
        /// Выпуск, в котором лаунчер не менялся: он поехал ради сервера, установщика
        /// или сборки. Такие версии в окно не попадают — игроку нечего в них читать, —
        /// но в списке остаются, чтобы у КАЖДОЙ выкатки была своя запись.
        /// </summary>
        public bool Technical { get; init; }

        /// <summary>
        /// Дата для показа: «31 августа 2026». Если дату записали в неожиданном виде,
        /// отдаём её как есть — пустая строка выглядела бы как потерянная запись.
        /// </summary>
        public string DateText {
            get {
                if (!DateTime.TryParseExact(this.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) {
                    return this.Date ?? string.Empty;
                }

                return string.Create(CultureInfo.InvariantCulture, $"{parsed.Day} {MonthsGenitive[parsed.Month - 1]} {parsed.Year}");
            }
        }
    }
}
