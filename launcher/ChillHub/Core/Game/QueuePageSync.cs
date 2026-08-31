// <copyright file="QueuePageSync.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    /// <summary>Что странице игры сделать с собой после сверки с очередью загрузок.</summary>
    internal enum QueuePageAction {
        /// <summary>Ничего: страница и очередь согласны друг с другом.</summary>
        None,

        /// <summary>Отразить позицию: этой игрой очередь занята прямо сейчас.</summary>
        Follow,

        /// <summary>Позиции больше нет — выйти из режима работы и перечитать состояние с диска.</summary>
        Finish,
    }

    /// <summary>
    /// Сверка страницы игры с очередью загрузок.
    /// <para>
    /// ОТСУТСТВИЕ ПОЗИЦИИ — ТОЖЕ НОВОСТЬ. Страница отписывается от очереди, пока её не
    /// видно, и события завершения ей никто не доставляет: закачка кончалась, пока была
    /// открыта новость из журнала изменений, а по «Назад» журнал навигации возвращал тот
    /// же объект страницы. Сверка молча выходила, не найдя позицию, и страница навсегда
    /// оставалась в режиме «Отмена / Обновляется» — кнопка снимала с очереди то, чего в
    /// ней уже нет, и не меняла ничего.
    /// </para>
    /// </summary>
    internal static class QueuePageSync {
        /// <summary>Что делать странице по итогам сверки.</summary>
        /// <param name="hasQueueItem">Игра нашлась в снимке очереди.</param>
        /// <param name="isBusy">Страница считает, что работа идёт.</param>
        /// <param name="viaQueue">Работа принадлежит очереди, а не самой странице.</param>
        /// <returns>Что делать со своим состоянием.</returns>
        internal static QueuePageAction Decide(bool hasQueueItem, bool isBusy, bool viaQueue) {
            if (hasQueueItem) {
                return QueuePageAction.Follow;
            }

            return isBusy && viaQueue ? QueuePageAction.Finish : QueuePageAction.None;
        }
    }
}
