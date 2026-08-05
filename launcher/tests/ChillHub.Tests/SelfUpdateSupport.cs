// <copyright file="SelfUpdateSupport.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Sync;

    /// <summary>
    /// Стенд самообновления: настоящая папка «установки» и настоящий %TEMP%, только
    /// временные. Без этого ни один тест самообновления запустить нельзя — код
    /// раньше ходил прямо в каталог работающего лаунчера.
    /// </summary>
    internal sealed class SelfUpdateStand : IDisposable {
        private readonly TempDir install = new TempDir();
        private readonly TempDir temp = new TempDir();
        private readonly TempDir state = new TempDir();

        /// <summary>Папка «установки» лаунчера.</summary>
        internal TempDir Install => this.install;

        /// <summary>Корень временных сессий обновления.</summary>
        internal TempDir Temp => this.temp;

        internal SelfUpdatePaths Paths => new SelfUpdatePaths(this.install.Root, this.temp.Root);

        /// <summary>Счётчик попыток в отдельном файле — общий процессный не трогаем.</summary>
        internal UpdateAttemptsStore Attempts => new UpdateAttemptsStore(Path.Combine(this.state.Root, "attempts.txt"));

        public void Dispose() {
            this.install.Dispose();
            this.temp.Dispose();
            this.state.Dispose();
        }
    }

    /// <summary>Подставная синхронизация: манифест и загрузка задаются тестом, в сеть никто не ходит.</summary>
    internal sealed class FakeSync : ISyncService {
        internal Func<string, Manifest>? OnManifest { get; set; }

        internal Func<DiffPlan, Task>? OnExecute { get; set; }

        /// <summary>План, с которым позвали ExecuteAsync — по нему проверяется проверка места.</summary>
        internal DiffPlan? LastPlan { get; private set; }

        /// <summary>Адреса запрошенных манифестов.</summary>
        internal List<string> ManifestUrls { get; } = new List<string>();

        /// <summary>Отчёт о прогрессе, который ExecuteAsync отдаст подписчику.</summary>
        internal SyncProgress? Emit { get; set; }

        public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
            this.ManifestUrls.Add(manifestUrl);
            var factory = this.OnManifest ?? (_ => new Manifest());
            return Task.FromResult(factory(manifestUrl));
        }

        public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
            this.LastPlan = plan;
            if (this.Emit != null) {
                progress?.Report(this.Emit);
            }

            return this.OnExecute?.Invoke(plan) ?? Task.CompletedTask;
        }
    }

    /// <summary>Подставной транспорт: latest.json отдаёт тест, настоящая сеть не задействована.</summary>
    internal sealed class SelfUpdateHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;

        internal SelfUpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => this.reply = reply;

        /// <summary>Отвечает готовым JSON.</summary>
        internal static SelfUpdateHandler Json(string body) => new SelfUpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

        /// <summary>Изображает обрыв связи.</summary>
        internal static SelfUpdateHandler Offline(string message = "сеть недоступна")
            => new SelfUpdateHandler(_ => throw new HttpRequestException(message));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(this.reply(request));
    }

    /// <summary>Сборка манифестов и записей о файлах для тестов самообновления.</summary>
    internal static class SelfUpdateManifest {
        /// <summary>Запись манифеста, ТОЧНО описывающая существующий файл (размер и оба хеша).</summary>
        internal static ManifestFile Matching(string root, string rel) {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            return new ManifestFile {
                Path = rel,
                Size = new FileInfo(full).Length,
                Sha256 = TestHash.Sha256OfFile(full),
                Blake3 = TestHash.Blake3OfFile(full),
            };
        }

        /// <summary>Запись манифеста про файл, которого на диске нет или он другой.</summary>
        internal static ManifestFile Different(string rel, long size = 10)
            => new ManifestFile {
                Path = rel,
                Size = size,
                Sha256 = new string('a', 64),
                Blake3 = new string('b', 64),
            };

        internal static Manifest Of(params ManifestFile[] files)
            => new Manifest { Version = "1.2.4", GameId = "launcher", Files = new List<ManifestFile>(files) };
    }

    /// <summary>Собирает применённые к окну состояния, чтобы проверять то, что увидит пользователь.</summary>
    internal sealed class UiRecorder {
        private readonly List<SelfUpdateUiState> states = new List<SelfUpdateUiState>();

        internal IReadOnlyList<SelfUpdateUiState> States => this.states;

        internal Action<SelfUpdateUiState> Apply => s => this.states.Add(s);

        /// <summary>Последний показанный текст статуса.</summary>
        internal string? LastStatus {
            get {
                for (var i = this.states.Count - 1; i >= 0; i--) {
                    if (this.states[i].StatusText != null) {
                        return this.states[i].StatusText;
                    }
                }

                return null;
            }
        }

        /// <summary>Итоговая доступность кнопки.</summary>
        internal bool? LastButtonEnabled {
            get {
                for (var i = this.states.Count - 1; i >= 0; i--) {
                    if (this.states[i].ButtonEnabled.HasValue) {
                        return this.states[i].ButtonEnabled;
                    }
                }

                return null;
            }
        }

        /// <summary>Итоговая подпись кнопки.</summary>
        internal string? LastButtonContent {
            get {
                for (var i = this.states.Count - 1; i >= 0; i--) {
                    if (this.states[i].ButtonContent != null) {
                        return this.states[i].ButtonContent;
                    }
                }

                return null;
            }
        }
    }

    /// <summary>Держит замок на каталог установки из ЧУЖОГО потока: мьютекс реентрантен для своего.</summary>
    internal sealed class ForeignUpdateLock : IDisposable {
        private readonly ManualResetEventSlim acquired = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
        private readonly Thread thread;

        internal ForeignUpdateLock(string installDir) {
            this.thread = new Thread(() => {
                ChillHub.Update.UpdateLock.TryAcquire(installDir, 0, out var mutex);
                this.acquired.Set();
                this.release.Wait();
                ChillHub.Update.UpdateLock.Release(mutex);
            }) {
                IsBackground = true,
            };
            this.thread.Start();
            this.acquired.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose() {
            this.release.Set();
            this.thread.Join(TimeSpan.FromSeconds(5));
            this.acquired.Dispose();
            this.release.Dispose();
        }
    }
}
