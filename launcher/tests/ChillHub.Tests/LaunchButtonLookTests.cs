// <copyright file="LaunchButtonLookTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Две кнопки запуска на витрине стоят вплотную, и различать их обязана не только
    /// заливка: при одинаковых надписях они читались как близнецы, и какая из них
    /// главная, приходилось угадывать.
    /// </summary>
    public class LaunchButtonLookTests {
        /// <summary>Главная кнопка: плотная подпись тем же цветом, что заголовок.</summary>
        [Fact]
        public void ЗалитаяКнопкаНабранаПлотнее() => UiThread.Run(() => {
            var (title, note) = Pair();

            LaunchButtonLook.Apply(title, note, accent: true, subtleForeground: Brushes.Gray);

            Assert.Equal(FontWeights.SemiBold, title.FontWeight);
            Assert.Same(title.Foreground, note.Foreground);
            Assert.Equal(LaunchButtonLook.AccentNoteOpacity, note.Opacity);
        });

        /// <summary>Запасная: обычное начертание и вторичный цвет подписи, без приглушения.</summary>
        [Fact]
        public void КонтурнаяКнопкаНабранаТише() => UiThread.Run(() => {
            var (title, note) = Pair();

            LaunchButtonLook.Apply(title, note, accent: false, subtleForeground: Brushes.Gray);

            Assert.Equal(FontWeights.Normal, title.FontWeight);
            Assert.Same(Brushes.Gray, note.Foreground);
            Assert.Equal(1.0, note.Opacity);
        });

        /// <summary>
        /// Одна и та же кнопка меняет вид, когда меняется запомненный вариант запуска:
        /// оформление не должно залипать на том, каким кнопка была в прошлый раз.
        /// </summary>
        [Fact]
        public void ОформлениеНеЗалипаетМеждуВызовами() => UiThread.Run(() => {
            var (title, note) = Pair();

            LaunchButtonLook.Apply(title, note, accent: true, subtleForeground: Brushes.Gray);
            LaunchButtonLook.Apply(title, note, accent: false, subtleForeground: Brushes.Gray);

            Assert.Equal(FontWeights.Normal, title.FontWeight);
            Assert.Equal(1.0, note.Opacity);
            Assert.Same(Brushes.Gray, note.Foreground);
        });

        private static (TextBlock Title, TextBlock Note) Pair()
            => (new TextBlock { Foreground = Brushes.White }, new TextBlock());
    }
}
