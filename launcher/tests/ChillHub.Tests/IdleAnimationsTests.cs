// <copyright file="IdleAnimationsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Бесконечные анимации разметки не должны идти, когда их некому смотреть.
    /// <para>
    /// Каждая такая анимация — вечный клок, который тикает на UI-потоке каждый кадр
    /// независимо от видимости окна. Скелетоны прячут <c>Visibility.Collapsed</c>, а не
    /// выгрузкой, поэтому по <c>Loaded</c>/<c>Unloaded</c> они не выключались никогда:
    /// спрятанный в трей лаунчер жёг на этом около 2% ядра, свёрнутый в панель задач —
    /// ещё и заметную долю видеокарты.
    /// </para>
    /// </summary>
    public class IdleAnimationsTests : IDisposable {
        /// <inheritdoc/>
        public void Dispose() {
            // Переключатель процессный: оставленный выключенным, он погасил бы анимации
            // в соседних тестах.
            UiAnimations.Instance.Enabled = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>Видимый скелетон пульсирует — иначе секция загрузки выглядит замершей.</summary>
        [Fact]
        public void ВидимыйСкелетонАнимируется() => UiThread.Run(async () => {
            using var root = new OffscreenVisualRoot();
            var skeleton = AddSkeleton(root);

            await UiThread.WaitUntil(() => skeleton.HasAnimatedProperties, "пульс видимого скелетона");
        });

        /// <summary>
        /// Спрятанный скелетон анимацию останавливает. Секции скелетонов прячут целиком,
        /// вместе с родителем, поэтому проверяем именно <c>IsVisible</c>, а не собственную
        /// <c>Visibility</c> элемента.
        /// </summary>
        [Fact]
        public void СпрятаннаяСекцияОстанавливаетПульс() => UiThread.Run(async () => {
            using var root = new OffscreenVisualRoot();
            var section = new StackPanel();
            root.Add(section);
            var skeleton = NewSkeleton();
            section.Children.Add(skeleton);

            await UiThread.WaitUntil(() => skeleton.HasAnimatedProperties, "пульс видимого скелетона");

            section.Visibility = Visibility.Collapsed;
            await UiThread.WaitUntil(() => !skeleton.HasAnimatedProperties, "остановка пульса у спрятанной секции");
        });

        /// <summary>
        /// Свёрнутое в панель задач окно гасит анимации общим переключателем: собственная
        /// <c>IsVisible</c> у такого окна остаётся истинной, и поймать этот случай разметке
        /// больше нечем.
        /// </summary>
        [Fact]
        public void ОбщийВыключательГаситПульс() => UiThread.Run(async () => {
            using var root = new OffscreenVisualRoot();
            var skeleton = AddSkeleton(root);

            await UiThread.WaitUntil(() => skeleton.HasAnimatedProperties, "пульс видимого скелетона");

            UiAnimations.Instance.Enabled = false;
            await UiThread.WaitUntil(() => !skeleton.HasAnimatedProperties, "остановка пульса выключателем");

            UiAnimations.Instance.Enabled = true;
            await UiThread.WaitUntil(() => skeleton.HasAnimatedProperties, "возврат пульса после разворачивания окна");
        });

        /// <summary>
        /// Шиммер карточки и строки списка не только идёт, но и вообще заводится. Анимация
        /// сдвигает <c>RenderTransform.X</c>, а Freezable внутри шаблона запечатывается
        /// вместе с ним: на замороженном преобразовании WPF отвечает «не удается анимировать
        /// в постоянном экземпляре объекта» — и это не тихий сбой, а необработанное
        /// исключение прямо на раскладке главной страницы.
        /// </summary>
        /// <param name="styleKey">Ключ проверяемого стиля скелетона.</param>
        [Theory]
        [InlineData("Style.Skeleton.NewsCard")]
        [InlineData("Style.Skeleton.GameRow")]
        public void ШиммерЗаводитсяИОстанавливается(string styleKey) => UiThread.Run(async () => {
            Exception? unhandled = null;
            void OnUnhandled(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
                unhandled = e.Exception;
                e.Handled = true;
            }

            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += OnUnhandled;
            try {
                using var root = new OffscreenVisualRoot();
                var card = root.Add(new ContentControl {
                    Width = 200,
                    Height = 60,
                    Style = (Style)Theme()[styleKey],
                });
                card.UpdateLayout();

                var shimmer = FindChild<System.Windows.Shapes.Rectangle>(card);
                Assert.NotNull(shimmer);

                // Анимируется не сам прямоугольник, а его преобразование, поэтому и
                // спрашиваем про анимацию именно у него. Заодно это прямая проверка, что
                // преобразование не заморожено: замороженное анимировать нельзя.
                var move = shimmer!.RenderTransform;
                Assert.False(move.IsFrozen, "преобразование шиммера запечатано шаблоном — анимировать его WPF откажется");
                await UiThread.WaitUntil(() => move.HasAnimatedProperties || unhandled != null, "запуск шиммера");
                Assert.Null(unhandled);

                card.Visibility = Visibility.Collapsed;
                await UiThread.WaitUntil(() => !move.HasAnimatedProperties, "остановка шиммера у спрятанной карточки");
                Assert.Null(unhandled);
            }
            finally {
                dispatcher.UnhandledException -= OnUnhandled;
            }
        });

        private static T? FindChild<T>(DependencyObject parent)
            where T : DependencyObject {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++) {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) {
                    return hit;
                }

                var deeper = FindChild<T>(child);
                if (deeper != null) {
                    return deeper;
                }
            }

            return null;
        }

        private static ResourceDictionary Theme() => (ResourceDictionary)Application.LoadComponent(
            new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative));

        private static Border NewSkeleton() {
            var theme = Theme();

            return new Border {
                Width = 120,
                Height = 40,
                Style = (Style)theme["Style.Skeleton"],
            };
        }

        private static Border AddSkeleton(OffscreenVisualRoot root) => root.Add(NewSkeleton());
    }
}
