// <copyright file="ChangelogMarksTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.Linq;

    using ChillHub.Core.Changelog;

    using Xunit;

    /// <summary>
    /// Какие выпуски в окне отмечаются значком «Новое».
    /// <para>
    /// Ошибка здесь не падает и не пишется в журнал — она просто врёт игроку:
    /// либо новым помечена вся история от 1.0, либо не помечено ничего, и после
    /// обновления через пять версий он листает список наугад.
    /// </para>
    /// </summary>
    public class ChangelogMarksTests {
        [Fact]
        public void ОтмечаемТолькоВышедшееПослеПрошлогоПоказа() {
            var releases = Releases("1.7.0", "1.6.30", "1.6.29", "1.6.28");

            var marked = ChangelogMarks.MarkUnseen(releases, "1.6.29");

            Assert.Equal(2, marked);
            Assert.Equal(new[] { "1.7.0", "1.6.30" }, Unseen(releases));
        }

        /// <summary>
        /// Первый запуск версии, которая вообще умеет вести список: сравнивать не с чем.
        /// Подсветить всю историю от 1.0 значит не выделить ничего — не отмечаем ничего,
        /// а отметку о показе ставит сам показ, и со следующего обновления выделение
        /// заработает по-настоящему.
        /// </summary>
        [Fact]
        public void НаПервомЗапускеНеОтмечаемНичего() {
            var releases = Releases("1.7.0", "1.6.30", "1.6.29");

            Assert.Equal(0, ChangelogMarks.MarkUnseen(releases, string.Empty));
            Assert.Empty(Unseen(releases));

            Assert.Equal(0, ChangelogMarks.MarkUnseen(releases, null));
            Assert.Empty(Unseen(releases));
        }

        [Fact]
        public void ТекущуюВерсиюПовторноНеОтмечаем() {
            var releases = Releases("1.7.0", "1.6.30");

            Assert.Equal(0, ChangelogMarks.MarkUnseen(releases, "1.7.0"));
            Assert.Empty(Unseen(releases));
        }

        /// <summary>
        /// Список в приложении один на весь запуск: открытый второй раз, он не должен
        /// показывать подсветку прошлого открытия.
        /// </summary>
        [Fact]
        public void ПовторнаяОтметкаСнимаетПрошлую() {
            var releases = Releases("1.7.0", "1.6.30", "1.6.29");
            ChangelogMarks.MarkUnseen(releases, "1.6.29");

            ChangelogMarks.MarkUnseen(releases, "1.7.0");

            Assert.Empty(Unseen(releases));
        }

        /// <summary>Сравнение числовое: после 1.6.9 версия 1.6.10 новее, а не старше.</summary>
        [Fact]
        public void СравниваемВерсииЧислами() {
            var releases = Releases("1.6.10", "1.6.9");

            ChangelogMarks.MarkUnseen(releases, "1.6.9");

            Assert.Equal(new[] { "1.6.10" }, Unseen(releases));
        }

        [Fact]
        public void ПустойСписокНеЛомается() {
            Assert.Equal(0, ChangelogMarks.MarkUnseen(null, "1.6.29"));
            Assert.Equal(0, ChangelogMarks.MarkUnseen(System.Array.Empty<ChangelogRelease>(), "1.6.29"));
        }

        private static IReadOnlyList<ChangelogRelease> Releases(params string[] versions)
            => versions
                .Select(v => new ChangelogRelease {
                    Version = v,
                    Date = "2026-08-31",
                    Changes = new[] { "Что-то изменилось." },
                })
                .ToList();

        private static string[] Unseen(IReadOnlyList<ChangelogRelease> releases)
            => releases.Where(r => r.IsNew).Select(r => r.Version).ToArray();
    }
}
