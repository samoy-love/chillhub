// <copyright file="BottomBarWatch.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
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
    /// Отдельным классом, потому что забытое свойство не падает и не пишется в лог —
    /// оно молча оставляет строку пустоты внизу экрана. Здесь список наблюдаемого
    /// виден целиком, и его проверяют тесты, а не глаз.
    /// </para>
    /// </summary>
    internal sealed class BottomBarWatch : IDisposable {
        private readonly List<(DependencyPropertyDescriptor Descriptor, object Target)> watched = new();
        private readonly EventHandler handler;
        private bool disposed;

        private BottomBarWatch(EventHandler handler) => this.handler = handler;

        /// <summary>
        /// Подписывается на строку состояния и на полосу выполнения.
        /// </summary>
        /// <param name="status">Строка состояния.</param>
        /// <param name="progress">Полоса выполнения.</param>
        /// <param name="onChange">Что звать при любом изменении.</param>
        /// <returns>Подписка; освободить — отписаться.</returns>
        internal static BottomBarWatch Attach(TextBlock status, ProgressBar progress, EventHandler onChange) {
            var watch = new BottomBarWatch(onChange);
            watch.Add(TextBlock.TextProperty.Name, typeof(TextBlock), status);
            watch.Add(RangeBase.ValueProperty.Name, typeof(ProgressBar), progress);
            watch.Add(ProgressBar.IsIndeterminateProperty.Name, typeof(ProgressBar), progress);
            return watch;
        }

        /// <summary>
        /// Снимает подписки. Обязательна: <c>AddValueChanged</c> заводит сильную ссылку
        /// на контрол, и без снятия страница не собиралась бы сборщиком мусора.
        /// </summary>
        public void Dispose() {
            if (this.disposed) {
                return;
            }

            this.disposed = true;
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
