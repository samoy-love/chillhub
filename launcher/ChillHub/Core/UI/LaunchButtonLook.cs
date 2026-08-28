// <copyright file="LaunchButtonLook.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    /// <summary>
    /// Как одеты две строки на кнопке запуска витрины.
    /// <para>
    /// Стиль кнопки задаёт заливку и рамку, но до самих надписей не дотягивается: обе
    /// живут именованными <see cref="TextBlock"/> внутри содержимого кнопки, и текст в них
    /// пишет страница. Пока правило жило там же, кнопки различались только цветом
    /// заливки — залитая и контурная стояли вплотную одинаково жирными близнецами.
    /// </para>
    /// <para>
    /// Здесь оно одно на обе кнопки и проверяется тестом: у главной подпись плотнее и
    /// набрана тем же цветом, что заголовок, у запасной — обычным начертанием и
    /// вторичным цветом.
    /// </para>
    /// </summary>
    public static class LaunchButtonLook {
        /// <summary>Насколько приглушена подпись на залитой кнопке.</summary>
        public const double AccentNoteOpacity = 0.85;

        /// <summary>Одевает надписи кнопки запуска.</summary>
        /// <param name="title">Крупная строка: откуда копия.</param>
        /// <param name="note">Мелкая строка: что произойдёт по нажатию.</param>
        /// <param name="accent">Кнопка залита акцентом — это главный путь.</param>
        /// <param name="subtleForeground">Вторичный цвет для подписи запасной кнопки.</param>
        public static void Apply(TextBlock title, TextBlock note, bool accent, Brush subtleForeground) {
            title.FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal;

            // На заливке подпись того же цвета, что заголовок, просто тише: вторичный
            // серый по акценту не проходит по контрасту (см. ThemeContrastTests).
            note.Foreground = accent ? title.Foreground : subtleForeground;
            note.Opacity = accent ? AccentNoteOpacity : 1.0;
        }
    }
}
