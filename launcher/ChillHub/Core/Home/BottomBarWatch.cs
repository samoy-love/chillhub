// <copyright file="BottomBarWatch.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;

    /// <summary>
    /// Подписка на всё, от чего зависит вид нижней панели.
    /// <para>
    /// Панель пересчитывается не по вызовам, а по свойствам: статус пишут два десятка
    /// мест по всей странице, и обходить каждое — значит однажды пропустить одно.
    /// Ровно это и случилось с бегунком: слушали текст и значение полосы, а гасят её
    /// через <see cref="ProgressBar.IsIndeterminate"/> — почти всегда в finally, где
    /// значение уже ноль. Ни одна подписка не срабатывала, и панель оставалась висеть
    /// пустой строкой до следующей закачки.
    /// </para>
    /// <para>
    /// Подписка живёт ровно столько, сколько страница на экране, и восстанавливается при
    /// возврате на неё. Одноразовая — заведённая в конструкторе и снятая по первому
    /// <c>Unloaded</c> — после первого же захода в игру или новость умирала навсегда:
    /// панель замирала в том виде, в каком её застал уход со страницы, и после удаления
    /// игры внизу оставалось «Готово» под панелью, которой полагалось исчезнуть.
    /// </para>
    /// <para>
    /// Отдельным классом, потому что забытое свойство не падает и не пишется в лог —
    /// оно молча оставляет строку пустоты внизу экрана. Здесь список наблюдаемого
    /// виден целиком, и его проверяют тесты, а не глаз.
    /// </para>
    /// </summary>
    internal sealed class BottomBarWatch : IDisposable {
        private readonly List<(DependencyPropertyDescriptor Descriptor, object Target)> watched = new();
        private readonly TextBlock status;
        private readonly ProgressBar progress;
        private readonly EventHandler handler;
        private FrameworkElement? page;
        private bool disposed;

        private BottomBarWatch(TextBlock status, ProgressBar progress, EventHandler handler) {
            this.status = status;
            this.progress = progress;
            this.handler = handler;
        }

        /// <summary>
        /// Подписывается на строку состояния и на полосу выполнения.
        /// </summary>
        /// <param name="status">Строка состояния.</param>
        /// <param name="progress">Полоса выполнения.</param>
        /// <param name="onChange">Что звать при любом изменении.</param>
        /// <returns>Подписка; освободить — отписаться.</returns>
        internal static BottomBarWatch Attach(TextBlock status, ProgressBar progress, EventHandler onChange) {
            var watch = new BottomBarWatch(status, progress, onChange);
            watch.Subscribe();
            return watch;
        }

        /// <summary>
        /// То же самое, но привязанное к жизни страницы: пока страница на экране —
        /// подписка есть, ушли со страницы — снята, вернулись — заведена заново.
        /// <para>
        /// Держать её всё время нельзя: <c>AddValueChanged</c> заводит сильную ссылку на
        /// контрол. Заводить один раз — тоже: после ухода со страницы панель переставала
        /// пересчитываться до конца работы лаунчера.
        /// </para>
        /// </summary>
        /// <param name="page">Страница, которой принадлежит панель.</param>
        /// <param name="status">Строка состояния.</param>
        /// <param name="progress">Полоса выполнения.</param>
        /// <param name="onChange">Что звать при любом изменении.</param>
        /// <returns>Подписка; освободить — отписаться совсем.</returns>
        internal static BottomBarWatch Follow(
            FrameworkElement page, TextBlock status, ProgressBar progress, EventHandler onChange) {
            var watch = Attach(status, progress, onChange);
            watch.page = page;
            page.Loaded += watch.OnPageLoaded;
            page.Unloaded += watch.OnPageUnloaded;
            return watch;
        }

        /// <summary>
        /// Снимает подписки насовсем. <c>AddValueChanged</c> заводит сильную ссылку
        /// на контрол, и без снятия страница не собиралась бы сборщиком мусора.
        /// </summary>
        public void Dispose() {
            if (this.disposed) {
                return;
            }

            this.disposed = true;
            if (this.page != null) {
                this.page.Loaded -= this.OnPageLoaded;
                this.page.Unloaded -= this.OnPageUnloaded;
                this.page = null;
            }

            this.Unsubscribe();
        }

        private void OnPageLoaded(object? sender, RoutedEventArgs e) {
            // Проверять disposed здесь незачем: освобождение снимает и эти два
            // обработчика — после него страница о подписке уже не знает.
            this.Subscribe();

            // Пока страницы не было на экране, статус и полоса менялись без свидетелей:
            // панель обязана догнать их сразу, а не ждать следующего изменения.
            this.handler(this, EventArgs.Empty);
        }

        private void OnPageUnloaded(object? sender, RoutedEventArgs e) => this.Unsubscribe();

        private void Subscribe() {
            if (this.watched.Count > 0) {
                return;
            }

            this.Add(TextBlock.TextProperty.Name, typeof(TextBlock), this.status);
            this.Add(RangeBase.ValueProperty.Name, typeof(ProgressBar), this.progress);
            this.Add(ProgressBar.IsIndeterminateProperty.Name, typeof(ProgressBar), this.progress);
        }

        private void Unsubscribe() {
            foreach (var (descriptor, target) in this.watched) {
                descriptor.RemoveValueChanged(target, this.handler);
            }

            this.watched.Clear();
        }

        private void Add(string property, Type owner, object? target) {
            if (target == null) {
                return;
            }

            var descriptor = DependencyPropertyDescriptor.FromName(property, owner, owner);
            if (descriptor == null) {
                Logging.Logger.Warn($"BottomBarWatch: свойство '{property}' у {owner.Name} не найдено");
                return;
            }

            descriptor.AddValueChanged(target, this.handler);
            this.watched.Add((descriptor, target));
        }
    }
}
