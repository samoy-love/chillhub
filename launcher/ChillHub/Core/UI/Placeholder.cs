// <copyright file="Placeholder.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System.Windows;

    /// <summary>
    /// Подсказка внутри пустого поля ввода.
    /// <para>
    /// Свойство поля, а не отдельная надпись поверх него. Пока подсказка была соседним
    /// TextBlock в той же ячейке сетки, её отступы подбирались руками под отступы поля — и
    /// расходились с ними: в форме обратной связи текст подсказки стоял не там, где после
    /// клика появлялась каретка. Внутри шаблона поля подсказка получает ту же рамку, тот же
    /// Padding, тот же шрифт и то же выравнивание, что и настоящий текст, — совпадение
    /// перестаёт быть делом везения.
    /// </para>
    /// </summary>
    public static class Placeholder {
        /// <summary>Текст подсказки; пустая строка — подсказки нет.</summary>
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(Placeholder),
            new FrameworkPropertyMetadata(string.Empty));

        /// <summary>Читает текст подсказки.</summary>
        /// <param name="element">Поле ввода.</param>
        /// <returns>Текст подсказки.</returns>
        public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

        /// <summary>Ставит текст подсказки.</summary>
        /// <param name="element">Поле ввода.</param>
        /// <param name="value">Текст подсказки.</param>
        public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
    }
}
