// <copyright file="ChangelogGateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;

    using ChillHub.Core.Changelog;

    using Xunit;

    /// <summary>
    /// Когда окно «Что нового» открывается само.
    /// <para>
    /// Обещание пользователю простое: один раз после обновления и ни разу больше.
    /// Ошибка в любую сторону заметна сразу — либо список всплывает при каждом
    /// запуске, либо не появляется вовсе, — а живым окном это не проверить.
    /// </para>
    /// </summary>
    public class ChangelogGateTests {
        private static readonly IReadOnlyList<ChangelogRelease> Releases = new[] {
            new ChangelogRelease { Version = "1.7.0", Date = "2026-09-01", Changes = new[] { "Что-то изменилось." } },
        };

        [Fact]
        public void ПоказываемПослеОбновления() {
            Assert.True(ChangelogGate.ShouldShow("1.6.25", "1.7.0", Releases));
        }

        [Fact]
        public void НеПоказываемВторойРазНаТойЖеВерсии() {
            Assert.False(ChangelogGate.ShouldShow("1.7.0", "1.7.0", Releases));
        }

        [Fact]
        public void НеПоказываемПослеОткатаНаСтаруюСборку() {
            Assert.False(ChangelogGate.ShouldShow("1.7.0", "1.6.25", Releases));
        }

        [Fact]
        public void ПоказываемКогдаОтметкиЕщёНет() {
            Assert.True(ChangelogGate.ShouldShow(null, "1.7.0", Releases));
            Assert.True(ChangelogGate.ShouldShow(string.Empty, "1.7.0", Releases));
        }

        /// <summary>
        /// Версия не 1.10 &lt; 1.9: сравнение числовое, иначе после 1.6.9 окно
        /// перестало бы открываться до самой 1.7.
        /// </summary>
        [Fact]
        public void СравниваемВерсииЧислами() {
            Assert.True(ChangelogGate.ShouldShow("1.6.9", "1.6.10", Releases));
            Assert.False(ChangelogGate.ShouldShow("1.6.10", "1.6.9", Releases));
        }

        [Fact]
        public void БезВерсииНеПоказываем() {
            Assert.False(ChangelogGate.ShouldShow("1.6.25", string.Empty, Releases));
            Assert.False(ChangelogGate.ShouldShow("1.6.25", null, Releases));
        }

        [Fact]
        public void ПустойСписокНеПоказываем() {
            Assert.False(ChangelogGate.ShouldShow(string.Empty, "1.7.0", System.Array.Empty<ChangelogRelease>()));
            Assert.False(ChangelogGate.ShouldShow(string.Empty, "1.7.0", null));
        }

        [Fact]
        public void ДатаПоказываетсяПоРусски() {
            var release = new ChangelogRelease { Version = "1.7.0", Date = "2026-09-01", Changes = new[] { "Что-то." } };

            Assert.Equal("1 сентября 2026", release.DateText);
        }
    }
}
