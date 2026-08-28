// <copyright file="ActivationThrottleTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Ограничитель работы по возврату фокуса на окно.
    /// <para>
    /// Возврат фокуса — удобный повод освежить состояние, но фокус возвращается не только
    /// когда человек пришёл из Steam: Alt+Tab даёт десятки активаций в минуту, и на каждой
    /// уходил запрос к серверу за версией да обход папки игры на диске.
    /// </para>
    /// </summary>
    public class ActivationThrottleTests {
        /// <summary>Первый раз разрешается всегда: данные на только что показанном окне старые.</summary>
        [Fact]
        public void ПервыйРазРазрешёнВсегда() {
            var clock = 1_000L;
            var throttle = new ActivationThrottle(TimeSpan.FromMinutes(10), () => clock);

            Assert.True(throttle.Allow());
        }

        /// <summary>Перещёлкивание окон подряд работу не запускает.</summary>
        [Fact]
        public void ПодрядИдущиеАктивацииПропускаются() {
            var clock = 0L;
            var throttle = new ActivationThrottle(TimeSpan.FromSeconds(5), () => clock);

            Assert.True(throttle.Allow());
            clock += 100;
            Assert.False(throttle.Allow());
            clock += 4_000;
            Assert.False(throttle.Allow());
        }

        /// <summary>Прошёл срок — работа снова разрешена.</summary>
        [Fact]
        public void ПослеСрокаРаботаСноваРазрешена() {
            var clock = 0L;
            var throttle = new ActivationThrottle(TimeSpan.FromSeconds(5), () => clock);

            Assert.True(throttle.Allow());
            clock += 5_000;
            Assert.True(throttle.Allow());

            // Отсчёт начинается заново от разрешённого раза, а не от первого.
            clock += 4_999;
            Assert.False(throttle.Allow());
        }

        /// <summary>Нулевой срок ничего не ограничивает: так его и отключают.</summary>
        [Fact]
        public void НулевойСрокНеОграничивает() {
            var clock = 0L;
            var throttle = new ActivationThrottle(TimeSpan.Zero, () => clock);

            Assert.True(throttle.Allow());
            Assert.True(throttle.Allow());
        }
    }
}
