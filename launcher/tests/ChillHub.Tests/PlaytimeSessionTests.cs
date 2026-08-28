// <copyright file="PlaytimeSessionTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;

    using ChillHub.Core.Game;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Жизнь игровой сессии: наигранное время и срок, на который включены моды.
    /// <para>
    /// Обе вещи ломались молча. Время перестало считаться, когда игра поехала запускаться
    /// новым путём, и заметить это можно было только по пустому playtime.json спустя
    /// неделю. А моды, оставленные включёнными после выхода из игры, поднимаются при
    /// следующем запуске ИЗ STEAM, мимо лаунчера, — и это тоже видно не сразу.
    /// </para>
    /// </summary>
    public class PlaytimeSessionTests : IDisposable {
        /// <summary>Настройки Doorstop с включёнными модами — как их кладёт установка модпака.</summary>
        private static readonly string Ini = string.Join(
            "\n", "[General]", "enabled=true", string.Empty);

        private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

        private readonly string dir = Path.Combine(Path.GetTempPath(), "chillhub-playtime-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable scope;

        public PlaytimeSessionTests() {
            Directory.CreateDirectory(this.dir);
            this.scope = PlaytimeStore.OverrideDirForTests(this.dir);
            PlaytimeStore.ResetForTests();
        }

        public void Dispose() {
            this.scope.Dispose();
            PlaytimeStore.ResetForTests();
            try {
                Directory.Delete(this.dir, recursive: true);
            }
            catch (IOException) {
                // Временный каталог — не повод валить прогон.
            }
        }

        /// <summary>
        /// Ждёт условия, а не «достаточной» паузы: поиск процесса уходит в фоновую задачу,
        /// и сон на глазок делает прогон мигающим.
        /// </summary>
        /// <param name="done">Условие.</param>
        /// <returns>Задача ожидания.</returns>
        private static async Task WaitUntilAsync(Func<bool> done) {
            var sw = Stopwatch.StartNew();
            while (!done()) {
                Assert.True(sw.Elapsed < WaitLimit, "не дождались записи о начатой сессии");
                await Task.Delay(20);
            }
        }

        /// <summary>Сессия закрывается — время игры прибавляется к сумме.</summary>
        [Fact]
        public void ЗакрытаяСессияПрибавляетВремя() {
            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", game);

            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(30));

            var entry = PlaytimeStore.Get("repo");
            Assert.InRange(entry.TotalSeconds, 1700, 1900);
            Assert.NotNull(entry.LastSessionAt);
        }

        /// <summary>
        /// Второй заход на тот же процесс не сдвигает начало сессии: через Steam игру
        /// ищут ожиданием, и два нажатия «Играть» подряд находят один и тот же процесс.
        /// </summary>
        [Fact]
        public void ПовторныйЗаходНеСдвигаетНачалоСессии() {
            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", game);
            PlaytimeStore.BeginSession("repo", game);

            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(10));

            Assert.InRange(PlaytimeStore.Get("repo").TotalSeconds, 500, 700);
        }

        /// <summary>
        /// Запуск с модами держит их включёнными ровно до выхода из игры. Иначе кнопка
        /// Play в самом Steam молча подняла бы игру с модами.
        /// </summary>
        [Fact]
        public void ПослеСессииСМодамиПапкаВозвращаетсяКВанили() {
            var gameDir = Path.Combine(this.dir, "steamapps", "REPO");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), "[General]\nenabled=true\n");

            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", game, gameDir);
            Assert.True(DoorstopConfig.ReadEnabled(gameDir));

            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(5));

            Assert.False(DoorstopConfig.ReadEnabled(gameDir));
        }

        /// <summary>Запуск без модов чужую папку не трогает вовсе.</summary>
        [Fact]
        public void СессияБезМодовПапкуНеТрогает() {
            var gameDir = Path.Combine(this.dir, "steamapps", "VANILLA");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), "[General]\nenabled=true\n");

            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("vanilla", game);
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(1));

            // Никто не просил её менять — значение осталось прежним.
            Assert.True(DoorstopConfig.ReadEnabled(gameDir));
        }

        /// <summary>
        /// Прямой запуск: процесс игры вернулся сразу, отсчёт заводится тем же вызовом.
        /// Ровно этот путь и перестал считать время, когда игра поехала через ModsLaunch.
        /// </summary>
        [Fact]
        public void ПрямойЗапускЗаводитОтсчётСразу() {
            using var game = Process.GetCurrentProcess();

            GameSession.Begin("repo", this.dir, null, game, viaSteam: false, moddedDir: null);
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(15));

            Assert.InRange(PlaytimeStore.Get("repo").TotalSeconds, 800, 1000);
        }

        /// <summary>
        /// Запуск через Steam: процесса игры у нас нет, его дожидаются поиском по папке —
        /// и только найденному заводят отсчёт.
        /// </summary>
        [Fact]
        public async Task ЗапускЧерезSteamЖдётПроцессИгры() {
            using var game = Process.GetCurrentProcess();
            var gameDir = Path.GetDirectoryName(game.MainModule!.FileName)!;
            GameProcessFinder.ByName = _ => new[] { new RunningProcess(game.Id, game.MainModule!.FileName) };

            try {
                GameSession.Begin("repo", gameDir, game.MainModule!.FileName, null, viaSteam: true, moddedDir: null);

                await WaitUntilAsync(() => File.Exists(Path.Combine(this.dir, "playtime.sessions.json"))
                                           && File.ReadAllText(Path.Combine(this.dir, "playtime.sessions.json")).Contains("repo", StringComparison.Ordinal));
            }
            finally {
                GameProcessFinder.ResetForTests();
            }

            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(5));
            Assert.InRange(PlaytimeStore.Get("repo").TotalSeconds, 250, 350);
        }

        /// <summary>
        /// Лаунчер умер раньше игры: незакрытую сессию подбирает его следующий запуск —
        /// время дописывается, а папка возвращается к ванили. Без этого моды остались бы
        /// включёнными навсегда, и Play в Steam поднимал бы игру с ними.
        /// </summary>
        [Fact]
        public void СледующийЗапускЗакрываетСессиюУмершегоЛаунчера() {
            var gameDir = Path.Combine(this.dir, "steamapps", "ORPHAN");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), Ini);

            // Процесс с таким номером в системе не живёт — игра закрылась, пока лаунчера не было.
            var pending = "{\"424242\":{\"GameId\":\"repo\",\"ProcessId\":424242,\"ProcessStartTimeTicks\":123," +
                          "\"SessionStartUtc\":\"" + DateTime.UtcNow.AddMinutes(-45).ToString("O") + "\"," +
                          "\"ModdedDir\":" + System.Text.Json.JsonSerializer.Serialize(gameDir) + "}}";
            File.WriteAllText(Path.Combine(this.dir, "playtime.sessions.json"), pending);

            PlaytimeStore.ResetForTests();
            PlaytimeStore.EnsureStarted();

            Assert.False(DoorstopConfig.ReadEnabled(gameDir));
            Assert.InRange(PlaytimeStore.Get("repo").TotalSeconds, 2600, 2800);
            Assert.Equal("{}", File.ReadAllText(Path.Combine(this.dir, "playtime.sessions.json")).Trim());
        }

        /// <summary>
        /// Игра так и не появилась (Steam не смог её запустить) — сессии нет вовсе.
        /// Пустая запись хуже отсутствующей: её потом закроют «задним числом» и припишут
        /// игре время, которого не было.
        /// </summary>
        [Fact]
        public async Task НеНайденнаяИграСессииНеЗаводит() {
            GameProcessFinder.ByName = _ => Array.Empty<RunningProcess>();
            GameProcessFinder.DefaultTimeout = TimeSpan.Zero;
            GameProcessFinder.PollInterval = TimeSpan.FromMilliseconds(1);

            try {
                GameSession.Begin("repo", this.dir, Path.Combine(this.dir, "REPO.exe"), null, viaSteam: true, moddedDir: null);

                // Ждём, пока фоновая задача точно отработает: файла сессий не появится.
                await Task.Delay(200);
            }
            finally {
                GameProcessFinder.ResetForTests();
            }

            Assert.Equal(0, PlaytimeStore.Get("repo").TotalSeconds);
        }

        /// <summary>Без игры отсчёт не заводится: имя пустое — записывать нечего.</summary>
        [Fact]
        public void БезИдентификатораИгрыОтсчётаНет() {
            using var game = Process.GetCurrentProcess();

            GameSession.Begin(null, this.dir, null, game, viaSteam: false, moddedDir: null);

            Assert.False(File.Exists(Path.Combine(this.dir, "playtime.sessions.json")));
        }

        /// <summary>Закрывать нечего — второй вызов ничего не портит и не удваивает время.</summary>
        [Fact]
        public void ПовторноеЗакрытиеНичегоНеМеняет() {
            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", game);
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(20));
            var after = PlaytimeStore.Get("repo").TotalSeconds;

            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(40));

            Assert.Equal(after, PlaytimeStore.Get("repo").TotalSeconds);
        }
    }
}
