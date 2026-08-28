// <copyright file="VisualTreeSearch.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System.Collections.Generic;
    using System.Windows;
    using System.Windows.Media;

    /// <summary>
    /// Обход дерева отрисовки: найти уже созданные элементы там, где до них нельзя
    /// дотянуться по имени.
    /// <para>
    /// Строки списка создаёт сам список, и элементов внутри них у страницы нет — ни поля,
    /// ни имени. А значок в строке грузится обработчиком <c>Loaded</c>, то есть в момент
    /// создания строки: чтобы перечитать картинки уже показанного списка, до них надо
    /// сначала добраться.
    /// </para>
    /// </summary>
    public static class VisualTreeSearch {
        /// <summary>
        /// Все элементы нужного типа в поддереве, включая вложенные.
        /// </summary>
        /// <typeparam name="T">Что искать.</typeparam>
        /// <param name="root">Откуда искать; null — искать негде.</param>
        /// <returns>Найденные элементы в порядке обхода сверху вниз.</returns>
        public static IEnumerable<T> Descendants<T>(DependencyObject? root)
            where T : DependencyObject {
            if (root == null) {
                yield break;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++) {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found) {
                    yield return found;
                }

                foreach (var nested in Descendants<T>(child)) {
                    yield return nested;
                }
            }
        }
    }
}
