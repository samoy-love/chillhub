// <copyright file="BusyGateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;

    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Индикатор работы, которая может кончиться мгновенно.
    /// <para>
    /// На машине, где проверка файлов занимает пятьдесят миллисекунд, полоса статуса
    /// появлялась и исчезала быстрее, чем её можно прочитать: человек видел вспышку
    /// внизу окна и не понимал, что это было. Здесь проверяется, что вспышки не будет
    /// ни с одной стороны — ни при мгновенной работе, ни при работе, кончившейся сразу
    /// после появления полосы.
    /// </para>
    /// </summary>
    public class BusyGateTests {
        /// <summary>Работа короче порога не показывается вовсе.</summary>
        [Fact]
        public void МгновеннаяРаботаНеПоказывается() {
            var clock = new FakeClock();
            var seen = new List<bool>();
            var gate = new BusyGate(v => seen.Add(v), clock.Now, clock.Schedule);

            gate.Set(true);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            gate.Set(false);
            clock.Advance(TimeSpan.FromSeconds(5));

            Assert.Empty(seen);
            Assert.False(gate.Visible);
        }

        /// <summary>Долгая работа показывается — но не раньше порога.</summary>
        [Fact]
        public void ДолгаяРаботаПоказываетсяПослеПорога() {
            var clock = new FakeClock();
            var seen = new List<bool>();
            var gate = new BusyGate(v => seen.Add(v), clock.Now, clock.Schedule);

            gate.Set(true);
            clock.Advance(TimeSpan.FromMilliseconds(BusyGate.AppearAfterMs - 1));
            Assert.Empty(seen);

            clock.Advance(TimeSpan.FromMilliseconds(2));
            Assert.Equal(new[] { true }, seen);
            Assert.True(gate.Visible);
        }

        /// <summary>
        /// Появившийся индикатор держится минимум: работа, кончившаяся сразу после его
        /// появления, дала бы ту же вспышку, только позже.
        /// </summary>
        [Fact]
        public void ПоявившисьДержитсяМинимум() {
            var clock = new FakeClock();
            var seen = new List<bool>();
            var gate = new BusyGate(v => seen.Add(v), clock.Now, clock.Schedule);

            gate.Set(true);
            clock.Advance(TimeSpan.FromMilliseconds(BusyGate.AppearAfterMs + 1));
            gate.Set(false);

            Assert.Equal(new[] { true }, seen);

            clock.Advance(TimeSpan.FromMilliseconds(BusyGate.MinVisibleMs));
            Assert.Equal(new[] { true, false }, seen);
            Assert.False(gate.Visible);
        }

        /// <summary>Долго провисевший индикатор прячется сразу: минимум уже отработан.</summary>
        [Fact]
        public void ПослеМинимумаПрячетсяСразу() {
            var clock = new FakeClock();
            var seen = new List<bool>();
            var gate = new BusyGate(v => seen.Add(v), clock.Now, clock.Schedule);

            gate.Set(true);
            clock.Advance(TimeSpan.FromSeconds(10));
            gate.Set(false);

            Assert.Equal(new[] { true, false }, seen);
        }

        /// <summary>
        /// «Начали — кончили — начали» не прячет индикатор посреди новой работы: каждая
        /// смена намерения обесценивает отложенные вызовы предыдущей.
        /// </summary>
        [Fact]
        public void НоваяРаботаОтменяетОтложенноеСокрытие() {
            var clock = new FakeClock();
            var seen = new List<bool>();
            var gate = new BusyGate(v => seen.Add(v), clock.Now, clock.Schedule);

            gate.Set(true);
            clock.Advance(TimeSpan.FromMilliseconds(BusyGate.AppearAfterMs + 1));
            gate.Set(false);
            gate.Set(true);
            clock.Advance(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { true }, seen);
            Assert.True(gate.Visible);
        }

        /// <summary>Повторное «работа идёт» ничего не меняет: индикатор один.</summary>
        [Fact]
        public void ПовторноеНачалоНеДублируетПоказ() {
            var clock = new FakeClock();
            var seen = new List<bool>();
            var gate = new BusyGate(v => seen.Add(v), clock.Now, clock.Schedule);

            gate.Set(true);
            gate.Set(true);
            clock.Advance(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { true }, seen);
        }

        /// <summary>Часы и отложенный вызов вместо настоящих пауз.</summary>
        private sealed class FakeClock {
            private readonly List<(DateTime At, Action What)> pending = new();
            private DateTime utc = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

            internal DateTime Now() => this.utc;

            internal void Schedule(TimeSpan delay, Action action) => this.pending.Add((this.utc + delay, action));

            internal void Advance(TimeSpan by) {
                var target = this.utc + by;
                while (true) {
                    var due = this.pending.FindAll(p => p.At <= target);
                    if (due.Count == 0) {
                        break;
                    }

                    due.Sort((a, b) => a.At.CompareTo(b.At));
                    var next = due[0];
                    this.pending.Remove(next);
                    this.utc = next.At;
                    next.What();
                }

                this.utc = target;
            }
        }
    }
}
