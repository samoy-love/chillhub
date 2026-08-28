// <copyright file="PlaceholderApiTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Подсказка поля и отступ каретки — мелочи, на которых держится совпадение
    /// подсказки с местом набора (см. <see cref="PlaceholderAlignmentTests"/>).
    /// </summary>
    public class PlaceholderApiTests {
        /// <summary>Текст подсказки читается тем же свойством, которым записан.</summary>
        [Fact]
        public void ПодсказкаЧитаетсяСПоля() => UiThread.Run(() => {
            var box = new TextBox();

            Assert.Equal(string.Empty, Placeholder.GetText(box));

            Placeholder.SetText(box, "Ваше имя");
            Assert.Equal("Ваше имя", Placeholder.GetText(box));
        });

        /// <summary>Отступ каретки прибавляется по бокам и не трогает верх и низ.</summary>
        [Fact]
        public void ОтступКареткиПрибавляетсяТолькоПоБокам() {
            var padded = CaretInsetConverter.WithCaretInset(new Thickness(8, 6, 8, 6));

            Assert.Equal(new Thickness(10, 6, 10, 6), padded);
        }

        /// <summary>Не-Thickness на входе — нулевые отступы плюс место под каретку.</summary>
        [Fact]
        public void ЧужоеЗначениеДаётТолькоОтступКаретки() {
            var conv = new CaretInsetConverter();

            var result = conv.Convert("не отступы", typeof(Thickness), null!, CultureInfo.InvariantCulture);

            Assert.Equal(new Thickness(CaretInsetConverter.CaretInset, 0, CaretInsetConverter.CaretInset, 0), result);
            Assert.Throws<NotImplementedException>(
                () => conv.ConvertBack(result, typeof(Thickness), null!, CultureInfo.InvariantCulture));
        }
    }
}
