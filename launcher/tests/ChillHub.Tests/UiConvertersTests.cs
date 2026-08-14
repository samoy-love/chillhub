// <copyright file="UiConvertersTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Globalization;
    using System.Windows.Media;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Конвертеры, из которых собраны подписи в списке игр и в очереди загрузок.
    /// <para>
    /// Это единственное место, где решается, что игрок прочитает под названием игры:
    /// «Готова» или «Обновление», «Следующая в очереди» или «В очереди · 3-я». Разметка
    /// их только показывает, поэтому проверять смысл надо здесь.
    /// </para>
    /// </summary>
    public class UiConvertersTests {
        /// <summary>Статус игры словом: обновление важнее факта установки.</summary>
        [Theory]
        [InlineData(false, false, "Не установлена")]
        [InlineData(true, false, "Готова")]
        [InlineData(true, true, "Обновление")]
        [InlineData(false, true, "Обновление")]
        public void СтатусИгрыНазываетсяСловом(bool installed, bool needsUpdate, string expected) {
            var game = new GameInfo { IsInstalled = installed, NeedsUpdate = needsUpdate };

            Assert.Equal(expected, Convert(new GameStatusTextConverter(), game));
        }

        /// <summary>Нет игры — нет и подписи: пустая строка, а не «Не установлена».</summary>
        [Fact]
        public void БезИгрыСтатусПустой() {
            Assert.Equal(string.Empty, Convert(new GameStatusTextConverter(), null));
        }

        /// <summary>Цвет статуса различает три состояния, иначе подпись читалась бы одинаково.</summary>
        [Fact]
        public void ЦветаСтатусовРазличаются() {
            var conv = new GameStatusBrushConverter();
            var ready = Brush(conv, new GameInfo { IsInstalled = true });
            var update = Brush(conv, new GameInfo { IsInstalled = true, NeedsUpdate = true });
            var absent = Brush(conv, new GameInfo());

            Assert.NotEqual(ready, update);
            Assert.NotEqual(ready, absent);
            Assert.NotEqual(update, absent);

            // Не выбранный объект не должен ронять отрисовку строки списка.
            Assert.NotNull(new GameStatusBrushConverter().Convert(null!, typeof(Brush), null!, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Ожидающая позиция называет своё место. Все они писали одно «Ждёт очереди…», и
        /// по трём одинаковым карточкам нельзя было понять, какая пойдёт следующей.
        /// </summary>
        [Theory]
        [InlineData(1, "Следующая в очереди")]
        [InlineData(2, "В очереди · 2-я")]
        [InlineData(7, "В очереди · 7-я")]
        public void ОжидающаяПозицияНазываетСвоёМесто(int position, string expected) {
            var item = Item(QueueItemState.Waiting, position: position);

            Assert.Equal(expected, Convert(new QueueItemStatusConverter(), item));
        }

        /// <summary>У качающейся позиции — её собственный статус, а не место в очереди.</summary>
        [Fact]
        public void КачающаясяПозицияПоказываетСвойСтатус() {
            var item = Item(QueueItemState.Running, status: "Скачивание обновления…");

            Assert.Equal("Скачивание обновления…", Convert(new QueueItemStatusConverter(), item));
        }

        /// <summary>Доля скачанного округляется до целых процентов.</summary>
        [Theory]
        [InlineData(0, 100, "0%")]
        [InlineData(43, 100, "43%")]
        [InlineData(100, 100, "100%")]
        public void ДоляСкачанногоПоказываетсяПроцентами(long done, long total, string expected) {
            var item = Item(QueueItemState.Running, done: done, total: total);

            Assert.Equal(expected, Convert(new QueueItemPercentTextConverter(), item));
        }

        /// <summary>
        /// План ещё не посчитан — процентов нет. Показывать «0%» до того, как известен
        /// объём, значило бы обещать долю, которой ещё не существует.
        /// </summary>
        [Fact]
        public void БезПланаПроцентовНет() {
            Assert.Equal(string.Empty, Convert(new QueueItemPercentTextConverter(), Item(QueueItemState.Running)));
        }

        /// <summary>Чужой объект не роняет отрисовку карточки.</summary>
        [Fact]
        public void ЧужойОбъектДаётПустуюСтроку() {
            Assert.Equal(string.Empty, Convert(new QueueItemStatusConverter(), "не позиция очереди"));
            Assert.Equal(string.Empty, Convert(new QueueItemPercentTextConverter(), 42));
        }

        private static QueueItem Item(
            QueueItemState state,
            long done = 0,
            long total = 0,
            string status = "",
            int position = 0)
            => new("game", "Игра", state, done, total, status, QueuePosition: position);

        private static string Convert(System.Windows.Data.IValueConverter conv, object? value)
            => (string)conv.Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture);

        private static Color Brush(System.Windows.Data.IValueConverter conv, GameInfo game)
            => ((SolidColorBrush)conv.Convert(game, typeof(Brush), null!, CultureInfo.InvariantCulture)).Color;
    }
}
