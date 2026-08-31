// <copyright file="BottomBarLook.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;

    /// <summary>
    /// Что видно в нижней панели главного экрана.
    /// </summary>
    /// <param name="Panel">Сама панель.</param>
    /// <param name="Progress">Полоса выполнения.</param>
    /// <param name="Status">Строка состояния.</param>
    /// <param name="SpeedEta">Строка скорости и остатка.</param>
    /// <param name="FilesSize">Строка файлов и объёма.</param>
    internal readonly record struct BottomBarView(
        bool Panel, bool Progress, bool Status, bool SpeedEta, bool FilesSize);

    /// <summary>
    /// Решение о нижней панели, отделённое от самой панели.
    /// <para>
    /// Причина та же, что у <see cref="ActionButtonState"/>: внутри страницы WPF это
    /// код, который проверяется только руками, а ошибка здесь не падает исключением —
    /// она молча оставляет внизу экрана строку пустоты. Ровно так и было: панель
    /// переживала конец работы и держала высоту под пустой строкой или под «Готово»
    /// до следующей закачки.
    /// </para>
    /// </summary>
    internal static class BottomBarLook {
        /// <summary>Текст статуса, которым лаунчер сообщает «работы нет».</summary>
        private const string IdleStatus = "Готово";

        /// <summary>
        /// Считает видимость панели и её строк.
        /// </summary>
        /// <param name="queueVisible">Показывается очередь загрузок.</param>
        /// <param name="indeterminate">Полоса бежит без известного процента.</param>
        /// <param name="progress">Значение полосы.</param>
        /// <param name="status">Текст строки состояния.</param>
        /// <param name="speedEta">Текст строки скорости и остатка.</param>
        /// <param name="filesSize">Текст строки файлов и объёма.</param>
        /// <returns>Что показать.</returns>
        internal static BottomBarView Decide(
            bool queueVisible,
            bool indeterminate,
            double progress,
            string? status,
            string? speedEta,
            string? filesSize) {
            // Полоса — только когда ей есть что показывать. Полоса в нуле читается как
            // остановившийся процесс, а не как его отсутствие.
            var running = indeterminate || progress > 0;

            // Строка состояния молчит, когда сказать нечего: пустой TextBlock под идущей
            // полосой всё равно держит строку высоты, а «Готово» рядом с ней отчитывается
            // о конце работы, которая ещё идёт.
            var speaks = !IsIdle(status);

            return new BottomBarView(
                Panel: queueVisible || running || speaks,
                Progress: running,
                Status: speaks,
                SpeedEta: !string.IsNullOrWhiteSpace(speedEta),
                FilesSize: !string.IsNullOrWhiteSpace(filesSize));
        }

        /// <summary>
        /// Сообщает ли строка состояния о работе.
        /// <para>
        /// Пустая строка — обычный способ сказать «работы нет». «Готово» остаётся
        /// известным этому месту, потому что так до сих пор отчитываются общие с
        /// другими экранами куски (<c>Core.Game.GameSyncRunner</c>, очередь загрузок),
        /// причём один — с точкой на конце, другой — без. Панель, не узнавшая отчёт о
        /// конце работы, остаётся висеть на экране с ним, поэтому точку здесь снимаем:
        /// разница в один знак не должна решать, уйдёт панель или нет.
        /// </para>
        /// </summary>
        /// <param name="text">Текст строки.</param>
        /// <returns>true, если работы нет.</returns>
        private static bool IsIdle(string? text) {
            if (string.IsNullOrWhiteSpace(text)) {
                return true;
            }

            return string.Equals(text.Trim().TrimEnd('.'), IdleStatus, StringComparison.Ordinal);
        }
    }
}
