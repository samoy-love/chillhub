// <copyright file="SpeedLimiter.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Простой токен-бакет для ограничения суммарной скорости скачивания.
    /// <para>
    /// Один экземпляр делят все потоки загрузки одного <see cref="SimpleSyncService.ExecuteAsync"/>:
    /// лимит держит именно суммарную скорость, а не скорость каждого потока по отдельности —
    /// иначе включённые N потоков сводили бы ограничение на нет, умножая его на N.
    /// </para>
    /// </summary>
    internal sealed class SpeedLimiter {
        private readonly long bytesPerSecond;
        private readonly object gate = new object();
        private double tokens;
        private long lastRefillTicks;

        private SpeedLimiter(int mbps) {
            this.bytesPerSecond = (long)mbps * 1024 * 1024;
            this.tokens = this.bytesPerSecond;
            this.lastRefillTicks = Environment.TickCount64;
        }

        /// <summary>
        /// Создаёт лимитер, либо null, если ограничение выключено (0 — без лимита).
        /// </summary>
        /// <param name="mbps">Лимит скорости, МБ/с.</param>
        /// <returns>Лимитер либо null.</returns>
        internal static SpeedLimiter? Create(int mbps) => mbps > 0 ? new SpeedLimiter(mbps) : null;

        /// <summary>
        /// Ждёт, пока в бакете не наберётся достаточно токенов на переданное число байт,
        /// затем списывает их. Без ожидания на «холодном» бакете первая порция пройдёт
        /// сразу — это позволяет коротким докачкам не спотыкаться о лимит на пустом месте.
        /// </summary>
        /// <param name="bytes">Сколько байт только что прочитано/записано.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Задача, завершающаяся, когда можно продолжать.</returns>
        internal async Task ThrottleAsync(int bytes, CancellationToken ct) {
            while (true) {
                int waitMs;
                lock (this.gate) {
                    this.Refill();
                    if (this.tokens >= bytes) {
                        this.tokens -= bytes;
                        return;
                    }

                    var missing = bytes - this.tokens;
                    waitMs = (int)Math.Clamp(missing * 1000.0 / this.bytesPerSecond, 1, 1000);
                }

                await Task.Delay(waitMs, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Пополняет бакет по прошедшему времени, не превышая размер в одну секунду скачивания.</summary>
        private void Refill() {
            var now = Environment.TickCount64;
            var elapsedMs = now - this.lastRefillTicks;
            if (elapsedMs <= 0) {
                return;
            }

            this.lastRefillTicks = now;
            var add = elapsedMs * this.bytesPerSecond / 1000.0;
            this.tokens = Math.Min(this.bytesPerSecond, this.tokens + add);
        }
    }
}
