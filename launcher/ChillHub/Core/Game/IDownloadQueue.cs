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
    /// <param name="BytesPerSecond">
    /// Сглаженная скорость закачки. Нужна не для красоты: на сборке в 16 ГБ полоса прогресса
    /// шевелится незаметно, и без чисел работающая закачка неотличима от зависшей.
    /// 0 — скорость ещё не измерена.
    /// </param>
    /// <param name="CanMoveUp">
    /// Позицию есть куда поднять. Кнопке нужен именно признак, а не догадка по состоянию:
    /// стрелки, которые нарисованы и нажимаются, но ничего не делают, читаются как
    /// сломанные — ровно это и было, пока перестановка шла только среди ожидающих.
    /// </param>
    /// <param name="CanMoveDown">Позицию есть куда опустить (ниже стоит другая ожидающая).</param>
    /// <param name="IconUrl">
    /// Иконка игры — та же, что в списке слева. Карточка очереди отличалась от строки
    /// списка только отсутствием картинки, и одну и ту же игру в двух местах экрана
    /// приходилось сопоставлять по названию.
    /// </param>
    /// <param name="QueuePosition">
    /// Номер позиции в очереди, начиная с 1. Ожидающая карточка сообщала только «Ждёт
    /// очереди…» — из трёх одинаковых надписей нельзя было понять, какая пойдёт следующей.
    /// </param>
    /// <summary>
    /// Что очередь делает с игрой.
    /// <para>
    /// ПРОВЕРКА ФАЙЛОВ — ТАКАЯ ЖЕ ДОЛГАЯ РАБОТА, КАК ЗАКАЧКА. Она читает и хеширует
    /// десятки гигабайт, а потом докачивает недостающее. Пока она шла мимо очереди,
    /// уход со страницы игры её обрывал, в панели загрузок её не было видно, а
    /// запущенная второй раз она шла параллельно первой по тем же файлам.
    /// </para>
    /// </summary>
    internal enum QueueTaskKind {
        /// <summary>Установка или обновление: скачать то, чего не хватает.</summary>
        Download,

        /// <summary>Проверка файлов: пересчитать хеши и починить расхождения.</summary>
        Verify,
    }

    internal sealed record QueueItem(
        string GameId,
        string Title,
        QueueItemState State,
        long BytesDownloaded,
        long TotalBytes,
        string StatusText,
        double BytesPerSecond = 0,
        bool CanMoveUp = false,
        bool CanMoveDown = false,
        string IconUrl = "",
        int QueuePosition = 0,
        QueueTaskKind Kind = QueueTaskKind.Download);

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

        /// <summary>
        /// Порядок ожидающих позиций изменился (см. <see cref="MoveUp"/>/<see cref="MoveDown"/>) —
        /// несёт полный новый снимок, а не одну позицию: перестановка меняет относительный
        /// порядок сразу двух записей.
        /// </summary>
        event Action<IReadOnlyList<QueueItem>>? Reordered;

        /// <summary>Текущее содержимое очереди — для первичной отрисовки списком/панелью.</summary>
        /// <returns>Снимок очереди на момент вызова.</returns>
        IReadOnlyList<QueueItem> Snapshot();

        /// <summary>
        /// Ставит игру в очередь на установку/обновление. Не делает ничего, если игра уже
        /// установлена и совпадает с последней версией, либо уже стоит в очереди.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>True, если игру действительно поставили в очередь.</returns>
        bool Enqueue(string gameId, QueueTaskKind kind = QueueTaskKind.Download);

        /// <summary>
        /// Убирает игру из очереди. Позицию, которая уже качается, помечает отменённой —
        /// сама загрузка останавливается по токену отмены, а не обрывается на месте.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>True, если такая позиция была найдена.</returns>
        bool Remove(string gameId);

        /// <summary>
        /// Сдвигает ожидающую позицию на один шаг раньше в очереди — меняет местами с
        /// предыдущей ожидающей позицией. Позицию, которая уже качается, не трогает: её
        /// место всегда первое.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>True, если позицию удалось сдвинуть.</returns>
        bool MoveUp(string gameId);

        /// <summary>Сдвигает ожидающую позицию на один шаг позже — см. <see cref="MoveUp"/>.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>True, если позицию удалось сдвинуть.</returns>
        bool MoveDown(string gameId);
    }
}
