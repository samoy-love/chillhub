// <copyright file="QueueDoneTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Game;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Что видит игрок, когда позиция очереди кончилась.
    /// <para>
    /// Кончившаяся закачка писала «Готово.» в строку внизу экрана — и панель, которая
    /// показывает ИДУЩУЮ работу, оставалась висеть с отчётом о работе, которой нет.
    /// Здесь проверяется, что конец работы уходит во всплывашку, а строка внизу
    /// достаётся только тому, что человек обязан увидеть и через минуту, — ошибке.
    /// </para>
    /// </summary>
    public class QueueDoneTests {
        /// <summary>Успех: всплывашка с названием игры, строка внизу пустая.</summary>
        [Fact]
        public void УспехГоворитВсплывашкойИОсвобождаетСтроку() {
            var done = QueueDone.For(QueueItemState.Completed, "Risk of Rain 2", "Готово.");

            Assert.Equal("Risk of Rain 2 готова к запуску", done.Toast);
            Assert.Equal(string.Empty, done.Status);
        }

        /// <summary>Без названия всплывашка всё равно осмысленна: молчать о конце работы нельзя.</summary>
        [Fact]
        public void БезНазванияВсплывашкаОстаётся() {
            var done = QueueDone.For(QueueItemState.Completed, "   ", "Готово.");

            Assert.Equal("Игра готова к запуску", done.Toast);
        }

        /// <summary>
        /// Ошибка остаётся в строке внизу: всплывашка живёт секунды, а человек мог
        /// отвернуться от экрана ровно на время закачки.
        /// </summary>
        [Fact]
        public void ОшибкаОстаётсяВСтроке() {
            var done = QueueDone.For(QueueItemState.Failed, "Lethal Company", "Не хватило места на диске.");

            Assert.Equal(string.Empty, done.Toast);
            Assert.Equal("Не хватило места на диске.", done.Status);
        }

        /// <summary>Пустой отказ всё равно называет себя: молчаливая ошибка неотличима от успеха.</summary>
        [Fact]
        public void ОшибкаБезТекстаНазываетСебяСама() {
            var done = QueueDone.For(QueueItemState.Failed, "PEAK", " ");

            Assert.Equal("Не удалось завершить операцию.", done.Status);
        }

        /// <summary>Снятую с очереди позицию снял сам человек — говорить ему об этом нечего.</summary>
        [Fact]
        public void СнятаяСОчередиМолчит() {
            var done = QueueDone.For(QueueItemState.Cancelled, "PEAK", "Снята из очереди.");

            Assert.Equal(string.Empty, done.Toast);
            Assert.Equal(string.Empty, done.Status);
        }
    }
}
