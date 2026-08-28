// <copyright file="GameProcessFinderTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;

    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Поиск процесса игры, запущенной через Steam.
    /// <para>
    /// Steam поднимает игру сам, и лаунчеру достаётся не тот процесс: без поиска по папке
    /// не считается наигранное время и, что важнее, не наступает момент, когда игру
    /// закрыли, — а по нему папка возвращается в состояние без модов.
    /// </para>
    /// <para>
    /// Имя exe у копии из Steam и у сборки с сервера одинаковое, поэтому цена ошибки —
    /// не «не нашли», а «нашли не ту копию»: время одной игры записалось бы другой, а
    /// моды выключились бы в чужой папке.
    /// </para>
    /// </summary>
    public class GameProcessFinderTests : IDisposable {
        private static readonly string SteamDir = Path.Combine("C:", "Steam", "REPO");
        private static readonly string LocalDir = Path.Combine("C:", "ChillHub", "REPO");

        public void Dispose() => GameProcessFinder.ResetForTests();

        /// <summary>Процесс из нужной папки находится по имени исполняемого файла.</summary>
        [Fact]
        public void ПроцессИзПапкиИгрыНаходится() {
            GameProcessFinder.ByName = _ => new List<RunningProcess> {
                new(100, Path.Combine(SteamDir, "REPO.exe")),
            };

            Assert.Equal(100, GameProcessFinder.Find(SteamDir, Path.Combine(SteamDir, "REPO.exe")));
        }

        /// <summary>
        /// Тот же exe, но из другой копии игры, за свой не выдаётся: у копии из Steam и у
        /// сборки с сервера имена файлов совпадают, и различает их только путь.
        /// </summary>
        [Fact]
        public void ЧужаяКопияСТемЖеИменемНеПодходит() {
            GameProcessFinder.ByName = _ => new List<RunningProcess> {
                new(101, Path.Combine(LocalDir, "REPO.exe")),
            };

            Assert.Null(GameProcessFinder.Find(SteamDir, Path.Combine(SteamDir, "REPO.exe")));
        }

        /// <summary>
        /// Папка с тем же началом имени — не та же папка: «…\REPO 2» рядом с «…\REPO»
        /// не должна сойти за неё.
        /// </summary>
        [Fact]
        public void ПапкаСПохожимИменемНеСчитаетсяСвоей() {
            Assert.False(GameProcessFinder.BelongsTo(Path.Combine(SteamDir + " 2", "REPO.exe"), SteamDir));
            Assert.True(GameProcessFinder.BelongsTo(Path.Combine(SteamDir, "REPO.exe"), SteamDir));
        }

        /// <summary>Путь процесса прочитать не удалось — процесс просто не наш.</summary>
        [Fact]
        public void НечитаемыйПутьНеЛоматПоиск() {
            GameProcessFinder.ByName = _ => new List<RunningProcess> { new(102, null) };

            Assert.Null(GameProcessFinder.Find(SteamDir, Path.Combine(SteamDir, "REPO.exe")));
            Assert.False(GameProcessFinder.BelongsTo(null, SteamDir));
            Assert.False(GameProcessFinder.BelongsTo(Path.Combine(SteamDir, "REPO.exe"), null));
        }

        /// <summary>Без пути к exe искать нечего — и падать тоже не на чем.</summary>
        [Fact]
        public void БезПутиКФайлуПоискаНет() {
            Assert.Null(GameProcessFinder.Find(SteamDir, null));
            Assert.Null(GameProcessFinder.Find(null, Path.Combine(SteamDir, "REPO.exe")));
        }

        /// <summary>
        /// Steam тянет с запуском — игра появляется не сразу, и ожидание её дожидается.
        /// </summary>
        [Fact]
        public async Task ОжиданиеДожидаетсяПоявленияИгры() {
            var calls = 0;
            GameProcessFinder.ByName = _ => ++calls < 3
                ? new List<RunningProcess>()
                : new List<RunningProcess> { new(103, Path.Combine(SteamDir, "REPO.exe")) };

            var pid = await GameProcessFinder.WaitAsync(
                SteamDir, Path.Combine(SteamDir, "REPO.exe"), TimeSpan.FromMinutes(1), _ => Task.CompletedTask);

            Assert.Equal(103, pid);
        }

        /// <summary>
        /// Игра так и не появилась — ожидание кончается отказом, а не висит вечно.
        /// Незакрытой сессии при этом не возникает: её просто не начали.
        /// </summary>
        [Fact]
        public async Task ОжиданиеКончаетсяПоСроку() {
            GameProcessFinder.ByName = _ => new List<RunningProcess>();

            var pid = await GameProcessFinder.WaitAsync(
                SteamDir, Path.Combine(SteamDir, "REPO.exe"), TimeSpan.FromSeconds(3), _ => Task.CompletedTask);

            Assert.Null(pid);
        }
    }
}
