// <copyright file="VersionOrderTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// От порядка версий зависит, какую сборку лаунчер считает последней.
    /// Ошибка не видна глазом: пользователь просто получает старую игру.
    /// </summary>
    public class VersionOrderTests {
        [Theory]
        [InlineData("1.1.10", "1.1.9")]
        [InlineData("1.1.0", "1.0.2")]
        [InlineData("2.0.0", "1.99.99")]
        [InlineData("1.0.10", "1.0.9")]
        [InlineData("1.0.1", "1.0.1-rc")]
        public void Compare_ПерваяВерсияНовее(string newer, string older) {
            Assert.True(VersionOrder.Compare(newer, older) > 0);
            Assert.True(VersionOrder.Compare(older, newer) < 0);
        }

        [Theory]
        [InlineData("1.2", "1.2.0")]
        [InlineData(" 1.2.3 ", "1.2.3")]
        public void Compare_РавныеВерсии(string a, string b) {
            Assert.Equal(0, VersionOrder.Compare(a, b));
        }

        [Fact]
        public void SelectLatest_БерётМаксимум_АНеПервыйЭлемент() {
            var builds = new List<string?> { "1.0.2", "1.1.10", "1.1.9", "1.0.10" };
            Assert.Equal("1.1.10", VersionOrder.SelectLatest(builds));
        }

        [Fact]
        public void SelectLatest_ПустойСписокИNull() {
            Assert.Null(VersionOrder.SelectLatest(null));
            Assert.Null(VersionOrder.SelectLatest(new List<string?>()));
            Assert.Null(VersionOrder.SelectLatest(new List<string?> { null, string.Empty, "  " }));
        }
    }
}
