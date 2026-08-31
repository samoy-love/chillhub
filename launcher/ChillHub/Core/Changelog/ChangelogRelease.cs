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
        /// Выпуск, в котором для игрока ничего не изменилось: он поехал ради сервера,
        /// установщика, сборки или перекладки кода внутри самого лаунчера. Такие версии
        /// в окно не попадают — читать в них нечего, — но в списке остаются, чтобы
        /// у КАЖДОЙ выкатки была своя запись.
        /// </summary>
        public bool Technical { get; init; }

        /// <summary>
        /// Выпуск, которого человек ещё не видел: вышел после того, как список
        /// показывали в прошлый раз. Отмечается перед показом
        /// (см. <see cref="ChangelogMarks"/>), в окне выделяется значком «Новое».
        /// <para>
        /// Обычное свойство без уведомлений: значения проставляются до того, как
        /// список попадёт в окно, и по ходу показа не меняются.
        /// </para>
        /// </summary>
        public bool IsNew { get; set; }

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
