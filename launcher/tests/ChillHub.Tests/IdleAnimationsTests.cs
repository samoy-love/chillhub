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

        private static Border NewSkeleton() {
            var theme = (ResourceDictionary)Application.LoadComponent(
                new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative));

            return new Border {
                Width = 120,
                Height = 40,
                Style = (Style)theme["Style.Skeleton"],
            };
        }

        private static Border AddSkeleton(OffscreenVisualRoot root) => root.Add(NewSkeleton());
    }
}
