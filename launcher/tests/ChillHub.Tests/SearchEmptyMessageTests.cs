// <copyright file="SearchEmptyMessageTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Подсказка «поиск ничего не нашёл» — словами, которые читает игрок.
    /// <para>
    /// Раньше список в этом случае просто пустел, и экран не отличался от «каталог не
    /// приехал». Подсказка называет размер каталога, поэтому число в ней склоняется,
    /// а не подставляется как есть: «В каталоге 8 игра» — ровно тот мусор, из-за
    /// которого тексты и вынесены из разметки.
    /// </para>
    /// </summary>
    public class SearchEmptyMessageTests {
        /// <summary>Слово «игра» склоняется по числу, включая одиннадцать и двадцать один.</summary>
        /// <param name="n">Сколько игр в каталоге.</param>
        /// <param name="expected">Ожидаемая форма слова.</param>
        [Theory]
        [InlineData(1, "игра")]
        [InlineData(2, "игры")]
        [InlineData(4, "игры")]
        [InlineData(5, "игр")]
        [InlineData(8, "игр")]
        [InlineData(11, "игр")]
        [InlineData(12, "игр")]
        [InlineData(14, "игр")]
        [InlineData(21, "игра")]
        [InlineData(22, "игры")]
        [InlineData(25, "игр")]
        [InlineData(111, "игр")]
        public void СловоИграСклоняетсяПоЧислу(int n, string expected)
            => Assert.Equal(expected, SearchEmptyMessage.PluralizeGameRu(n));

        /// <summary>Подсказка называет размер каталога и оба выхода из положения.</summary>
        [Fact]
        public void ПодсказкаНазываетРазмерКаталогаИЧтоДелать() {
            var hint = SearchEmptyMessage.Hint(8);

            Assert.Contains("В каталоге 8 игр", hint);
            Assert.Contains("другое слово", hint);
            Assert.Contains("сбросьте поиск", hint);
        }

        /// <summary>
        /// Пустой каталог размером не хвастается: «В каталоге 0 игр» — насмешка, а не
        /// подсказка. Остаётся только совет.
        /// </summary>
        [Fact]
        public void ПриПустомКаталогеЧислоНеНазывается() {
            var hint = SearchEmptyMessage.Hint(0);

            Assert.DoesNotContain("каталоге", hint);
            Assert.Contains("другое слово", hint);
        }

        /// <summary>Заголовок один и тот же, его не собирают на месте.</summary>
        [Fact]
        public void ЗаголовокНеСобираетсяВРазметке()
            => Assert.Equal("Ничего не нашлось", SearchEmptyMessage.Title);
    }
}
