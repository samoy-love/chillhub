// <copyright file="QueueDockLayout.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Сколько строк очереди показывать внизу главного экрана и что написать на
    /// строке-раскрывашке под ними.
    /// <para>
    /// Раньше высота дока была долей окна (28%), и на невысоком окне очередь занимала
    /// четверть экрана, чтобы показать две с половиной строки со своим скроллом, а ленте
    /// новостей оставалось полторы карточки — тоже со скроллом. Очередь — состояние, а не
    /// содержимое: она берёт ровно столько, сколько нужно её строкам, а остальное прячет
    /// за строкой «Показать ещё N». На низком окне остаётся только та строка, которая
    /// качается: это ровно то, за чем на очередь смотрят.
    /// </para>
    /// </summary>
    public static class QueueDockLayout {
        /// <summary>
        /// Высота страницы, ниже которой в свёрнутом доке остаётся одна строка. Считается
        /// от страницы, а не от окна: шапка и рамки окна доку всё равно не достаются.
        /// </summary>
        public const double CompactBelowHeight = 760;

        /// <summary>Сколько строк показывает свёрнутый док на обычном окне.</summary>
        public const int DefaultRows = 3;

        /// <summary>Не чаще, чем раз в столько миллисекунд, перерисовывается строка закачки.</summary>
        public const double RowRefreshMs = 250;

        /// <summary>Что показать в доке очереди.</summary>
        /// <param name="count">Всего позиций в очереди.</param>
        /// <param name="pageHeight">Высота страницы в пикселях; NaN и ноль — разметки ещё не было.</param>
        /// <param name="expanded">Док раскрыт пользователем.</param>
        /// <returns>Число видимых строк и подпись раскрывашки.</returns>
        public static QueueDockView Compute(int count, double pageHeight, bool expanded) {
            if (count <= 0) {
                return new QueueDockView(0, string.Empty);
            }

            var collapsed = Math.Min(count, CollapsedRows(pageHeight));
            var visible = expanded ? count : collapsed;

            var toggle = count > visible
                ? $"Показать ещё {count - visible}"
                : count > collapsed ? "Свернуть очередь" : string.Empty;

            return new QueueDockView(visible, toggle);
        }

        /// <summary>
        /// Оставляет в <paramref name="visible"/> первые <paramref name="rows"/> позиций
        /// очереди, правя список по месту.
        /// <para>
        /// Именно по месту, а не пересборкой: карточка в доке перерисовывается на каждый тик
        /// прогресса, и полная замена списка гасила бы наведение и подсказку под курсором.
        /// Позиция трогается, только если на этом месте стоит другой объект — <c>QueueItem</c>
        /// неизменяем, и обновление приезжает новым экземпляром.
        /// </para>
        /// </summary>
        /// <typeparam name="T">Тип позиции очереди.</typeparam>
        /// <param name="source">Очередь целиком.</param>
        /// <param name="visible">Список, привязанный к доку.</param>
        /// <param name="rows">Сколько первых позиций показать.</param>
        public static void ApplyVisible<T>(IList<T> source, IList<T> visible, int rows) {
            var take = Math.Min(Math.Max(0, rows), source.Count);

            while (visible.Count > take) {
                visible.RemoveAt(visible.Count - 1);
            }

            for (var i = 0; i < take; i++) {
                if (i >= visible.Count) {
                    visible.Add(source[i]);
                }
                else if (!ReferenceEquals(visible[i], source[i])) {
                    visible[i] = source[i];
                }
            }
        }

        /// <summary>
        /// Пора ли перерисовывать строку качающейся игры.
        /// <para>
        /// Отчёты о ходе закачки приходят десять раз в секунду, и каждый из них заменял
        /// позицию в списке новым объектом. Для WPF это НОВАЯ СТРОКА: контейнер сносится и
        /// собирается заново со всем содержимым — значком, четырьмя подписями, полосой.
        /// Десять пересборок в секунду на каждую качающуюся игру видно и в загрузке
        /// процессора, и в том, как под курсором пропадает подсветка.
        /// </para>
        /// <para>
        /// Цифры от этого не отстают: четыре раза в секунду — быстрее, чем человек успевает
        /// прочитать проценты. А смена состояния (началась, кончилась, отменена) проходит
        /// без задержки всегда: это не цифра, а событие.
        /// </para>
        /// </summary>
        /// <param name="sameState">Состояние позиции не изменилось.</param>
        /// <param name="sinceLastMs">Сколько миллисекунд прошло с прошлой перерисовки строки.</param>
        /// <returns>true, если строку нужно обновить сейчас.</returns>
        public static bool ShouldRefreshRow(bool sameState, double sinceLastMs)
            => !sameState || sinceLastMs >= RowRefreshMs;

        /// <summary>Сколько строк помещается в свёрнутый док при такой высоте страницы.</summary>
        /// <param name="pageHeight">Высота страницы в пикселях.</param>
        /// <returns>1 на низком окне, <see cref="DefaultRows"/> на обычном.</returns>
        private static int CollapsedRows(double pageHeight) {
            // NaN и ноль приходят на первом проходе разметки, пока окно не измерено:
            // считаем окно обычным, иначе док на старте схлопывался бы и прыгал.
            var compact = !double.IsNaN(pageHeight) && pageHeight > 0 && pageHeight < CompactBelowHeight;
            return compact ? 1 : DefaultRows;
        }
    }

    /// <summary>Вид дока очереди: сколько строк видно и что написано на раскрывашке.</summary>
    /// <param name="VisibleRows">Число видимых строк.</param>
    /// <param name="ToggleText">Подпись раскрывашки; пустая, если раскрывать и сворачивать нечего.</param>
    public sealed record QueueDockView(int VisibleRows, string ToggleText);
}
