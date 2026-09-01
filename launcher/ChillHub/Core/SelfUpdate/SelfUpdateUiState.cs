// <copyright file="SelfUpdateUiState.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    /// <summary>
    /// Что окно обновления должно показать после очередного шага процесса.
    /// <para>
    /// Шов между логикой самообновления и окном. Логика не знает ни про
    /// <c>StatusText</c>, ни про <c>PrimaryBtn</c> — она возвращает описание
    /// состояния, а окно его применяет. Каждое поле необязательное: <c>null</c>
    /// означает «этот элемент не трогать», потому что исходный код действительно
    /// на части веток оставлял, например, прогресс как есть.
    /// </para>
    /// </summary>
    internal sealed class SelfUpdateUiState {
        /// <summary>Текст статуса; null — оставить прежний.</summary>
        internal string? StatusText { get; init; }

        /// <summary>Подпись главной кнопки; null — оставить прежнюю.</summary>
        internal string? ButtonContent { get; init; }

        /// <summary>Доступность главной кнопки; null — оставить прежнюю.</summary>
        internal bool? ButtonEnabled { get; init; }

        /// <summary>Режим полосы прогресса; null — оставить прежний.</summary>
        internal bool? Indeterminate { get; init; }

        /// <summary>Значение полосы прогресса; null — оставить прежнее.</summary>
        internal double? ProgressValue { get; init; }
    }

    /// <summary>
    /// Видимость полосы прогресса в окне самообновления.
    /// <para>
    /// Полоса показывает работу, а не занимает место. Прибитая видимой в разметке,
    /// она стояла пустой дорожкой под любым сообщением окна — в том числе под
    /// «не удалось проверить обновления» — и читалась как загрузка, застрявшая на
    /// нуле: игрок ждал, что она поедет, а ехать было нечему.
    /// </para>
    /// </summary>
    internal static class SelfUpdateProgressBar {
        /// <summary>
        /// Видна ли полоса.
        /// </summary>
        /// <param name="indeterminate">Полоса бежит без известного процента.</param>
        /// <param name="value">Значение полосы.</param>
        /// <returns>true, если полосе есть что показать.</returns>
        internal static bool Visible(bool indeterminate, double value) => indeterminate || value > 0;
    }
}
