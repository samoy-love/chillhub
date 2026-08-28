// <copyright file="LaunchOptionsCacheTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;

    using ChillHub.Core;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Снимок вариантов запуска: он экономит поход в реестр и на диск на каждой
    /// перерисовке витрины, но не имеет права пережить изменение состояния игры.
    /// <para>
    /// Ошибка здесь не падает, а тихо показывает вчерашний день: кнопка ещё секунду
    /// предлагает «установить моды», которые только что установлены, — и щелчок по ней
    /// уводит игрока во второй круг установки.
    /// </para>
    /// </summary>
    public class LaunchOptionsCacheTests {
        /// <summary>Свежий снимок отдаётся вместо повторного счёта.</summary>
        [Fact]
        public void СвежийСнимокПереиспользуется() {
            var clock = 0L;
            var cache = new LaunchOptionsCache(() => clock);
            var game = Game();
            var options = Options();

            cache.Put(game, options);
            clock += LaunchOptionsCache.LifetimeMs - 1;

            Assert.Same(options, cache.Get(game));
        }

        /// <summary>Просроченный снимок не отдаётся: копию в Steam могли удалить.</summary>
        [Fact]
        public void ПросроченныйСнимокНеОтдаётся() {
            var clock = 0L;
            var cache = new LaunchOptionsCache(() => clock);
            var game = Game();

            cache.Put(game, Options());
            clock += LaunchOptionsCache.LifetimeMs;

            Assert.Null(cache.Get(game));
        }

        /// <summary>
        /// СМЕНА СОСТОЯНИЯ ИГРЫ ОБНУЛЯЕТ СНИМОК СРАЗУ, не дожидаясь срока: игру только
        /// что установили — предлагать «установить» ещё секунду нельзя.
        /// </summary>
        [Fact]
        public void СменаСостоянияИгрыОбнуляетСнимок() {
            var cache = new LaunchOptionsCache(() => 0L);
            var game = Game();
            cache.Put(game, Options());

            game.IsInstalled = true;

            Assert.Null(cache.Get(game));
        }

        /// <summary>Снимок одной игры не отдаётся другой.</summary>
        [Fact]
        public void СнимокДругойИгрыНеПодходит() {
            var cache = new LaunchOptionsCache(() => 0L);
            cache.Put(Game(), Options());

            Assert.Null(cache.Get(Game("peak")));
        }

        /// <summary>Явный сброс — после установки модов состояние папки уже другое.</summary>
        [Fact]
        public void ЯвныйСбросЗабываетСнимок() {
            var cache = new LaunchOptionsCache(() => 0L);
            var game = Game();
            cache.Put(game, Options());

            cache.Invalidate();

            Assert.Null(cache.Get(game));
            Assert.Null(cache.Get(null));
        }

        /// <summary>Пустой кеш ничего не выдумывает.</summary>
        [Fact]
        public void ПустойКешОтдаётНичего() {
            Assert.Null(new LaunchOptionsCache(() => 0L).Get(Game()));
        }

        private static GameInfo Game(string id = "lethal-company") => new GameInfo { GameId = id };

        private static IReadOnlyList<LaunchOption> Options() => new List<LaunchOption> {
            new LaunchOption(
                LaunchTarget.SteamModded, "Steam · с модами", @"C:\steam\game", true, LaunchAction.Play, string.Empty),
        };
    }
}
