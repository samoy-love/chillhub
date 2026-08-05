// <copyright file="GameStatusVerifierTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Проверка статуса игры по манифесту — то, откуда берётся надпись на кнопке действия.
    /// <para>
    /// Здесь дороже всего два исхода: посчитать установленную игру неустановленной
    /// (пользователь качает десятки гигабайт заново) и посчитать наполовину обновлённую
    /// игру готовой (запуск смеси двух версий). Оба проверяются ниже.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class GameStatusVerifierTests {
        /// <summary>Файлы совпали с эталоном — игра установлена и обновления не требует.</summary>
        [Fact]
        public async Task СовпадениеСЭталономЭтоГотоваяИгра() {
            using var games = new GamesPathScope();
            WriteGameFile(games.Root, "game");
            var sync = new FakeSyncService(new DiffPlan());
            var game = new GameInfo { GameId = "game", LatestVersion = "1.0.0" };

            await Verifier(sync).VerifyAsync(game);

            Assert.True(game.IsInstalled);
            Assert.False(game.NeedsUpdate);
        }

        /// <summary>Часть файлов отличается — игра установлена, но требует докачки.</summary>
        [Fact]
        public async Task НедостающиеФайлыТребуютОбновления() {
            using var games = new GamesPathScope();
            WriteGameFile(games.Root, "game");
            var sync = new FakeSyncService(PlanWithDownloads(2, 1024));
            var game = new GameInfo { GameId = "game", LatestVersion = "1.0.0" };

            await Verifier(sync).VerifyAsync(game);

            Assert.True(game.IsInstalled);
            Assert.True(game.NeedsUpdate);
        }

        /// <summary>
        /// Лишние локальные файлы (логи, кеш модов) в план удаления попадают, но игру
        /// устаревшей не делают: иначе кнопка вечно предлагала бы «Обновить».
        /// </summary>
        [Fact]
        public async Task ЛишниеЛокальныеФайлыНеТребуютОбновления() {
            using var games = new GamesPathScope();
            WriteGameFile(games.Root, "game");
            var plan = new DiffPlan();
            plan.ToDelete.Add("logs/game.log");
            var game = new GameInfo { GameId = "game", LatestVersion = "1.0.0" };

            await Verifier(new FakeSyncService(plan)).VerifyAsync(game);

            Assert.True(game.IsInstalled);
            Assert.False(game.NeedsUpdate);
        }

        /// <summary>Пустая папка — игра не установлена, даже если план различий пуст.</summary>
        [Fact]
        public async Task ПустаяПапкаЭтоНеустановленнаяИгра() {
            using var games = new GamesPathScope();
            var game = new GameInfo { GameId = "game", LatestVersion = "1.0.0" };

            await Verifier(new FakeSyncService(new DiffPlan())).VerifyAsync(game);

            Assert.False(game.IsInstalled);
            Assert.False(game.NeedsUpdate);
        }

        /// <summary>
        /// Маркер незавершённого обновления решает всё: сеть не трогаем, игру считаем
        /// требующей восстановления. Файлы смешаны из двух сборок, и «Играть» тут запрещено (C2).
        /// </summary>
        [Fact]
        public async Task ОборванноеОбновлениеРешаетсяБезСети() {
            using var games = new GamesPathScope();
            var root = WriteGameFile(games.Root, "game");
            File.WriteAllText(Path.Combine(root, SimpleSyncService.UpdateMarkerFileName), "version=1.0.0");
            var sync = new FakeSyncService(new DiffPlan());
            var game = new GameInfo { GameId = "game", LatestVersion = "1.0.0" };

            await Verifier(sync).VerifyAsync(game);

            Assert.True(game.IsInstalled);
            Assert.True(game.NeedsUpdate);
            Assert.Equal(0, sync.ManifestRequests);
        }

        /// <summary>
        /// Сервер не назвал эталонную версию — сравнивать не с чем: наличие файлов
        /// определяет установленность, а обновление не навязываем.
        /// </summary>
        [Fact]
        public async Task БезЭталоннойВерсииСудимПоФайлам() {
            using var games = new GamesPathScope();
            WriteGameFile(games.Root, "game");
            var sync = new FakeSyncService(new DiffPlan());
            var game = new GameInfo { GameId = "game", LatestVersion = string.Empty, NeedsUpdate = true };

            await Verifier(sync).VerifyAsync(game);

            Assert.True(game.IsInstalled);
            Assert.False(game.NeedsUpdate);
            Assert.Equal(0, sync.ManifestRequests);
        }

        /// <summary>
        /// Сеть отвалилась посреди проверки — прежний статус остаётся нетронутым.
        /// Сбросить его в «не установлено» значит предложить пользователю скачать всё заново.
        /// </summary>
        [Fact]
        public async Task СбойСетиНеСбрасываетСтатус() {
            using var games = new GamesPathScope();
            WriteGameFile(games.Root, "game");
            var sync = new FakeSyncService(new InvalidOperationException("нет сети"));
            var game = new GameInfo { GameId = "game", LatestVersion = "1.0.0", IsInstalled = true, NeedsUpdate = false };

            await Verifier(sync).VerifyAsync(game);

            Assert.True(game.IsInstalled);
            Assert.False(game.NeedsUpdate);
        }

        /// <summary>Отклонённый манифест — тоже не повод менять статус в фоновой проверке.</summary>
        [Fact]
        public async Task ОтклонённыйМанифестНеМеняетСтатус() {
            using var games = new GamesPathScope();
            WriteGameFile(games.Root, "game");
            var sync = new FakeSyncService(new ManifestValidationException("опасный путь"));
            var game = new GameInfo { GameId = "game", LatestVersion = "1.0.0", IsInstalled = true };

            await Verifier(sync).VerifyAsync(game);

            Assert.True(game.IsInstalled);
        }

        /// <summary>
        /// Статус считается известным даже при сбое проверки: иначе кнопка действия
        /// останется заблокированной в «Проверке…» до конца сессии (C4).
        /// </summary>
        [Fact]
        public async Task ПослеСбояСтатусВсёРавноСчитаетсяИзвестным() {
            using var games = new GamesPathScope();
            var verified = new VerifiedGames();
            var sync = new FakeSyncService(new InvalidOperationException("нет сети"));

            await new GameStatusVerifier(sync, () => "https://example.test", new SpaceHint(), verified)
                .VerifyAsync(new GameInfo { GameId = "game", LatestVersion = "1.0.0" });

            Assert.True(verified.IsKnown("game"));
        }

        /// <summary>Оценка объёма закачки уходит в кеш — из неё складывается подсказка «Нужно: …».</summary>
        [Fact]
        public async Task ОценкаОбъёмаПопадаетВКеш() {
            using var games = new GamesPathScope();
            WriteGameFile(games.Root, "game");
            var hint = new SpaceHint();

            await new GameStatusVerifier(new FakeSyncService(PlanWithDownloads(1, 4096)), () => "https://example.test", hint, new VerifiedGames())
                .VerifyAsync(new GameInfo { GameId = "game", LatestVersion = "1.0.0" });

            Assert.True(hint.TryGet("game", out var need));
            Assert.Equal(4096, need);
        }

        /// <summary>Игра без идентификатора и вовсе отсутствующая игра не роняют фоновую проверку.</summary>
        [Fact]
        public async Task ИграБезИдентификатораНеРоняетПроверку() {
            using var games = new GamesPathScope();
            var sync = new FakeSyncService(new DiffPlan());

            await Verifier(sync).VerifyAsync(new GameInfo { GameId = string.Empty, LatestVersion = "1.0.0" });
            await Verifier(sync).VerifyAsync(null!);

            Assert.Equal(0, sync.ManifestRequests);
        }

        private static GameStatusVerifier Verifier(ISyncService sync) =>
            new GameStatusVerifier(sync, () => "https://example.test", new SpaceHint(), new VerifiedGames());

        private static DiffPlan PlanWithDownloads(int count, long bytes) {
            var plan = new DiffPlan { TotalDownloadBytes = bytes, TotalFilesToDownload = count };
            for (var i = 0; i < count; i++) {
                plan.Downloads.Add(new FileTask { RelativePath = $"file{i}.bin" });
            }

            return plan;
        }

        private static string WriteGameFile(string gamesRoot, string gameId) {
            var root = Path.Combine(gamesRoot, gameId);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "game.exe"), "MZ");
            return root;
        }
    }

    /// <summary>
    /// Служба синхронизации без сети и без диска: отдаёт заранее заданный план
    /// либо заранее заданную ошибку. Настоящая ходит на сервер и хеширует гигабайты.
    /// </summary>
    internal sealed class FakeSyncService : ISyncService {
        private readonly DiffPlan? plan;
        private readonly Exception? failure;

        internal FakeSyncService(DiffPlan plan) => this.plan = plan;

        internal FakeSyncService(Exception failure) => this.failure = failure;

        /// <summary>Gets сколько раз запрашивали манифест: ноль означает «до сети дело не дошло».</summary>
        internal int ManifestRequests { get; private set; }

        public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
            this.ManifestRequests++;
            if (this.failure != null) {
                throw this.failure;
            }

            return Task.FromResult(new Manifest { Version = "1.0.0", Files = new List<ManifestFile>() });
        }

        public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
            => Task.FromResult(this.plan ?? new DiffPlan());

        public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
            => this.PlanAsync(manifest, localRoot, contentBaseUrl, ct);

        public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) => Task.CompletedTask;
    }
}
