// <copyright file="NewsFlowPanel.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;
    using System.Windows;
    using System.Windows.Controls;

    /// <summary>
    /// Раскладка ленты новостей: колонки равной ширины, но неполный последний ряд
    /// делит ширину между теми, кто в нём есть.
    /// <para>
    /// UniformGrid держал ширину колонки одинаковой всегда, и три карточки в двух
    /// колонках оставляли справа от последней дыру в половину ленты — на широком окне
    /// это метр пустоты внутри контента, который читается как «здесь что-то не
    /// загрузилось». Новостей в ленте нечётное число ровно в половине случаев, так что
    /// дыра была не исключением, а обычным видом главного экрана.
    /// </para>
    /// </summary>
    public class NewsFlowPanel : Panel {
        /// <summary>Сколько колонок в полном ряду.</summary>
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(NewsFlowPanel),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Gets or sets число колонок в полном ряду.</summary>
        public int Columns {
            get => (int)this.GetValue(ColumnsProperty);
            set => this.SetValue(ColumnsProperty, value);
        }

        /// <summary>
        /// Сколько элементов стоит в ряду с номером <paramref name="row"/>: во всех рядах,
        /// кроме последнего, — по числу колонок, в последнем — остаток.
        /// </summary>
        /// <param name="count">Всего элементов.</param>
        /// <param name="columns">Колонок в полном ряду.</param>
        /// <param name="row">Номер ряда, с нуля.</param>
        /// <returns>Число элементов в ряду.</returns>
        public static int ItemsInRow(int count, int columns, int row) {
            var cols = Math.Max(1, columns);
            var first = row * cols;
            if (count <= 0 || first >= count) {
                return 0;
            }

            return Math.Min(cols, count - first);
        }

        /// <inheritdoc/>
        protected override Size MeasureOverride(Size availableSize) {
            var cols = Math.Max(1, this.Columns);
            var count = this.InternalChildren.Count;
            if (count == 0) {
                return default;
            }

            // Бесконечная ширина случается на первом проходе внутри ScrollViewer с
            // отключённой горизонтальной прокруткой: делить её на колонки нельзя,
            // меряем карточки свободно и отдаём их суммарную высоту.
            var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            double totalHeight = 0;

            for (var row = 0; row * cols < count; row++) {
                var inRow = ItemsInRow(count, cols, row);
                var cell = width > 0 ? width / inRow : double.PositiveInfinity;
                double rowHeight = 0;

                for (var i = 0; i < inRow; i++) {
                    var child = this.InternalChildren[(row * cols) + i];
                    child.Measure(new Size(cell, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                }

                totalHeight += rowHeight;
            }

            return new Size(width, totalHeight);
        }

        /// <inheritdoc/>
        protected override Size ArrangeOverride(Size finalSize) {
            var cols = Math.Max(1, this.Columns);
            var count = this.InternalChildren.Count;
            double y = 0;

            for (var row = 0; row * cols < count; row++) {
                var inRow = ItemsInRow(count, cols, row);
                var cell = finalSize.Width / inRow;
                double rowHeight = 0;

                for (var i = 0; i < inRow; i++) {
                    rowHeight = Math.Max(rowHeight, this.InternalChildren[(row * cols) + i].DesiredSize.Height);
                }

                for (var i = 0; i < inRow; i++) {
                    this.InternalChildren[(row * cols) + i].Arrange(new Rect(i * cell, y, cell, rowHeight));
                }

                y += rowHeight;
            }

            return finalSize;
        }
    }
}
