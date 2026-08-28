// <copyright file="NewsFlowPanelTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Раскладка ленты новостей.
    /// <para>
    /// Проверяется то, ради чего панель и заведена: неполный последний ряд делит ширину
    /// между своими карточками, а не оставляет справа дыру в половину ленты — именно так
    /// выглядела главная с нечётным числом новостей, то есть в половине случаев.
    /// </para>
    /// <para>
    /// Тесты идут на STA-потоке: элементы WPF вне такого потока не создаются. Окна не
    /// поднимаются — измерить и разложить панель можно и без цели отрисовки.
    /// </para>
    /// </summary>
    public class NewsFlowPanelTests {
        /// <summary>Полный ряд делится поровну, неполный — между теми, кто в нём есть.</summary>
        [Fact]
        public void НеполныйРядЗанимаетВсюШирину() {
            UiThread.Run(() => {
                var panel = Layout(count: 3, columns: 2, width: 600);

                Assert.Equal(new Rect(0, 0, 300, 50), Slot(panel, 0));
                Assert.Equal(new Rect(300, 0, 300, 50), Slot(panel, 1));
                Assert.Equal(new Rect(0, 50, 600, 50), Slot(panel, 2));
            });
        }

        /// <summary>Полные ряды остаются равными: делит ширину только последний, неполный.</summary>
        [Fact]
        public void ПолныеРядыОстаютсяРовными() {
            UiThread.Run(() => {
                var panel = Layout(count: 4, columns: 2, width: 600);

                Assert.Equal(new Rect(0, 50, 300, 50), Slot(panel, 2));
                Assert.Equal(new Rect(300, 50, 300, 50), Slot(panel, 3));
            });
        }

        /// <summary>Одна колонка — карточки в столбик во всю ширину, как на узком окне.</summary>
        [Fact]
        public void ОднаКолонкаСтавитКарточкиВСтолбик() {
            UiThread.Run(() => {
                var panel = Layout(count: 2, columns: 1, width: 400);

                Assert.Equal(new Rect(0, 0, 400, 50), Slot(panel, 0));
                Assert.Equal(new Rect(0, 50, 400, 50), Slot(panel, 1));
            });
        }

        /// <summary>Высота панели — сумма рядов: столько места лента и просит у страницы.</summary>
        [Fact]
        public void ВысотаПанелиСкладываетсяИзРядов() {
            UiThread.Run(() => {
                var panel = Panel(count: 3, columns: 2);
                panel.Measure(new Size(600, double.PositiveInfinity));

                Assert.Equal(100, panel.DesiredSize.Height);
            });
        }

        /// <summary>
        /// Бесконечная ширина приходит на первом проходе внутри ScrollViewer: делить её
        /// на колонки нельзя, и панель не должна ни падать, ни просить бесконечности.
        /// </summary>
        [Fact]
        public void БесконечнаяШиринаНеЛомаетИзмерение() {
            UiThread.Run(() => {
                var panel = Panel(count: 3, columns: 2);
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Assert.Equal(0, panel.DesiredSize.Width);
                Assert.False(double.IsInfinity(panel.DesiredSize.Height));
            });
        }

        /// <summary>Пустая лента ничего не занимает — под вкладками не остаётся полосы.</summary>
        [Fact]
        public void ПустаяЛентаНичегоНеЗанимает() {
            UiThread.Run(() => {
                var panel = Panel(count: 0, columns: 2);
                panel.Measure(new Size(600, double.PositiveInfinity));

                Assert.Equal(default, panel.DesiredSize);
            });
        }

        private static NewsFlowPanel Panel(int count, int columns) {
            var panel = new NewsFlowPanel { Columns = columns };
            for (var i = 0; i < count; i++) {
                panel.Children.Add(new Border { Height = 50 });
            }

            return panel;
        }

        private static NewsFlowPanel Layout(int count, int columns, double width) {
            var panel = Panel(count, columns);
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            return panel;
        }

        private static Rect Slot(NewsFlowPanel panel, int index)
            => LayoutInformation.GetLayoutSlot((FrameworkElement)panel.Children[index]);
    }
}
