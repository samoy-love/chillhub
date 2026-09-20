// <copyright file="SpeedLimitTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Ограничение скорости скачивания: потолок настройки и то, что правку слышно
    /// на ИДУЩЕЙ закачке.
    /// <para>
    /// Настройку открывают ровно тогда, когда качающаяся игра забила канал. Пока
    /// лимит читался один раз, при создании бакета, правка действовала только на
    /// следующую загрузку — а на сборке в полсотни гигабайт «следующая» наступает
    /// через несколько часов.
    /// </para>
    /// </summary>
    public class SpeedLimitTests {
        /// <summary>
        /// Потолок был 10 МБ/с, и это делало настройку бесполезной там, где она нужна:
        /// на канале в 500–1000 Мбит «ограничить» означало «резать до восьмой части».
        /// </summary>
        [Fact]
        public void ПотолокОграниченияВышеДомашнегоКанала() {
            var cfg = new AppConfig { SpeedLimitMbps = 200 };

            ConfigService.Clamp(cfg);

            Assert.Equal(200, cfg.SpeedLimitMbps);
        }

        /// <summary>Выше потолка настройка не уходит — иначе «ограничение» перестаёт им быть.</summary>
        [Fact]
        public void ВышеПотолкаЗначениеСрезается() {
            var cfg = new AppConfig { SpeedLimitMbps = AppConfig.MaxSpeedLimitMbps + 1 };

            ConfigService.Clamp(cfg);

            Assert.Equal(AppConfig.MaxSpeedLimitMbps, cfg.SpeedLimitMbps);
        }

        /// <summary>Отрицательное значение — это «без лимита», а не «качать назад».</summary>
        [Fact]
        public void ОтрицательноеЗначениеЗначитБезЛимита() {
            var cfg = new AppConfig { SpeedLimitMbps = -5 };

            ConfigService.Clamp(cfg);

            Assert.Equal(0, cfg.SpeedLimitMbps);
        }

        /// <summary>Нулевой лимит не заставляет ждать ни одного такта.</summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task БезЛимитаПропускаетСразу() {
            var limiter = SpeedLimiter.Create(() => 0);

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 64; i++) {
                await limiter.ThrottleAsync(1024 * 1024, CancellationToken.None);
            }

            Assert.True(watch.ElapsedMilliseconds < 500, $"без лимита ждали {watch.ElapsedMilliseconds} мс");
        }

        /// <summary>
        /// ЛИМИТ СПРАШИВАЕТСЯ НА КАЖДОМ ШАГЕ. Включённое посреди закачки ограничение
        /// обязано притормозить её саму, а не следующую.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ВключённыйПосредиЗакачкиЛимитДействуетСразу() {
            var mbps = 0;
            var limiter = SpeedLimiter.Create(() => mbps);

            // Без лимита бакет ничего не копит и никого не держит.
            await limiter.ThrottleAsync(1024 * 1024, CancellationToken.None);

            // Лимит в один мегабайт в секунду: мегабайт разом сразу не пройдёт —
            // бакет после работы без лимита пуст, и его надо накопить.
            mbps = 1;
            var watch = Stopwatch.StartNew();
            await limiter.ThrottleAsync(1024 * 1024, CancellationToken.None);

            Assert.True(watch.ElapsedMilliseconds > 200, $"лимит не подействовал: ждали {watch.ElapsedMilliseconds} мс");
        }

        /// <summary>
        /// ПОРЦИЯ БОЛЬШЕ СЕКУНДНОГО ЗАПАСА НЕ ВЕШАЕТ ПОТОК. Бакет копит не больше
        /// секунды скачивания, и условие «набралось на всю порцию» для такой порции
        /// не выполнится никогда — поток крутился бы в цикле ожидания до конца света.
        /// Держалось это на совпадении чисел: порции по 256 КиБ и нижний лимит в
        /// мегабайт в секунду. Оба числа — настройки, и менять их никто не запрещал.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ПорцияБольшеСекундногоЗапасаНеЗависает() {
            var limiter = SpeedLimiter.Create(() => 1);

            var work = limiter.ThrottleAsync(8 * 1024 * 1024, CancellationToken.None);

            Assert.True(await Task.WhenAny(work, Task.Delay(10_000)) == work, "поток завис на порции больше запаса");
        }

        /// <summary>
        /// Снятое посреди закачки ограничение тоже слышно сразу: держать её после
        /// «без лимита» значило бы, что настройка не работает в обе стороны.
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task СнятыйПосредиЗакачкиЛимитПерестаётДержать() {
            var mbps = 1;
            var limiter = SpeedLimiter.Create(() => mbps);
            await limiter.ThrottleAsync(1024 * 1024, CancellationToken.None);

            mbps = 0;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 16; i++) {
                await limiter.ThrottleAsync(1024 * 1024, CancellationToken.None);
            }

            Assert.True(watch.ElapsedMilliseconds < 500, $"после снятия лимита ждали {watch.ElapsedMilliseconds} мс");
        }
    }
}
