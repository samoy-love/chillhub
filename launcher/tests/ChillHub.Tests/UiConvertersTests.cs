// <copyright file="UiConvertersTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
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
    /// «Установлена» или «Обновление», «Следующая в очереди» или «В очереди · 3-я». Разметка
    /// их только показывает, поэтому проверять смысл надо здесь.
    /// </para>
    /// </summary>
    public class UiConvertersTests {
        /// <summary>Статус игры словом: обновление важнее факта установки.</summary>
        [Theory]
        [InlineData(false, false, "Не установлена")]
        [InlineData(true, false, "Установлена")]
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

        /// <summary>
        /// Метка очереди в строке списка игр: качающаяся — с целым процентом, ждущая —
        /// «В очереди», остальные и отсутствующие — пусто (тогда показывается статус на диске).
        /// </summary>
        [Fact]
        public void МеткаОчередиДляСтрокиСписка() {
            Assert.Equal("Скачивание · 38%", QueueRowLabel.For(Item(QueueItemState.Running, done: 38, total: 100)));
            Assert.Equal("Скачивание", QueueRowLabel.For(Item(QueueItemState.Running)));
            Assert.Equal("В очереди", QueueRowLabel.For(Item(QueueItemState.Waiting, position: 2)));
            Assert.Equal(string.Empty, QueueRowLabel.For(Item(QueueItemState.Completed)));
            Assert.Equal(string.Empty, QueueRowLabel.For(null));
        }

        /// <summary>
        /// Чип загрузок в шапке окна: процент качающейся плюс число ждущих; без качающейся —
        /// только ждущие; пустая очередь — пустая строка, чип прячется.
        /// </summary>
        [Fact]
        public void ЧипЗагрузокВШапке() {
            Assert.Equal(string.Empty, DownloadsChip.Text(Array.Empty<QueueItem>()));
            Assert.Equal("38%", DownloadsChip.Text(new[] { Item(QueueItemState.Running, done: 38, total: 100) }));
            Assert.Equal("38% · ещё 2", DownloadsChip.Text(new[] {
                Item(QueueItemState.Running, done: 38, total: 100),
                Item(QueueItemState.Waiting, position: 2),
                Item(QueueItemState.Waiting, position: 3),
            }));
            Assert.Equal("загрузка", DownloadsChip.Text(new[] { Item(QueueItemState.Running) }));
            Assert.Equal("в очереди 1", DownloadsChip.Text(new[] { Item(QueueItemState.Waiting, position: 1) }));
        }

        /// <summary>
        /// Оставшееся время — словами, а не «13:22»: минуты и часы читаются без догадок,
        /// секунды показываются только когда счёт идёт на секунды.
        /// </summary>
        [Theory]
        [InlineData(40, "40 с")]
        [InlineData(61, "2 мин")]
        [InlineData(13 * 60 + 22, "14 мин")]
        [InlineData(3600 + 5 * 60, "1 ч 05 мин")]
        [InlineData(2 * 86400 + 3 * 3600, "2 дня 3 ч")]
        [InlineData(86400, "1 день")]
        public void ОставшеесяВремяСловами(double seconds, string expected) {
            Assert.Equal(expected, ChillHub.Core.Home.HomeFormat.FormatEta(seconds));
        }

        /// <summary>Метка очереди важнее статуса на диске и красится акцентом; без метки — как раньше.</summary>
        [Fact]
        public void СтрокаСпискаПредпочитаетМеткуОчереди() {
            var game = new GameInfo { IsInstalled = true, NeedsUpdate = true };
            var text = new GameRowStatusTextConverter();
            var brush = new GameRowStatusBrushConverter();

            Assert.Equal("Обновление", text.Convert(new object[] { game, string.Empty }, typeof(string), null!, CultureInfo.InvariantCulture));
            Assert.Equal("Скачивание · 38%", text.Convert(new object[] { game, "Скачивание · 38%" }, typeof(string), null!, CultureInfo.InvariantCulture));

            var idle = (SolidColorBrush)brush.Convert(new object[] { game, string.Empty }, typeof(Brush), null!, CultureInfo.InvariantCulture);
            var busy = (SolidColorBrush)brush.Convert(new object[] { game, "В очереди" }, typeof(Brush), null!, CultureInfo.InvariantCulture);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#E0A64B"), idle.Color);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#7C5CFF"), busy.Color);
        }

        /// <summary>Лента новостей: две колонки только на широком окне.</summary>
        [Theory]
        [InlineData(900.0, 1)]
        [InlineData(1199.0, 1)]
        [InlineData(1200.0, 2)]
        [InlineData(1700.0, 2)]
        [InlineData(double.NaN, 1)]
        public void КолонкиЛентыПоШирине(double width, int expected) {
            Assert.Equal(expected, NewsColumnsConverter.ColumnsFor(width));
        }

        /// <summary>Неполный последний ряд ленты делит ширину между своими карточками.</summary>
        [Theory]
        [InlineData(3, 2, 0, 2)]
        [InlineData(3, 2, 1, 1)]
        [InlineData(4, 2, 1, 2)]
        [InlineData(5, 3, 1, 2)]
        [InlineData(1, 2, 0, 1)]
        public void ПоследнийРядЛентыЗаполняетШирину(int count, int columns, int row, int expected) {
            Assert.Equal(expected, NewsFlowPanel.ItemsInRow(count, columns, row));
        }

        /// <summary>За последним рядом рядов нет — ноль карточек, а не отрицательное число.</summary>
        [Fact]
        public void ЗаПоследнимРядомЛентыПусто() {
            Assert.Equal(0, NewsFlowPanel.ItemsInRow(3, 2, 2));
            Assert.Equal(0, NewsFlowPanel.ItemsInRow(0, 2, 0));
        }

        /// <summary>Док очереди: сколько строк видно на странице такой высоты.</summary>
        [Theory]
        [InlineData(4, 900.0, false, 3)]
        [InlineData(4, 700.0, false, 1)]
        [InlineData(4, 900.0, true, 4)]
        [InlineData(2, 900.0, false, 2)]
        [InlineData(0, 900.0, false, 0)]
        [InlineData(4, double.NaN, false, 3)]
        public void СтрокДокаПоВысотеОкна(int count, double height, bool expanded, int expected) {
            Assert.Equal(expected, QueueDockLayout.Compute(count, height, expanded).VisibleRows);
        }

        /// <summary>Раскрывашка называет, сколько позиций спрятано, и умеет свернуть обратно.</summary>
        [Theory]
        [InlineData(4, 900.0, false, "Показать ещё 1")]
        [InlineData(4, 700.0, false, "Показать ещё 3")]
        [InlineData(4, 900.0, true, "Свернуть очередь")]
        [InlineData(3, 900.0, false, "")]
        [InlineData(1, 700.0, false, "")]
        public void РаскрывашкаДокаНазываетСпрятанное(int count, double height, bool expanded, string expected) {
            Assert.Equal(expected, QueueDockLayout.Compute(count, height, expanded).ToggleText);
        }

        /// <summary>
        /// Док показывает первые строки очереди, а обновление позиции не пересобирает
        /// список: меняется ровно та строка, за которой приехал новый экземпляр.
        /// </summary>
        [Fact]
        public void ДокПравитВидимыеСтрокиПоМесту() {
            var a = Item(QueueItemState.Running);
            var b = Item(QueueItemState.Waiting, position: 2);
            var c = Item(QueueItemState.Waiting, position: 3);
            var source = new List<QueueItem> { a, b, c };
            var visible = new List<QueueItem>();

            QueueDockLayout.ApplyVisible(source, visible, 2);
            Assert.Equal(new[] { a, b }, visible);

            // Тик прогресса: качающаяся позиция приезжает новым объектом, соседка — та же.
            var a2 = Item(QueueItemState.Running, done: 5, total: 10);
            source[0] = a2;
            QueueDockLayout.ApplyVisible(source, visible, 2);
            Assert.Same(a2, visible[0]);
            Assert.Same(b, visible[1]);

            // Очередь укоротилась — лишние строки уходят, оставшиеся не трогаются.
            source.RemoveAt(2);
            QueueDockLayout.ApplyVisible(source, visible, 1);
            Assert.Same(a2, Assert.Single(visible));
        }

        /// <summary>Просить больше строк, чем есть позиций, безопасно: покажем сколько есть.</summary>
        [Fact]
        public void ДокНеПросачиваетсяЗаКонецОчереди() {
            var source = new List<QueueItem> { Item(QueueItemState.Running) };
            var visible = new List<QueueItem>();

            QueueDockLayout.ApplyVisible(source, visible, 5);
            Assert.Single(visible);

            QueueDockLayout.ApplyVisible(source, visible, 0);
            Assert.Empty(visible);
        }

        /// <summary>
        /// Объём и скорость — разными строками: в карточке очереди они стоят одна под
        /// другой в правой колонке, и склеивать их в одну строку больше нечем.
        /// </summary>
        [Fact]
        public void ЦифрыЗакачкиРазложеныПоДвумСтрокам() {
            var item = Item(QueueItemState.Running, done: 5L * 1024 * 1024, total: 20L * 1024 * 1024, speed: 1024 * 1024);

            Assert.Equal("5,0 МБ / 20,0 МБ", Convert(new QueueItemSizeConverter(), item));
            Assert.Equal("1,0 МБ/с · осталось 15 с", Convert(new QueueItemSpeedConverter(), item));
        }

        /// <summary>
        /// Пока скорость неизвестна, второй строки нет вовсе: «0,0 МБ/с» на первых
        /// секундах — не сведения, а шум, и остаток по такой скорости бесконечен.
        /// </summary>
        [Fact]
        public void БезИзвестнойСкоростиВтораяСтрокаПустая() {
            var item = Item(QueueItemState.Running, done: 0, total: 1024);

            Assert.Equal(string.Empty, Convert(new QueueItemSpeedConverter(), item));
            Assert.Equal("0 Б / 1,0 КБ", Convert(new QueueItemSizeConverter(), item));
        }

        /// <summary>Объём неизвестен — цифр нет ни в одной строке, а не «0 / 0».</summary>
        [Fact]
        public void БезИзвестногоОбъёмаЦифрНет() {
            var item = Item(QueueItemState.Running, done: 100, total: 0, speed: 512);

            Assert.Equal(string.Empty, Convert(new QueueItemSizeConverter(), item));
            Assert.Equal(string.Empty, Convert(new QueueItemSpeedConverter(), item));
        }

        private static QueueItem Item(
            QueueItemState state,
            long done = 0,
            long total = 0,
            string status = "",
            int position = 0,
            double speed = 0)
            => new("game", "Игра", state, done, total, status, speed, QueuePosition: position);

        private static string Convert(System.Windows.Data.IValueConverter conv, object? value)
            => (string)conv.Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture);

        private static Color Brush(System.Windows.Data.IValueConverter conv, GameInfo game)
            => ((SolidColorBrush)conv.Convert(game, typeof(Brush), null!, CultureInfo.InvariantCulture)).Color;
    }
}
