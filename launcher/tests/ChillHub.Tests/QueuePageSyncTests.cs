// <copyright file="QueuePageSyncTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Сверка страницы игры с очередью загрузок при возврате на страницу.
    /// <para>
    /// Страница отписывается от очереди, пока её не видно. Закачка кончается, пока
    /// открыта новость из журнала изменений, — событие завершения доставить некому. По
    /// «Назад» журнал навигации возвращает ТОТ ЖЕ объект страницы, и пока сверка молча
    /// выходила, не найдя позицию, страница навсегда оставалась в режиме
    /// «Отмена / Обновляется»: кнопка снимала с очереди то, чего в ней уже нет.
    /// </para>
    /// </summary>
    public class QueuePageSyncTests {
        /// <summary>Позиция на месте — страница следует за ней, как и раньше.</summary>
        [Fact]
        public void ПозицияВОчередиОтражаетсяНаСтранице() {
            Assert.Equal(
                QueuePageAction.Follow,
                QueuePageSync.Decide(hasQueueItem: true, isBusy: true, viaQueue: true));
        }

        /// <summary>
        /// Работа была за очередью, а позиции больше нет — значит, она кончилась, пока
        /// страница была скрыта. Выходим из режима работы и перечитываем диск.
        /// </summary>
        [Fact]
        public void ИсчезнувшаяПозицияВыводитСтраницуИзРежимаРаботы() {
            Assert.Equal(
                QueuePageAction.Finish,
                QueuePageSync.Decide(hasQueueItem: false, isBusy: true, viaQueue: true));
        }

        /// <summary>
        /// Своя, не очередная работа страницы (её отменяет уход со страницы) чужого
        /// снимка не касается: трогать её нельзя.
        /// </summary>
        [Fact]
        public void СобственнуюРаботуСтраницыСнимокНеТрогает() {
            Assert.Equal(
                QueuePageAction.None,
                QueuePageSync.Decide(hasQueueItem: false, isBusy: true, viaQueue: false));
        }

        /// <summary>Страница ничем не занята, в очереди её тоже нет — делать нечего.</summary>
        [Fact]
        public void СпокойнойСтраницеДелатьНечего() {
            Assert.Equal(
                QueuePageAction.None,
                QueuePageSync.Decide(hasQueueItem: false, isBusy: false, viaQueue: false));
        }
    }
}
