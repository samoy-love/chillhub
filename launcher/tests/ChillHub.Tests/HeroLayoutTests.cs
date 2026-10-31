// <copyright file="HeroLayoutTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Markup;
    using System.Windows.Media;
    using System.Xml.Linq;

    using Xunit;

    /// <summary>Проверяет фон всей витрины в реальной разметке страницы, включая ограничения высоты.</summary>
    public class HeroLayoutTests {
        [Theory]
        [InlineData(960, 530)]
        [InlineData(800, 480)]
        [InlineData(1200, 720)]
        public void ОбложкаЗаполняетБаннерБезВнутреннейРамки(double width, double height) => UiThread.Run(() => {
            var page = LoadPage();
            var banner = (Border)page.FindName("HeroBanner");
            var cover = (System.Windows.Shapes.Rectangle)page.FindName("HeroCoverImg");
            cover.Visibility = Visibility.Visible;
            cover.Fill = Brushes.CornflowerBlue;
            ((TextBlock)page.FindName("HeroTitleText")).Text = "over the hill";
            ((TextBlock)page.FindName("HeroMetaText")).Text = "1 ч в игре";
            ((TextBlock)page.FindName("HeroHintText")).Visibility = Visibility.Collapsed;
            var size = new Size(width, height);
            for (var i = 0; i < 3; i++) {
                page.Measure(size);
                page.Arrange(new Rect(size));
                page.UpdateLayout();
            }

            var bounds = cover.TransformToAncestor(banner).TransformBounds(new Rect(cover.RenderSize));
            Assert.InRange(bounds.Left, 0, 1.1);
            Assert.InRange(bounds.Top, 0, 1.1);
            Assert.InRange(banner.ActualWidth - bounds.Right, 0, 1.1);
            Assert.InRange(banner.ActualHeight - bounds.Bottom, 0, 1.1);
            // Размер прямоугольника сам по себе не гарантирует видимость: WPF мог
            // обрезать весь родительский Grid по внутренним полям карточки.
            Assert.Null(VisualTreeHelper.GetClip(banner.Child));
        });

        internal static Page LoadPage() {
            XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var root = Path.Combine(ChangelogTests.FindRepoRoot(), "launcher", "ChillHub");
            var document = XDocument.Load(Path.Combine(root, "Pages", "HomePage.xaml"));
            document.Root!.Attribute(x + "Class")!.Remove();
            // Загружаем настоящую страницу без сетевых запросов и запуска сервисов.
            var events = new[] { "Click", "SizeChanged", "SelectionChanged", "MouseDoubleClick", "TextChanged", "PreviewKeyDown", "ContextMenuOpening", "ImageFailed" };
            foreach (var attribute in document.Descendants().Attributes().Where(a => events.Contains(a.Name.LocalName)).ToArray()) {
                attribute.Remove();
            }

            var resources = document.Root.Element(w + "Page.Resources")!;
            var app = XDocument.Load(Path.Combine(root, "App.xaml"));
            var dictionary = new XElement(app.Descendants(w + "ResourceDictionary").First());
            dictionary.Add(resources.Elements().ToArray());
            resources.ReplaceNodes(dictionary);
            return (Page)XamlReader.Parse(document.ToString().Replace("clr-namespace:ChillHub.Core.UI", "clr-namespace:ChillHub.Core.UI;assembly=ChillHub", StringComparison.Ordinal));
        }
    }
}
