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
            RunningGames.ResetForTests();
        }

        public void Dispose() {
            this.scope.Dispose();
            PlaytimeStore.ResetForTests();
            RunningGames.ResetForTests();
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
        private static async Task WaitUntilAsync(Func<bool> done, string what = "записи о начатой сессии") {
            var sw = Stopwatch.StartNew();
            while (!done()) {
                Assert.True(sw.Elapsed < WaitLimit, "не дождались " + what);
                await Task.Delay(20);
            }
        }

        /// <summary>
        /// Заведённая сессия — это и есть «игра запущена» для витрины: второго места,
        /// где бы это отслеживалось, нет, и разойтись им негде.
        /// </summary>
        [Fact]
        public void СессияДелаетИгруЗапущеннойДляВитрины() {
            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalModded, game);
            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));

            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Лаунчер закрыли и открыли, не выходя из игры: подобранная сессия сразу
        /// значится запущенной, иначе витрина предложила бы запустить игру второй раз.
        /// </summary>
        [Fact]
        public void ПодобраннаяСессияЖивойИгрыСразуЗначитсяЗапущенной() {
            using var game = Process.GetCurrentProcess();
            WritePending(game, target: "SteamModded");

            PlaytimeStore.ResetForTests();
            PlaytimeStore.EnsureStarted();

            // И игра в целом, и та самая её версия — обе значатся запущенными.
            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));
            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo", LaunchTarget.SteamModded));

            // А соседняя версия — нет: это другая папка и другой процесс.
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo", LaunchTarget.LocalModded));
        }

        /// <summary>
        /// Сессия, заведённая сборкой, которая ещё не различала версии: версии в записи
        /// нет. Время такой сессии не пропадает — оно уходит в общий счёт игры, а не
        /// приписывается наугад одной из четырёх копий.
        /// </summary>
        [Fact]
        public void СессияБезВерсииЗакрываетсяВОбщийСчётИгры() {
            using var game = Process.GetCurrentProcess();
            WritePending(game, target: null);

            PlaytimeStore.ResetForTests();
            PlaytimeStore.EnsureStarted();
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow);

            Assert.InRange(PlaytimeStore.Get("repo").TotalSeconds, 250, 350);
        }

        /// <summary>Кладёт незакрытую сессию живого процесса — с версией или без неё.</summary>
        /// <param name="game">Процесс, играющий роль игры.</param>
        /// <param name="target">Имя варианта запуска; null — запись старой сборки.</param>
        private void WritePending(Process game, string? target) {
            var withTarget = target == null ? string.Empty : ",\"Target\":\"" + target + "\"";
            var pending = "{\"" + game.Id + "\":{\"GameId\":\"repo\"" + withTarget +
                          ",\"ProcessId\":" + game.Id +
                          ",\"ProcessStartTimeTicks\":" + game.StartTime.Ticks +
                          ",\"SessionStartUtc\":\"" + DateTime.UtcNow.AddMinutes(-5).ToString("O") + "\"}}";
            File.WriteAllText(Path.Combine(this.dir, "playtime.sessions.json"), pending);
        }

        /// <summary>
        /// ВНУТРИ — ПО ВЕРСИЯМ, НАРУЖУ — ОДНОЙ ЦИФРОЙ. Игроку важно, сколько он провёл
        /// в игре, а не в какой из папок она лежала; раздельный счёт нужен, чтобы одна
        /// копия не приписывала себе часы другой.
        /// </summary>
        [Fact]
        public void ВремяКопитсяПоВерсиямАПоказываетсяСуммой() {
            using var game = Process.GetCurrentProcess();

            PlaytimeStore.BeginSession("repo", LaunchTarget.SteamModded, game);
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(30));

            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalModded, game);
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(10));

            // Каждая версия помнит своё.
            Assert.InRange(PlaytimeStore.Get("repo", LaunchTarget.SteamModded).TotalSeconds, 1700, 1900);
            Assert.InRange(PlaytimeStore.Get("repo", LaunchTarget.LocalModded).TotalSeconds, 500, 700);

            // Соседняя, в которую не играли, — ноль, а не чужие часы.
            Assert.Equal(0, PlaytimeStore.Get("repo", LaunchTarget.SteamVanilla).TotalSeconds);

            // А витрина показывает сорок минут одной строкой.
            Assert.InRange(PlaytimeStore.Get("repo").TotalSeconds, 2300, 2500);
        }

        /// <summary>
        /// Последняя сессия в сумме — самая поздняя из всех версий: игрок спрашивает
        /// «когда я играл в неё в прошлый раз», а не «в какую из копий».
        /// </summary>
        [Fact]
        public void ПоследняяСессияБерётсяСамаяПоздняяИзВерсий() {
            using var game = Process.GetCurrentProcess();
            var early = DateTime.UtcNow.AddHours(-5);
            var late = DateTime.UtcNow.AddMinutes(-1);

            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalVanilla, game);
            PlaytimeStore.FinishForTests(game.Id, early);

            PlaytimeStore.BeginSession("repo", LaunchTarget.SteamModded, game);
            PlaytimeStore.FinishForTests(game.Id, late);

            var total = PlaytimeStore.Get("repo");
            Assert.NotNull(total.LastSessionAt);
            Assert.Equal(late, total.LastSessionAt!.Value, TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Время, накопленное сборками до разделения по версиям, лежит под голым
        /// идентификатором игры — и остаётся в сумме. Месяцы наигранного не должны
        /// исчезнуть из-за того, что мы научились считать точнее.
        /// </summary>
        [Fact]
        public void СтароеВремяБезВерсииОстаётсяВСумме() {
            File.WriteAllText(
                Path.Combine(this.dir, "playtime.json"),
                "{\"repo\":{\"TotalSeconds\":3600,\"LastSessionSeconds\":600}}");

            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", LaunchTarget.SteamModded, game);
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(10));

            // Час старого плюс десять минут новой версии.
            Assert.InRange(PlaytimeStore.Get("repo").TotalSeconds, 4100, 4300);
        }

        /// <summary>
        /// В сумму по игре не попадает время соседней: файл общий на все игры, и
        /// перепутанные записи приписали бы одной игре часы другой.
        /// </summary>
        [Fact]
        public void ВремяСоседнейИгрыВСуммуНеПопадает() {
            File.WriteAllText(
                Path.Combine(this.dir, "playtime.json"),
                "{\"peak#SteamModded\":{\"TotalSeconds\":7200}," +
                "\"repo#SteamModded\":{\"TotalSeconds\":600}}");

            Assert.Equal(600, PlaytimeStore.Get("repo").TotalSeconds);
            Assert.Equal(7200, PlaytimeStore.Get("peak").TotalSeconds);

            // И про игру, которой в файле нет, — ноль, а не чужие часы.
            Assert.Equal(0, PlaytimeStore.Get("lethal-company").TotalSeconds);
        }

        /// <summary>Сессия закрывается — время игры прибавляется к сумме.</summary>
        [Fact]
        public void ЗакрытаяСессияПрибавляетВремя() {
            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalModded, game);

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
            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalModded, game);
            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalModded, game);

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
            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalModded, game, gameDir);
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
            PlaytimeStore.BeginSession("vanilla", LaunchTarget.LocalModded, game);
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

            GameSession.Begin("repo", this.dir, null, game, viaSteam: false, moddedDir: null, target: LaunchTarget.LocalModded);
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
                GameSession.Begin("repo", gameDir, game.MainModule!.FileName, null, viaSteam: true, moddedDir: null, target: LaunchTarget.LocalModded);

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
                GameSession.Begin("repo", this.dir, Path.Combine(this.dir, "REPO.exe"), null, viaSteam: true, moddedDir: null, target: LaunchTarget.LocalModded);

                // Ждём ПРИЗНАК завершения фоновой задачи, а не «достаточную» паузу:
                // «Запускается…» снимается в её finally. Сон на глазок делал прогон
                // мигающим — на медленном раннере двухсот миллисекунд не хватало.
                await WaitUntilAsync(
                    () => RunningGames.StateOf("repo") == GameRunState.None,
                    "снятия «Запускается…» после неудачного поиска процесса");
            }
            finally {
                GameProcessFinder.ResetForTests();
            }

            // Сессии не завелось: пустая запись хуже отсутствующей — её потом закроют
            // «задним числом» и припишут игре время, которого не было.
            Assert.False(File.Exists(Path.Combine(this.dir, "playtime.sessions.json")));
            Assert.Equal(0, PlaytimeStore.Get("repo").TotalSeconds);
        }

        /// <summary>Без игры отсчёт не заводится: имя пустое — записывать нечего.</summary>
        [Fact]
        public void БезИдентификатораИгрыОтсчётаНет() {
            using var game = Process.GetCurrentProcess();

            GameSession.Begin(null, this.dir, null, game, viaSteam: false, moddedDir: null, target: LaunchTarget.LocalModded);

            Assert.False(File.Exists(Path.Combine(this.dir, "playtime.sessions.json")));
        }

        /// <summary>Закрывать нечего — второй вызов ничего не портит и не удваивает время.</summary>
        [Fact]
        public void ПовторноеЗакрытиеНичегоНеМеняет() {
            using var game = Process.GetCurrentProcess();
            PlaytimeStore.BeginSession("repo", LaunchTarget.LocalModded, game);
            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(20));
            var after = PlaytimeStore.Get("repo").TotalSeconds;

            PlaytimeStore.FinishForTests(game.Id, DateTime.UtcNow.AddMinutes(40));

            Assert.Equal(after, PlaytimeStore.Get("repo").TotalSeconds);
        }
    }
}
