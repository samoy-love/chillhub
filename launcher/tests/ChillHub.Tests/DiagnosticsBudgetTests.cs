// <copyright file="DiagnosticsBudgetTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Text;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Ограничение размера диагностического пакета.
    /// <para>
    /// Лимит существует не ради экономии: сервер отвергает тело запроса целиком, если оно
    /// превышает его бюджет. Пакет, вылезший за предел, не обрезается по дороге — отчёт
    /// пропадает вместе с ним, причём ровно тогда, когда он нужнее всего.
    /// </para>
    /// </summary>
    public class DiagnosticsBudgetTests {
        /// <summary>Текст, укладывающийся в бюджет, не должен меняться вообще.</summary>
        [Fact]
        public void КороткийТекстНеТрогается() {
            const string text = "# Пакет\nвсё помещается";
            Assert.Equal(text, Diagnostics.TrimToBudget(text, 1024));
        }

        /// <summary>Результат обязан укладываться в бюджет — иначе смысла в обрезке нет.</summary>
        [Theory]
        [InlineData(200)]
        [InlineData(1000)]
        [InlineData(4096)]
        public void РезультатУкладываетсяВБюджет(int budget) {
            var text = new string('я', 20000); // 2 байта на символ в UTF-8
            var trimmed = Diagnostics.TrimToBudget(text, budget);
            Assert.True(
                Encoding.UTF8.GetByteCount(trimmed) <= budget,
                $"вышло {Encoding.UTF8.GetByteCount(trimmed)} байт при бюджете {budget}");
        }

        /// <summary>
        /// Сохраняются ОБА края: в начале конфигурация и версии, в конце самые свежие
        /// записи лога. Вырезается середина, и это должно быть видно.
        /// </summary>
        [Fact]
        public void СохраняютсяНачалоИКонецАСерединаПомечена() {
            var head = "НАЧАЛО-МАРКЕР";
            var tail = "КОНЕЦ-МАРКЕР";
            var text = head + new string('x', 100000) + tail;

            var trimmed = Diagnostics.TrimToBudget(text, 4096);

            Assert.StartsWith(head, trimmed, StringComparison.Ordinal);
            Assert.EndsWith(tail, trimmed, StringComparison.Ordinal);
            Assert.Contains("середина вырезана", trimmed, StringComparison.Ordinal);
        }

        /// <summary>
        /// Суррогатные пары не должны рваться пополам: половина пары — это уже битый текст,
        /// который дальше поедет в JSON.
        /// </summary>
        [Fact]
        public void СуррогатныеПарыНеРвутся() {
            // U+1F600 — четыре байта UTF-8 и две кодовые единицы UTF-16.
            var text = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 5000));

            for (var budget = 64; budget <= 512; budget += 7) {
                var trimmed = Diagnostics.TrimToBudget(text, budget);
                foreach (var ch in trimmed) {
                    // Одиночный суррогат означает разорванную пару.
                    Assert.False(
                        char.IsHighSurrogate(ch) && trimmed.IndexOf(ch) == trimmed.Length - 1,
                        "старший суррогат оказался последним символом — пара разорвана");
                }

                // Кодировщик заменяет битые пары на U+FFFD; если пар не рвали, замен нет.
                var roundTrip = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(trimmed));
                Assert.DoesNotContain('�', roundTrip);
            }
        }
    }
}
