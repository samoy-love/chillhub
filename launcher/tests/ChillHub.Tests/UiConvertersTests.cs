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

        /// <summary>
        /// Высота витрины считается из ширины по пропорции Steam Library Hero: именно
        /// поэтому обложку можно брать на SteamGridDB как есть.
        /// </summary>
        [Theory]
        [InlineData(1920.0, 620.0)]
        [InlineData(960.0, 310.0)]
        [InlineData(890.0, 287.4)]
        public void ВысотаВитриныДержитПропорциюSteam(double width, double expected) {
            var height = (double)new AspectHeightConverter().Convert(width, typeof(double), null!, CultureInfo.InvariantCulture);

            Assert.Equal(expected, height, 1);
        }

        /// <summary>
        /// Ширины ещё нет — высота Auto, а не ноль: иначе витрина схлопывалась бы
        /// в полоску и мигала на первом проходе разметки.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void БезВнятнойШириныВысотаAuto(double width) {
            var height = (double)new AspectHeightConverter().Convert(width, typeof(double), null!, CultureInfo.InvariantCulture);

            Assert.True(double.IsNaN(height));
        }

        /// <summary>Пропорцию можно задать из разметки — параметром.</summary>
        [Fact]
        public void ПропорциюМожноЗадатьПараметром() {
            var height = (double)new AspectHeightConverter().Convert(1000.0, typeof(double), "0.5", CultureInfo.InvariantCulture);

            Assert.Equal(500.0, height, 1);
        }

        /// <summary>
        /// Предел блока — доля высоты окна: витрина и очередь загрузок делят экран
        /// пропорционально, а не фиксированными пикселями, которые на невысоком окне
        /// вдвоём выдавливали ленту новостей в ноль.
        /// </summary>
        [Fact]
        public void ДоляВысотыОкнаСчитаетсяИзПараметра() {
            var conv = new HeightShareConverter();

            Assert.Equal(272.0, (double)conv.Convert(800.0, typeof(double), "0.34", CultureInfo.InvariantCulture), 1);
        }

        /// <summary>
        /// Пока высота неизвестна, предела нет вовсе: MaxHeight=NaN разметка не принимает,
        /// а PositiveInfinity — штатное «без ограничения».
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void БезВысотыДоляНеОграничивает(double height) {
            var conv = new HeightShareConverter();

            Assert.Equal(double.PositiveInfinity, (double)conv.Convert(height, typeof(double), "0.34", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Наигранное меньше часа — в минутах: «0 ч в игре» после первого запуска
        /// выглядело как отсутствие данных.
        /// </summary>
        [Theory]
        [InlineData(0L, "1 мин")]
        [InlineData(59L, "1 мин")]
        [InlineData(25 * 60L, "25 мин")]
        [InlineData(3600L, "1 ч")]
        [InlineData(142 * 3600L + 1800, "142 ч")]
        public void НаигранноеФорматируетсяЧитаемо(long seconds, string expected) {
            Assert.Equal(expected, PlaytimeStore.FormatTotal(seconds));
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
