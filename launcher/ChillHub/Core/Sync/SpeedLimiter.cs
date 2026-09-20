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
    /// <para>
    /// ЛИМИТ СПРАШИВАЕТСЯ КАЖДЫЙ РАЗ, А НЕ ЗАПОМИНАЕТСЯ ПРИ СОЗДАНИИ. Настройку
    /// открывают ровно тогда, когда идущая закачка забила канал, — а прочитанное один
    /// раз значение означало, что правка подействует только на следующую загрузку.
    /// На сборке в полсотни гигабайт «следующая» наступает через несколько часов.
    /// </para>
    /// </summary>
    internal sealed class SpeedLimiter {
        private readonly Func<int> mbps;
        private readonly object gate = new object();
        private double tokens;
        private long lastRefillTicks;

        private SpeedLimiter(Func<int> mbps) {
            this.mbps = mbps;
            this.tokens = 0;
            this.lastRefillTicks = Environment.TickCount64;
        }

        /// <summary>
        /// Создаёт лимитер, который на каждом шаге спрашивает текущий лимит.
        /// Пока лимит нулевой, он ничего не делает и не стоит ничего.
        /// </summary>
        /// <param name="mbps">Откуда брать лимит скорости, МБ/с; 0 — без лимита.</param>
        /// <returns>Лимитер.</returns>
        internal static SpeedLimiter Create(Func<int> mbps) => new SpeedLimiter(mbps);

        /// <summary>Байт в секунду по текущей настройке; 0 — ограничения нет.</summary>
        private long BytesPerSecond {
            get {
                int current;
                try {
                    current = this.mbps();
                }
                catch (Exception) {
                    // Настройка не прочиталась — качаем без ограничения: остановить
                    // закачку из-за неудавшегося чтения конфига хуже, чем не ограничить.
                    return 0;
                }

                return current > 0 ? (long)current * 1024 * 1024 : 0;
            }
        }

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
                    var rate = this.BytesPerSecond;
                    if (rate <= 0) {
                        // Ограничение выключили — бакет больше не нужен, а часы
                        // переводим, чтобы включённый обратно лимит не пополнился
                        // разом за всё время без него.
                        this.lastRefillTicks = Environment.TickCount64;
                        this.tokens = rate;
                        return;
                    }

                    this.Refill(rate);

                    // ПОРЦИЯ БОЛЬШЕ СЕКУНДНОГО ЗАПАСА НЕ ДОЛЖНА ВЕШАТЬ ПОТОК.
                    //
                    // Бакет копит не больше секунды скачивания, поэтому условие
                    // «набралось на всю порцию» для такой порции не выполняется
                    // НИКОГДА: поток крутился бы в этом цикле до конца света. Пока
                    // порции приходили по 256 КиБ, а нижний лимит был мегабайт в
                    // секунду, до этого не доходило — но условие держалось на
                    // совпадении чисел, а не на устройстве.
                    //
                    // Берём в долг: списываем всю порцию, даже уходя в минус, а долг
                    // отдаётся пополнением — то есть ожиданием на следующем вызове.
                    // Средняя скорость от этого не меняется, а зависнуть нечему.
                    var enough = Math.Min(bytes, rate);
                    if (this.tokens >= enough) {
                        this.tokens -= bytes;
                        return;
                    }

                    var missing = enough - this.tokens;
                    waitMs = (int)Math.Clamp(missing * 1000.0 / rate, 1, 1000);
                }

                await Task.Delay(waitMs, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Пополняет бакет по прошедшему времени, не превышая размер в одну секунду скачивания.</summary>
        /// <param name="rate">Текущий лимит, байт в секунду.</param>
        private void Refill(long rate) {
            var now = Environment.TickCount64;
            var elapsedMs = now - this.lastRefillTicks;
            if (elapsedMs <= 0) {
                this.tokens = Math.Min(rate, this.tokens);
                return;
            }

            this.lastRefillTicks = now;
            var add = elapsedMs * rate / 1000.0;
            this.tokens = Math.Min(rate, this.tokens + add);
        }
    }
}
