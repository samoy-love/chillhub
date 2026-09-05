// <copyright file="QueueDone.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using ChillHub.Core.Game;

    /// <summary>
    /// Что сказать игроку, когда позиция очереди кончилась.
    /// </summary>
    /// <param name="Toast">Всплывашка; пусто — молчим.</param>
    /// <param name="Status">Строка нижней панели; пусто — панель уходит с экрана.</param>
    internal readonly record struct QueueDoneReport(string Toast, string Status);

    /// <summary>
    /// Конец работы очереди — всплывашкой, а не строкой внизу экрана.
    /// <para>
    /// Нижняя панель показывает ИДУЩУЮ работу и уходит с экрана, когда её нет.
    /// Кончившаяся закачка писала туда «Готово.» — и панель оставалась висеть с
    /// отчётом о работе, которой нет, до следующего изменения статуса. Ровно то же
    /// самое про удаление игры говорит всплывашка: она видна секунду и не занимает
    /// экран потом.
    /// </para>
    /// <para>
    /// Успех при этом виден и без слов: карточка из очереди исчезает, в списке
    /// появляется «Установлена», а кнопка витрины становится «Играть». Всплывашка
    /// нужна ровно затем, чтобы конец долгой работы был замечен человеком, который
    /// в этот момент смотрел в другое окно.
    /// </para>
    /// </summary>
    internal static class QueueDone {
        /// <summary>
        /// Решает, что показать по концу позиции очереди.
        /// </summary>
        /// <param name="state">Чем кончилась позиция.</param>
        /// <param name="title">Название игры.</param>
        /// <param name="statusText">Последняя строка состояния позиции.</param>
        /// <param name="kind">Качали или проверяли.</param>
        /// <returns>Что сказать.</returns>
        internal static QueueDoneReport For(
            QueueItemState state, string? title, string? statusText, QueueTaskKind kind = QueueTaskKind.Download) {
            var name = string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();

            // Проверка кончается своими словами. «Готова к запуску» после проверки
            // читается как «мы её тебе поставили», хотя человек просил только сверить
            // файлы, и на диске ничего не изменилось.
            if (state == QueueItemState.Completed && kind == QueueTaskKind.Verify) {
                return new QueueDoneReport("Файлы проверены, всё на месте", string.Empty);
            }

            return state switch {
                // Успех. «Готова к запуску» вместо «Готово»: одно слово о конце работы
                // не говорит, ЧЬЕЙ работы, а всплывашек за долгую очередь бывает
                // несколько подряд.
                QueueItemState.Completed => new QueueDoneReport(
                    name.Length > 0 ? $"{name} готова к запуску" : "Игра готова к запуску",
                    string.Empty),

                // Ошибку человек обязан увидеть, даже отвернувшись: она остаётся в
                // строке внизу, пока её не сменит следующая работа.
                QueueItemState.Failed => new QueueDoneReport(
                    string.Empty,
                    string.IsNullOrWhiteSpace(statusText) ? "Не удалось завершить операцию." : statusText),

                // Снятую с очереди позицию снял сам человек — рассказывать ему об этом нечего.
                _ => new QueueDoneReport(string.Empty, string.Empty),
            };
        }
    }
}
