// <copyright file="ButtonIcon.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    /// <summary>Значок кнопки в общей сетке 24 × 24, отдельно от её подписи.</summary>
    public static class ButtonIcon {
        /// <summary>Контур значка; null оставляет кнопку без значка и зазора.</summary>
        public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached(
            "Data", typeof(Geometry), typeof(ButtonIcon), new FrameworkPropertyMetadata(null));

        /// <summary>Заливает контур цветом подписи, чтобы значок не терялся рядом с текстом.</summary>
        public static readonly DependencyProperty IsFilledProperty = DependencyProperty.RegisterAttached(
            "IsFilled", typeof(bool), typeof(ButtonIcon), new FrameworkPropertyMetadata(false));

        /// <summary>Проверяет, нужна ли заливка значка.</summary>
        /// <param name="element">Кнопка.</param>
        /// <returns>Нужна ли заливка.</returns>
        public static bool GetIsFilled(DependencyObject element) => (bool)element.GetValue(IsFilledProperty);

        /// <summary>Включает заливку значка.</summary>
        /// <param name="element">Кнопка.</param>
        /// <param name="value">Нужна ли заливка.</param>
        public static void SetIsFilled(DependencyObject element, bool value) => element.SetValue(IsFilledProperty, value);

        /// <summary>Показывает действие модели векторным значком вместо символа шрифта.</summary>
        /// <param name="button">Кнопка действия или варианта запуска.</param>
        /// <param name="glyph">Значок, выбранный моделью состояния.</param>
        internal static void ApplyGlyph(Button button, string glyph) {
            var key = glyph switch {
                "\uE768" => "Icon.Play",
                "\uE896" => "Icon.Download",
                "\uE895" or "\uE72C" => "Icon.Refresh",
                "\uE711" => "Icon.Close",
                "\uE916" => "Icon.Clock",
                "\uE73E" => "Icon.Check",
                _ => null,
            };
            if (key == null) {
                SetData(button, null);
            }
            else {
                button.SetResourceReference(DataProperty, key);
            }

            SetIsFilled(button, glyph == "\uE768");
        }

        /// <summary>Читает контур значка.</summary>
        /// <param name="element">Кнопка.</param>
        /// <returns>Контур или null.</returns>
        public static Geometry? GetData(DependencyObject element) => (Geometry?)element.GetValue(DataProperty);

        /// <summary>Задаёт контур значка.</summary>
        /// <param name="element">Кнопка.</param>
        /// <param name="value">Контур или null.</param>
        public static void SetData(DependencyObject element, Geometry? value) => element.SetValue(DataProperty, value);
    }
}
