// <copyright file="KaraokeTickerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// «Караоке»-строка в шапке: счёт символов и времени печати.
    /// <para>
    /// Строка — украшение, но считает она себя тридцать раз в секунду и умеет
    /// испортить лаунчер тремя способами: уйти за границу массива строк, поделить
    /// на ноль при нулевом интервале печати и «догнать» всю строку одним кадром
    /// после сворачивания окна. Все три проверяются здесь, без окна и без таймера —
    /// время подаётся снаружи.
    /// </para>
    /// </summary>
    public class KaraokeTickerTests {
        private static readonly DateTime Start = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Текст разбивается на строки, а хвостовые пробелы уходят.</summary>
        [Fact]
        public void ТекстРазбиваетсяНаСтрокиБезХвостовыхПробелов() {
            var ticker = Ticker("первая   \r\nвторая\t\nтретья");

            Assert.Equal(new[] { "первая", "вторая", "третья" }, ticker.Lines);
        }

        /// <summary>
        /// Пустые строки сохраняются: в песне они работают паузой, и выброшенные
        /// превратили бы её в сплошной поток.
        /// </summary>
        [Fact]
        public void ПустыеСтрокиСохраняютсяКакПаузы() {
            var ticker = Ticker("раз\n\nдва");

            Assert.Equal(new[] { "раз", string.Empty, "два" }, ticker.Lines);
        }

        /// <summary>
        /// Пустой текст не оставляет пустой массив: обращение к нулевой строке
        /// уронило бы шапку окна на первом же тике.
        /// </summary>
        [Fact]
        public void ПустойТекстНеОставляетПустойСписокСтрок() {
            var ticker = Ticker(string.Empty);

            Assert.Single(ticker.Lines);
            Assert.Equal(string.Empty, ticker.CurrentLine);
            Assert.Equal(string.Empty, ticker.NextLine);
        }

        /// <summary>
        /// За последней строкой идёт первая: песня крутится по кругу, и выход
        /// за границу массива тут был бы падением, а не концом песни.
        /// </summary>
        [Fact]
        public void ПослеПоследнейСтрокиИдётПервая() {
            var ticker = Ticker("раз\nдва");
            ticker.ResetToStart(Start);

            Assert.Equal("два", ticker.NextLine);
            ticker.MoveToNextLine(Start);

            Assert.Equal("два", ticker.CurrentLine);
            Assert.Equal("раз", ticker.NextLine);
        }

        /// <summary>
        /// Печать идёт по времени, а не по числу тиков: под нагрузкой тики приходят
        /// реже, и посимвольная привязка к ним растягивала бы строку вдвое.
        /// </summary>
        [Fact]
        public void ДоНаступленияИнтервалаПечататьНечего() {
            var ticker = Ticker("привет");
            ticker.ResetToStart(Start);

            var interval = ticker.Config.CharIntervalMs;
            Assert.Equal(0, ticker.PlanAdvance(Start.AddMilliseconds(interval - 1)));
            Assert.Equal(1, ticker.PlanAdvance(Start.AddMilliseconds(interval)));
        }

        /// <summary>
        /// Долгая пауза между тиками не должна выплюнуть всю строку разом: потолок
        /// на тик и держит ощущение печати, ради которого строка вообще нужна.
        /// </summary>
        [Fact]
        public void ЗастрявшийТикНеВыплёвываетСтрокуЦеликом() {
            var config = new KaraokeConfig();
            var ticker = Ticker("очень длинная строка");
            ticker.ResetToStart(Start);

            // Полминуты простоя — это сотни символов по темпу печати. Наружу выходит
            // потолок и ни символом больше, каким бы он ни был.
            Assert.Equal(config.MaxAdvanceCharsPerTick, ticker.PlanAdvance(Start.AddSeconds(30)));
            Assert.InRange(config.MaxAdvanceCharsPerTick, 1, 3);
        }

        /// <summary>Нулевой интервал печати не должен давать деления на ноль.</summary>
        [Fact]
        public void НулевойИнтервалПечатиНеДаётДеленияНаНоль() {
            var ticker = new KaraokeTicker(new KaraokeConfig { CharIntervalMs = 0, MaxAdvanceCharsPerTick = 3 });
            ticker.SetLyrics("текст");
            ticker.ResetToStart(Start);

            Assert.Equal(3, ticker.PlanAdvance(Start.AddMilliseconds(5)));
        }

        /// <summary>Напечатанное копится символ за символом и не выходит за длину строки.</summary>
        [Fact]
        public void НапечатанноеНеВыходитЗаДлинуСтроки() {
            var ticker = Ticker("абв");
            ticker.ResetToStart(Start);

            Assert.Equal("а", ticker.Type(1));
            Assert.Equal("абв", ticker.Type(10));
            Assert.True(ticker.LineComplete);
            Assert.Equal("абв", ticker.TypedText);
        }

        /// <summary>
        /// Строка считается дописанной только на последнем символе: раньше времени
        /// объявленный конец обрубал бы каждую строку песни.
        /// </summary>
        [Fact]
        public void СтрокаСчитаетсяДописаннойТолькоНаПоследнемСимволе() {
            var ticker = Ticker("аб");
            ticker.ResetToStart(Start);

            Assert.False(ticker.LineComplete);
            ticker.Type(1);
            Assert.False(ticker.LineComplete);
            ticker.Type(1);
            Assert.True(ticker.LineComplete);
        }

        /// <summary>
        /// Пустая строка дописана сразу — иначе песня встала бы навсегда на первой же
        /// паузе между куплетами.
        /// </summary>
        [Fact]
        public void ПустаяСтрокаСчитаетсяДописаннойСразу() {
            var ticker = Ticker("\nвторая");
            ticker.ResetToStart(Start);

            Assert.Equal(string.Empty, ticker.CurrentLine);
            Assert.True(ticker.LineComplete);
        }

        /// <summary>
        /// Отставание списывается ровно на потраченное время, а не «под ноль»:
        /// иначе накопленный долг обнулялся бы каждый тик и печать шла бы медленнее заданной.
        /// </summary>
        [Fact]
        public void ОтставаниеСписываетсяТолькоНаПотраченноеВремя() {
            var ticker = Ticker("абвг");
            ticker.ResetToStart(Start);

            // Прошло четыре интервала, напечатали один символ — долг в три интервала остаётся,
            // и следующий тик берёт из него ровно столько, сколько разрешает потолок.
            ticker.Type(1);
            ticker.CommitProgress(1);

            Assert.Equal(2, ticker.PlanAdvance(Start.AddMilliseconds(180)));
            ticker.Type(2);
            ticker.CommitProgress(2);
            Assert.Equal(1, ticker.PlanAdvance(Start.AddMilliseconds(180)));
        }

        /// <summary>
        /// Свёрнутое окно — это пауза. При возобновлении строка не должна «догонять»
        /// сразу всё пропущенное: человек вернулся к окну и видит рывок вместо печати.
        /// </summary>
        [Fact]
        public void ПаузаНеДаётДогнатьВсёПропущенное() {
            var ticker = Ticker("длинная строка для печати");
            ticker.ResetToStart(Start);

            ticker.BeginPause(Start);
            ticker.EndPause(Start.AddMinutes(10));

            Assert.Equal(0, ticker.PlanAdvance(Start.AddMinutes(10)));
            Assert.Equal(1, ticker.PlanAdvance(Start.AddMinutes(10).AddMilliseconds(60)));
        }

        /// <summary>
        /// Повторное начало паузы её не удлиняет: окно шлёт и «свернули», и «потеряло
        /// фокус» — засчитав обе, лаунчер сдвинул бы отсчёт вдвое.
        /// </summary>
        [Fact]
        public void ПовторноеНачалоПаузыЕёНеУдлиняет() {
            var ticker = Ticker("строка");
            ticker.ResetToStart(Start);

            ticker.BeginPause(Start);
            ticker.BeginPause(Start.AddSeconds(5));
            ticker.EndPause(Start.AddSeconds(10));

            Assert.Equal(0, ticker.PlanAdvance(Start.AddSeconds(10)));
        }

        /// <summary>Конец паузы, которой не было, ничего не сдвигает.</summary>
        [Fact]
        public void КонецНесуществовавшейПаузыНичегоНеСдвигает() {
            var ticker = Ticker("строка");
            ticker.ResetToStart(Start);

            ticker.EndPause(Start.AddSeconds(10));

            Assert.Equal(1, ticker.PlanAdvance(Start.AddMilliseconds(60)));
        }

        /// <summary>
        /// После старта первый же тик обязан что-то напечатать: пустая шапка в первые
        /// шестьдесят миллисекунд выглядит как незагрузившийся интерфейс.
        /// </summary>
        [Fact]
        public void ПервыйТикПослеСтартаСразуПечатает() {
            var ticker = Ticker("строка");
            ticker.ResetToStart(Start);

            ticker.BackdateForFirstChar(Start);

            Assert.Equal(1, ticker.PlanAdvance(Start));
        }

        /// <summary>Переход к следующей строке начинает её с нуля символов.</summary>
        [Fact]
        public void ПереходКСледующейСтрокеНачинаетЕёСНуля() {
            var ticker = Ticker("аб\nвг");
            ticker.ResetToStart(Start);
            ticker.Type(2);

            ticker.MoveToNextLine(Start.AddSeconds(1));

            Assert.Equal("вг", ticker.CurrentLine);
            Assert.Equal(string.Empty, ticker.TypedText);
            Assert.False(ticker.LineComplete);
        }

        /// <summary>
        /// Ширина контейнера строки зажата: без нижней границы шапка схлопывается
        /// в точку, без верхней — выдавливает кнопки за край окна.
        /// </summary>
        [Theory]
        [InlineData(0.0, 0.0, 260.0)]
        [InlineData(100.0, 16.0, 260.0)]
        [InlineData(400.0, 16.0, 428.0)]
        [InlineData(5000.0, 16.0, 800.0)]
        public void ШиринаКонтейнераСтрокиЗажатаВГраницы(double measured, double padding, double expected)
            => Assert.Equal(expected, KaraokeTicker.HostWidth(measured, padding));

        private static KaraokeTicker Ticker(string lyrics) {
            var ticker = new KaraokeTicker(new KaraokeConfig());
            ticker.SetLyrics(lyrics);
            return ticker;
        }
    }
}
