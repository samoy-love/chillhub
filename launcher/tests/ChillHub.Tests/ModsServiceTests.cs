// <copyright file="ModsServiceTests.cs" company="PlaceholderCompany">
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
    using ChillHub.Core.Mods;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Установка модпака в папку игры.
    /// <para>
    /// Здесь проверяется не скачивание (его делает общий движок синхронизации, у него
    /// свои тесты), а то, что модпак правильно ЗАПИСЫВАЕТ СВОЮ ПРИНАДЛЕЖНОСТЬ: маркер
    /// версии и копию манифеста. Без них следующая синхронизация игры посчитает моды
    /// мусором и удалит их, а следующая синхронизация модпака не будет знать, что
    /// удалять из прошлой версии. Отдельно проверен случай миграции, ради которого
    /// вся эта запись и делается на ветке «скачивать нечего».
    /// </para>
    /// </summary>
    public class ModsServiceTests : IDisposable {
        private readonly string root;

        /// <summary>Инициализирует временный каталог под фикстуры.</summary>
        public ModsServiceTests() {
            this.root = Path.Combine(Path.GetTempPath(), "ChillHubModsService", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.root);
        }

        /// <inheritdoc/>
        public void Dispose() {
            try {
                Directory.Delete(this.root, true);
            }
            catch (IOException) {
                // Временный каталог не удалился — прогону это не мешает.
            }

            GC.SuppressFinalize(this);
        }

        private static GameInfo GameWithPack() => new() {
            GameId = "lethal-company",
            Mods = new ModsInfo {
                HasLatest = true,
                Version = "ASTeam-LethalReloaded-2.2.12",
                DisplayName = "Lethal Reloaded",
                DisplayVersion = "2.2.12",
                ManifestUrl = "/manifests/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12.json",
                ContentBaseUrl = "/content/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12/files",
            },
        };

        private static Manifest PackManifest() => new() {
            Version = "ASTeam-LethalReloaded-2.2.12",
            GameId = "lethal-company",
            Files = new List<ManifestFile> {
                new() { Path = "winhttp.dll", Size = 1, Blake3 = "aa" },
                new() { Path = "doorstop_config.ini", Size = 1, Blake3 = "bb" },
                new() { Path = "BepInEx/core/BepInEx.Preloader.dll", Size = 1, Blake3 = "cc" },
            },
        };

        /// <summary>Игра без модпака ничего не устанавливает и не пишет на диск.</summary>
        [Fact]
        public async Task БезМодпакаНичегоНеДелается() {
            var sync = new RecordingSync(new DiffPlan());

            var result = await ModsService.EnsureAsync(
                new GameInfo { GameId = "x" }, this.root, "https://example", sync, null, CancellationToken.None);

            Assert.Equal(ModsSyncOutcome.NoModpack, result.Outcome);
            Assert.True(result.Ok);
            Assert.False(sync.PlanCalled);
            Assert.Empty(GameLocalState.ReadModsVersionAt(this.root));
        }

        /// <summary>Успешная установка пишет и маркер версии, и копию манифеста.</summary>
        [Fact]
        public async Task УстановкаЗаписываетВерсиюИМанифест() {
            var plan = new DiffPlan { TotalDownloadBytes = 4096 };
            plan.Downloads.Add(new FileTask { RelativePath = "winhttp.dll" });
            var sync = new RecordingSync(plan, PackManifest());

            var result = await ModsService.EnsureAsync(
                GameWithPack(), this.root, "https://example/", sync, null, CancellationToken.None);

            Assert.Equal(ModsSyncOutcome.Installed, result.Outcome);
            Assert.Equal(4096, result.Downloaded);
            Assert.True(sync.ExecuteCalled);
            Assert.Equal("ASTeam-LethalReloaded-2.2.12", GameLocalState.ReadModsVersionAt(this.root));

            var owned = GameLocalState.ReadInstalledModPackPaths(this.root);
            Assert.Contains("winhttp.dll", owned);
            Assert.Contains("BepInEx/core/BepInEx.Preloader.dll", owned);
        }

        /// <summary>
        /// Случай миграции: файлы модов уже лежат на диске с теми же хешами, скачивать
        /// нечего — но принадлежность записать НАДО. Иначе следующая синхронизация игры
        /// увидит 2400 «лишних» файлов BepInEx и снесёт их.
        /// </summary>
        [Fact]
        public async Task ПриПустомПланеПринадлежностьВсёРавноЗаписывается() {
            var sync = new RecordingSync(new DiffPlan(), PackManifest());

            var result = await ModsService.EnsureAsync(
                GameWithPack(), this.root, "https://example", sync, null, CancellationToken.None);

            Assert.Equal(ModsSyncOutcome.UpToDate, result.Outcome);
            Assert.False(sync.ExecuteCalled);
            Assert.Equal("ASTeam-LethalReloaded-2.2.12", GameLocalState.ReadModsVersionAt(this.root));
            Assert.Equal(3, GameLocalState.ReadInstalledModPackPaths(this.root).Count);
        }

        /// <summary>План строится по правилам модпака, а не игры: иначе он снёс бы игру.</summary>
        [Fact]
        public async Task ПланСтроитсяВРежимеМодпака() {
            var sync = new RecordingSync(new DiffPlan(), PackManifest());

            await ModsService.EnsureAsync(
                GameWithPack(), this.root, "https://example", sync, null, CancellationToken.None);

            Assert.NotNull(sync.Options);
            Assert.Equal(ManifestScope.OwnFilesOnly, sync.Options!.Scope);
        }

        /// <summary>Адреса манифеста и файлов склеиваются с базой API без задвоенных слешей.</summary>
        [Fact]
        public async Task АдресаСклеиваютсяКорректно() {
            var sync = new RecordingSync(new DiffPlan(), PackManifest());

            await ModsService.EnsureAsync(
                GameWithPack(), this.root, "https://launcher.example/", sync, null, CancellationToken.None);

            Assert.Equal(
                "https://launcher.example/manifests/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12.json",
                sync.ManifestUrl);
            Assert.Equal(
                "https://launcher.example/content/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12/files",
                sync.ContentBaseUrl);
        }

        /// <summary>
        /// Сбой не должен оставлять маркер: иначе лаунчер считал бы полуустановленный
        /// модпак установленным и больше не пытался бы его починить.
        /// </summary>
        [Fact]
        public async Task ПриСбоеМаркерНеПишется() {
            var sync = new ThrowingSync(new InvalidOperationException("сеть отвалилась"));

            var result = await ModsService.EnsureAsync(
                GameWithPack(), this.root, "https://example", sync, null, CancellationToken.None);

            Assert.Equal(ModsSyncOutcome.Failed, result.Outcome);
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Message);
            Assert.Empty(GameLocalState.ReadModsVersionAt(this.root));
        }

        /// <summary>Отмена пробрасывается наружу, а не превращается в «не удалось».</summary>
        [Fact]
        public async Task ОтменаПробрасывается() {
            var sync = new ThrowingSync(new OperationCanceledException());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModsService.EnsureAsync(
                GameWithPack(), this.root, "https://example", sync, null, CancellationToken.None));
        }

        /// <summary>Пустой путь к папке — отказ с внятным текстом, а не исключение.</summary>
        [Fact]
        public async Task БезПапкиИгрыУстановкаОтклоняется() {
            var sync = new RecordingSync(new DiffPlan(), PackManifest());

            var result = await ModsService.EnsureAsync(
                GameWithPack(), string.Empty, "https://example", sync, null, CancellationToken.None);

            Assert.Equal(ModsSyncOutcome.Failed, result.Outcome);
            Assert.False(sync.PlanCalled);
        }

        /// <summary>
        /// Отключение модов не удаляет файлы — только выключает Doorstop. Удаление
        /// полутора гигабайт ради «поиграть без модов» никому не нужно.
        /// </summary>
        [Fact]
        public void ОтключениеНеУдаляетФайлы() {
            var dir = Path.Combine(this.root, "game");
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(dir, DoorstopConfig.FileName), "[General]\nenabled = true\n");
            File.WriteAllText(Path.Combine(dir, "BepInEx", "plugins", "Mod.dll"), "mod");

            Assert.True(ModsService.Disable(dir));

            Assert.False(DoorstopConfig.ReadEnabled(dir));
            Assert.True(File.Exists(Path.Combine(dir, "BepInEx", "plugins", "Mod.dll")));
        }

        /// <summary>Подставная синхронизация, запоминающая, с чем её вызвали.</summary>
        private sealed class RecordingSync : ISyncService {
            private readonly DiffPlan plan;
            private readonly Manifest manifest;

            internal RecordingSync(DiffPlan plan, Manifest? manifest = null) {
                this.plan = plan;
                this.manifest = manifest ?? new Manifest();
            }

            internal bool PlanCalled { get; private set; }

            internal bool ExecuteCalled { get; private set; }

            internal string ManifestUrl { get; private set; } = string.Empty;

            internal string ContentBaseUrl { get; private set; } = string.Empty;

            internal PlanOptions? Options { get; private set; }

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
                this.ManifestUrl = manifestUrl;
                return Task.FromResult(this.manifest);
            }

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => this.PlanAsync(manifest, localRoot, contentBaseUrl, PlanOptions.Default, ct);

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct) {
                this.PlanCalled = true;
                this.ContentBaseUrl = contentBaseUrl;
                this.Options = options;
                return Task.FromResult(this.plan);
            }

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
                this.ExecuteCalled = true;
                return Task.CompletedTask;
            }
        }

        /// <summary>Подставная синхронизация, всегда падающая заданным исключением.</summary>
        private sealed class ThrowingSync : ISyncService {
            private readonly Exception error;

            internal ThrowingSync(Exception error) => this.error = error;

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) => throw this.error;

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => throw this.error;

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => throw this.error;

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct)
                => throw this.error;
        }
    }
}
