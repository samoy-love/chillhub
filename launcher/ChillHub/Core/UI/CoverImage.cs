// <copyright file="CoverImage.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core.Home;

    /// <summary>
    /// Адрес картинки, которую должен показывать <see cref="Image"/>.
    /// <para>
    /// КАРТИНКА ОБЯЗАНА СЛЕДОВАТЬ ЗА СВОЕЙ СТРОКОЙ. Раньше загрузку заводил обработчик
    /// <c>Loaded</c>, а адрес приезжал привязкой в <c>Source</c> и <c>Tag</c>. Обе половины
    /// этой схемы ломались об одно и то же — переиспользование строки списка:
    /// </para>
    /// <para>
    /// WPF при замене элемента коллекции (<c>list[i] = other</c> — ровно это делает
    /// <see cref="QueueDockLayout.ApplyVisible"/>, когда меняется порядок очереди
    /// загрузок) НЕ пересоздаёт строку, а подставляет ей новые данные. Тот же самый
    /// <see cref="Image"/> остаётся на месте, <c>Loaded</c> второй раз не приходит — и
    /// загрузка новой картинки не начинается никогда.
    /// </para>
    /// <para>
    /// А привязка <c>Source</c> к этому моменту уже мертва: загрузчик кладёт готовую
    /// картинку в <c>Source</c> присваиванием, а локальное значение вытесняет
    /// одностороннюю привязку насовсем. Показанной так и остаётся картинка прошлой игры —
    /// в очереди загрузок у PEAK стоял значок Drive Beyond Horizons.
    /// </para>
    /// <para>
    /// Присоединённое свойство закрывает обе дыры разом: смена данных строки меняет его
    /// значение, а изменение значения — это и есть команда загрузить. Никакого
    /// <c>Loaded</c>, никакой привязки, которую можно затереть.
    /// </para>
    /// </summary>
    public static class CoverImage {
        /// <summary>Адрес картинки; смена значения запускает загрузку.</summary>
        public static readonly DependencyProperty UrlProperty =
            DependencyProperty.RegisterAttached(
                "Url",
                typeof(string),
                typeof(CoverImage),
                new PropertyMetadata(null, OnUrlChanged));

        /// <summary>Задаёт адрес картинки элемента.</summary>
        /// <param name="element">Элемент — <see cref="Image"/>.</param>
        /// <param name="value">Адрес; пусто — картинки нет.</param>
        public static void SetUrl(DependencyObject element, string? value) {
            element?.SetValue(UrlProperty, value);
        }

        /// <summary>Возвращает адрес картинки элемента.</summary>
        /// <param name="element">Элемент — <see cref="Image"/>.</param>
        /// <returns>Адрес или null.</returns>
        public static string? GetUrl(DependencyObject element) =>
            element?.GetValue(UrlProperty) as string;

        private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            if (d is not Image img) {
                return;
            }

            // Строке подставили другую игру — значит, и картинку надо другую. Прежняя
            // остаётся на месте лишь до тех пор, пока новая не приедет: мигание пустотой
            // в списке, который просто переставили, хуже секунды со старым значком.
            ImageLoader.Load(img, e.NewValue as string, ConfigService.Current.ApiBaseUrl);
        }
    }
}
