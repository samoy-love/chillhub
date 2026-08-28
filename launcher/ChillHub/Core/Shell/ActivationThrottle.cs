// <copyright file="ActivationThrottle.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;

    /// <summary>
    /// Пропускает работу, если с прошлого раза прошло слишком мало времени.
    /// <para>
    /// Возврат фокуса на окно — удобный повод освежить то, что могло измениться, пока
    /// лаунчер стоял в стороне. Беда в том, что фокус возвращается не только когда человек
    /// приходит из Steam: Alt+Tab между окнами даёт десятки активаций в минуту, и на каждой
    /// уходил запрос к серверу за версией да обход папки игры на диске — на UI-потоке.
    /// </para>
    /// <para>
    /// Отсчёт по <see cref="Environment.TickCount64"/>, а не по системным часам: перевод
    /// времени назад (или синхронизация с NTP) не должен запирать проверку на часы вперёд.
    /// </para>
    /// </summary>
    internal sealed class ActivationThrottle {
        private readonly long minIntervalMs;
        private readonly Func<long> now;
        private long lastAt;
        private bool used;

        /// <summary>Initializes a new instance of the <see cref="ActivationThrottle"/> class.</summary>
        /// <param name="minInterval">Не чаще одного раза в этот срок.</param>
        /// <param name="now">Часы в миллисекундах; шов для тестов.</param>
        internal ActivationThrottle(TimeSpan minInterval, Func<long>? now = null) {
            this.minIntervalMs = (long)Math.Max(0, minInterval.TotalMilliseconds);
            this.now = now ?? (() => Environment.TickCount64);
        }

        /// <summary>
        /// Пора ли делать работу. Первый вызов разрешает всегда: окно только что показали,
        /// и данные на нём заведомо старые.
        /// </summary>
        /// <returns>true, если работу нужно выполнить; тогда же отсчёт начинается заново.</returns>
        internal bool Allow() {
            var current = this.now();
            if (this.used && current - this.lastAt < this.minIntervalMs) {
                return false;
            }

            this.used = true;
            this.lastAt = current;
            return true;
        }
    }
}
