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
    /// Действие — главная строка на обеих кнопках. Название копии идёт ниже,
    /// вторичным цветом; главный вариант отличается заливкой, а не размером.
    /// </para>
    /// </summary>
    public static class LaunchButtonLook {
        /// <summary>Короткая подпись меню дополнительных действий.</summary>
        public static string MoreOptionsLabel => "Ещё";

        /// <summary>Насколько приглушено название копии на залитой кнопке.</summary>
        public const double AccentNoteOpacity = 0.85;

        /// <summary>Одевает надписи кнопки запуска.</summary>
        /// <param name="title">Вторичная строка: откуда копия.</param>
        /// <param name="note">Главная строка: что произойдёт по нажатию.</param>
        /// <param name="accent">Кнопка залита акцентом — это главный путь.</param>
        /// <param name="subtleForeground">Вторичный цвет названия копии.</param>
        /// <param name="enabled">Доступно ли действие.</param>
        public static void Apply(TextBlock title, TextBlock note, bool accent, Brush subtleForeground, bool enabled = true) {
            // Действие заметнее названия копии. Основной цвет наследуется от кнопки,
            // поэтому выключение и смена темы не оставляют яркую надпись на сером фоне.
            note.ClearValue(TextBlock.ForegroundProperty);
            note.FontWeight = FontWeights.SemiBold;
            note.Opacity = 1.0;
            title.FontWeight = FontWeights.Normal;
            title.Foreground = accent || !enabled ? note.Foreground : subtleForeground;
            title.Opacity = accent && enabled ? AccentNoteOpacity : 1.0;
        }
    }
}
