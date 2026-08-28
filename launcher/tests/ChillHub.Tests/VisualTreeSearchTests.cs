// <copyright file="VisualTreeSearchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Linq;
    using System.Windows.Controls;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Обход дерева отрисовки: по нему находят значки уже показанных строк списка, чтобы
    /// перечитать их при обновлении.
    /// <para>
    /// Промах здесь тихий: «Обновить список игр» перестал бы менять обложки, а понять
    /// это можно было бы только по жалобе «картинка старая».
    /// </para>
    /// </summary>
    public class VisualTreeSearchTests {
        /// <summary>Находятся и свои дети, и вложенные глубже.</summary>
        [Fact]
        public void НаходятсяЭлементыНаЛюбойГлубине() => UiThread.Run(() => {
            using var root = new OffscreenVisualRoot();
            var outer = root.Add(new Image());
            var panel = root.Add(new StackPanel());
            var inner = new Image();
            panel.Children.Add(inner);
            root.Root.UpdateLayout();

            var found = VisualTreeSearch.Descendants<Image>(root.Root).ToList();

            Assert.Equal(2, found.Count);
            Assert.Contains(outer, found);
            Assert.Contains(inner, found);
        });

        /// <summary>Чужие типы не подбираются: искали картинки — получили картинки.</summary>
        [Fact]
        public void ЭлементыДругогоТипаНеПопадают() => UiThread.Run(() => {
            using var root = new OffscreenVisualRoot();
            root.Add(new TextBlock { Text = "не картинка" });
            root.Root.UpdateLayout();

            Assert.Empty(VisualTreeSearch.Descendants<Image>(root.Root));
        });

        /// <summary>Искать не в чем — не повод падать: вызов приходит и до отрисовки списка.</summary>
        [Fact]
        public void ПустоеДеревоНеПриводитКОшибке() {
            Assert.Empty(VisualTreeSearch.Descendants<Image>(null));
        }
    }
}
