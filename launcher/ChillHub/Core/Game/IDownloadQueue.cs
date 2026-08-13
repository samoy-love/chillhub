// <copyright file="IDownloadQueue.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;

    /// <summary>Состояние одной позиции в очереди загрузок.</summary>
    internal enum QueueItemState {
        /// <summary>Ждёт своей очереди.</summary>
        Waiting,

        /// <summary>Сейчас качается/устанавливается.</summary>
        Running,

        /// <summary>Завершилась успешно.</summary>
        Completed,

        /// <summary>Завершилась с ошибкой.</summary>
        Failed,

        /// <summary>Снята из очереди до завершения.</summary>
        Cancelled,
    }

    /// <summary>
    /// Снимок одной позиции очереди — то, что видит UI. Неизменяемый: каждое
    /// изменение состояния — это новый экземпляр, переданный через событие.
    /// </summary>
    /// <param name="GameId">Идентификатор игры.</param>
    /// <param name="Title">Заголовок для отображения (на момент постановки в очередь).</param>
    /// <param name="State">Текущее состояние позиции.</param>
    /// <param name="BytesDownloaded">Скачано байт (на момент последнего отчёта).</param>
    /// <param name="TotalBytes">Всего байт по плану (0, пока план не построен).</param>
    /// <param name="StatusText">Короткая строка состояния — то же, что видит одиночная страница игры.</param>
    internal sealed record QueueItem(
        string GameId,
        string Title,
        QueueItemState State,
        long BytesDownloaded,
        long TotalBytes,
        string StatusText);

    /// <summary>
    /// Очередь загрузок игр: то, с чем говорит UI. Реализация — деталь (фаза 1 держит всё
    /// в памяти и качает последовательно; фаза 2 сможет подменить её на версию с диском и
    /// общим лимитером, не трогая ни один вызывающий код).
    /// </summary>
    internal interface IDownloadQueue {
        /// <summary>Позицию добавили в очередь.</summary>
        event Action<QueueItem>? ItemAdded;

        /// <summary>Обновился прогресс позиции, которая уже качается.</summary>
        event Action<QueueItem>? ItemProgress;

        /// <summary>Позиция завершилась — успехом или ошибкой (см. <see cref="QueueItem.State"/>).</summary>
        event Action<QueueItem>? ItemCompleted;

        /// <summary>Позицию убрали из очереди (снята вручную или завершилась/отменилась).</summary>
        event Action<QueueItem>? ItemRemoved;

        /// <summary>Текущее содержимое очереди — для первичной отрисовки списком/панелью.</summary>
        /// <returns>Снимок очереди на момент вызова.</returns>
        IReadOnlyList<QueueItem> Snapshot();

        /// <summary>
        /// Ставит игру в очередь на установку/обновление. Не делает ничего, если игра уже
        /// установлена и совпадает с последней версией, либо уже стоит в очереди.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>True, если игру действительно поставили в очередь.</returns>
        bool Enqueue(string gameId);

        /// <summary>
        /// Убирает игру из очереди. Позицию, которая уже качается, помечает отменённой —
        /// сама загрузка останавливается по токену отмены, а не обрывается на месте.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>True, если такая позиция была найдена.</returns>
        bool Remove(string gameId);
    }
}
