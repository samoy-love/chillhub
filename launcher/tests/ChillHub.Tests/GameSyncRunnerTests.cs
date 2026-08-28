// <copyright file="GameSyncRunnerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Home;
    using ChillHub.Core.Mods;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Установка, обновление и переключение версии игры со страницы игры.
    /// <para>
    /// Это самая дорогая операция лаунчера: она пишет в папку игры и способна удалить
    /// оттуда чужие файлы. Проверяется, что ни один отказ не выдаётся за успех (маркер
    /// версии пишется ТОЛЬКО после реально выполненной синхронизации), что нехватка
    /// места и запущенная игра останавливают операцию ДО первого скачанного байта и что
    /// удаление лишних файлов не происходит без ответа «да».
    /// </para>
    /// </summary>
    public class GameSyncRunnerTests {
        /// <summary>Успешная операция пишет маркер версии и говорит «Готово».</summary>
        [Fact]
        public async Task УспехПишетМаркерВерсииИСообщаетГотово() {
            var probe = new UiProbe();
            var runner = NewRunner(new FakeSync(), probe, out var written);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.Equal(("game", "1.2.0"), Assert.Single(written));
            Assert.Equal("Готово.", probe.LastStatus);
        }

        /// <summary>
        /// Успешная операция помечает локальное состояние изменённым: по этому признаку
        /// главная страница перечитывает статусы игр, иначе после возврата она показывает
        /// «не установлена» на только что поставленной игре.
        /// </summary>
        [Fact]
        public async Task УспехПомечаетЛокальноеСостояниеИзменённым() {
            GameLocalStateChanges.Consume(); // сбрасываем след предыдущих проверок
            var runner = NewRunner(new FakeSync(), new UiProbe(), out _);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.True(GameLocalStateChanges.Consume());
            Assert.False(GameLocalStateChanges.Consume());
        }

        /// <summary>
        /// Прерванная операция состояние изменённым не помечает: перечитывать нечего,
        /// а лишний обход папок игр на главной странице стоит секунд на медленном диске.
        /// </summary>
        [Fact]
        public async Task ОтменаНеПомечаетЛокальноеСостояние() {
            GameLocalStateChanges.Consume();
            var sync = new FakeSync { OnExecute = () => throw new OperationCanceledException() };
            var runner = NewRunner(sync, new UiProbe(), out _);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.False(GameLocalStateChanges.Consume());
        }

        /// <summary>
        /// Отмена не пишет маркер версии. Иначе прерванная закачка выдала бы папку
        /// с половиной файлов за установленную сборку.
        /// </summary>
        [Fact]
        public async Task ОтменаНеПишетМаркерВерсии() {
            var probe = new UiProbe();
            var sync = new FakeSync { OnExecute = () => throw new OperationCanceledException() };
            var runner = NewRunner(sync, probe, out var written);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.Empty(written);
            Assert.Equal("Операция отменена.", probe.LastStatus);
            Assert.Empty(probe.Errors);
        }

        /// <summary>Отменённая операция не оставляет на экране строку скорости от прошлой закачки.</summary>
        [Fact]
        public async Task ОтменаСбрасываетСтрокуСкорости() {
            var probe = new UiProbe();
            var sync = new FakeSync { OnExecute = () => throw new OperationCanceledException() };
            var runner = NewRunner(sync, probe, out _);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.Equal(string.Empty, probe.LastSpeedEta);
        }

        /// <summary>
        /// Ошибка записи файлов объясняется по-человечески: место и права — то, что
        /// пользователь может проверить сам.
        /// </summary>
        [Fact]
        public async Task ОшибкаЗаписиОбъясняетсяМестомИПравами() {
            var probe = new UiProbe();
            var sync = new FakeSync { OnExecute = () => throw new IOException("диск переполнен") };
            var runner = NewRunner(sync, probe, out var written);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.Empty(written);
            Assert.Equal(
                "Не удалось записать файлы игры. Проверьте свободное место и права доступа.",
                Assert.Single(probe.Errors).Message);
        }

        /// <summary>Любой другой сбой даёт общее сообщение, но всё равно попадает в лог с местом ошибки.</summary>
        [Fact]
        public async Task ПрочийСбойДаётОбщееСообщениеСКонтекстом() {
            var probe = new UiProbe();
            var sync = new FakeSync { OnExecute = () => throw new InvalidOperationException("что-то пошло не так") };
            var runner = NewRunner(sync, probe, out _);

            await runner.RunAsync(Request(), CancellationToken.None);

            var error = Assert.Single(probe.Errors);
            Assert.Equal("Не удалось завершить операцию. Попробуйте ещё раз.", error.Message);
            Assert.Contains("gid=game", error.Context, StringComparison.Ordinal);
            Assert.Contains("version=1.2.0", error.Context, StringComparison.Ordinal);
        }

        /// <summary>
        /// Отклонённый манифест — отдельное сообщение: файлы игры не тронуты, и говорить
        /// «попробуйте ещё раз» здесь неверно.
        /// </summary>
        [Fact]
        public async Task ОтклонённыйМанифестПолучаетСвоёСообщение() {
            var probe = new UiProbe();
            var sync = new FakeSync { OnManifest = () => throw new ManifestValidationException("опасный путь") };
            var runner = NewRunner(sync, probe, out var written);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.Empty(written);
            var error = Assert.Single(probe.Errors);
            Assert.Equal(ManifestValidator.UserMessage, error.Message);
            Assert.Contains("ManifestValidation", error.Context, StringComparison.Ordinal);
        }

        /// <summary>
        /// Не хватает места — операция останавливается до скачивания. Иначе закачка
        /// упирается в переполненный диск где-то на середине и оставляет мусор.
        /// </summary>
        [Fact]
        public async Task НехваткаМестаОстанавливаетДоСкачивания() {
            var probe = new UiProbe();
            var sync = new FakeSync { Plan = PlanWith(totalBytes: 1000) };
            var runner = NewRunner(sync, probe, out var written);
            runner.FreeSpaceFor = _ => 10;

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.Equal("Недостаточно свободного места.", probe.LastStatus);
            Assert.False(sync.Executed);
            Assert.Empty(written);
        }

        /// <summary>
        /// Неизвестное свободное место (0 — сетевой путь, отсутствующий диск) не считается
        /// нехваткой: отказать в установке из-за неудавшейся диагностики хуже, чем попробовать.
        /// </summary>
        [Fact]
        public async Task НеизвестноеСвободноеМестоНеОстанавливаетОперацию() {
            var probe = new UiProbe();
            var sync = new FakeSync { Plan = PlanWith(totalBytes: 1000) };
            var runner = NewRunner(sync, probe, out _);
            runner.FreeSpaceFor = _ => 0;

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.True(sync.Executed);
        }

        /// <summary>Объём закачки и свободное место показываются рядом — так видно, чего не хватает.</summary>
        [Fact]
        public async Task ОбъёмЗакачкиПоказываетсяРядомСоСвободнымМестом() {
            var probe = new UiProbe();
            var sync = new FakeSync { Plan = PlanWith(totalBytes: 2048) };
            var runner = NewRunner(sync, probe, out _);
            runner.FreeSpaceFor = _ => 4096;

            await runner.RunAsync(Request(), CancellationToken.None);

            // Сам формат размера проверяется отдельно; здесь важен состав строки,
            // а не разделитель дробной части — он зависит от локали машины.
            Assert.Equal(
                $"Нужно: {HomeFormat.FormatSize(2048)} ({HomeFormat.FormatSize(4096)} доступно)",
                probe.LastFilesSize);
        }

        /// <summary>Когда качать нечего, строку с объёмом не рисуем — сверка файлов ничего не скачивает.</summary>
        [Fact]
        public async Task ПриПустойЗакачкеСтрокаОбъёмаНеПоявляется() {
            var probe = new UiProbe();
            var runner = NewRunner(new FakeSync(), probe, out _);

            await runner.RunAsync(Request(), CancellationToken.None);

            Assert.Null(probe.LastFilesSize);
        }

        /// <summary>
        /// Проверка файлов спрашивает перед удалением: в папке игры лежат моды и сохранения,
        /// которых нет в манифесте, и стереть их молча нельзя.
        /// </summary>
        [Fact]
        public async Task ПроверкаФайловСпрашиваетПередУдалением() {
            var probe = new UiProbe { ConfirmAnswer = false };
            var sync = new FakeSync { Plan = PlanWith(toDelete: new List<string> { "mods/a.dll", "save.dat" }) };
            var runner = NewRunner(sync, probe, out var written);

            await runner.RunAsync(Request(confirmDeletions: true), CancellationToken.None);

            Assert.Equal("Проверка отменена.", probe.LastStatus);
            Assert.False(sync.Executed);
            Assert.Empty(written);
            Assert.Equal("Проверка файлов", Assert.Single(probe.Questions).Title);
        }

        /// <summary>Согласие на удаление доводит проверку до конца.</summary>
        [Fact]
        public async Task СогласиеНаУдалениеДоводитПроверкуДоКонца() {
            var probe = new UiProbe { ConfirmAnswer = true };
            var sync = new FakeSync { Plan = PlanWith(toDelete: new List<string> { "mods/a.dll" }) };
            var runner = NewRunner(sync, probe, out _);

            await runner.RunAsync(Request(confirmDeletions: true), CancellationToken.None);

            Assert.True(sync.Executed);
        }

        /// <summary>Удалять нечего — вопрос не задаём: лишний модальный вопрос обесценивает предупреждение.</summary>
        [Fact]
        public async Task БезЛишнихФайловВопросНеЗадаётся() {
            var probe = new UiProbe { ConfirmAnswer = false };
            var runner = NewRunner(new FakeSync(), probe, out _);

            await runner.RunAsync(Request(confirmDeletions: true), CancellationToken.None);

            Assert.Empty(probe.Questions);
        }

        /// <summary>
        /// Обычная установка не спрашивает про удаление, даже когда план что-то удаляет:
        /// вопрос относится только к сверке файлов уже установленной игры.
        /// </summary>
        [Fact]
        public async Task ОбычнаяУстановкаПроУдалениеНеСпрашивает() {
            var probe = new UiProbe { ConfirmAnswer = false };
            var sync = new FakeSync { Plan = PlanWith(toDelete: new List<string> { "old.pak" }) };
            var runner = NewRunner(sync, probe, out _);

            await runner.RunAsync(Request(confirmDeletions: false), CancellationToken.None);

            Assert.Empty(probe.Questions);
            Assert.True(sync.Executed);
        }

        /// <summary>Вопрос об удалении называет число файлов и версию: без них согласие вслепую.</summary>
        [Fact]
        public void ВопросОбУдаленииНазываетЧислоФайловИВерсию() {
            var text = GameSyncRunner.DeletionConfirmText("1.2.0", 7);

            Assert.Contains("версии 1.2.0: 7", text, StringComparison.Ordinal);
            Assert.Contains("моды", text, StringComparison.Ordinal);
        }

        /// <summary>Запущенная игра останавливает операцию: её файлы заняты и заменить их нельзя.</summary>
        [Fact]
        public async Task ЗапущеннаяИграОстанавливаетОперацию() {
            var probe = new UiProbe();
            var sync = new FakeSync();
            var runner = NewRunner(sync, probe, out var written);
            using var processes = new RunningProcessScope("Lethal");

            await runner.RunAsync(Request(exeRelativePath: @"bin\Lethal.exe"), CancellationToken.None);

            Assert.Equal("Игра запущена (Lethal). Закройте игру и повторите.", probe.LastStatus);
            Assert.False(sync.Executed);
            Assert.Empty(written);
        }

        /// <summary>Игра без известного exe не считается запущенной — иначе установка была бы невозможна.</summary>
        [Fact]
        public async Task ИграБезИзвестногоExeСчитаетсяЗакрытой() {
            var probe = new UiProbe();
            var sync = new FakeSync();
            var runner = NewRunner(sync, probe, out _);
            using var processes = new RunningProcessScope("Lethal");

            await runner.RunAsync(Request(exeRelativePath: null), CancellationToken.None);

            Assert.True(sync.Executed);
        }

        /// <summary>Без идентификатора игры операцию не начинаем — ставить некуда.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void БезИдентификатораИгрыОперацияНеНачинается(string? gameId) {
            var probe = new UiProbe();
            var runner = NewRunner(new FakeSync(), probe, out _);

            Assert.False(runner.TryBegin(gameId, isInstalled: false));
            Assert.Equal("Не удалось определить игру", probe.LastStatus);
        }

        /// <summary>
        /// Технические работы останавливают установку и объясняют причину: работы могли
        /// начаться уже после того, как кнопки нарисовались доступными.
        /// </summary>
        [Fact]
        public void ТехническиеРаботыОстанавливаютУстановку() {
            var probe = new UiProbe();
            var runner = NewRunner(new FakeSync(), probe, out _);
            runner.Maintenance = () => new MaintenanceStateView(BlocksInstall: true, BlocksUpdate: false, "Идут работы");

            Assert.False(runner.TryBegin("game", isInstalled: false));
            Assert.Equal("Идут работы", probe.LastStatus);
            Assert.Equal(1, probe.MaintenanceApplied);
        }

        /// <summary>
        /// Для установленной игры смотрим на запрет ОБНОВЛЕНИЯ, а не установки: это разные
        /// флаги, и сервер может запретить только одно из двух.
        /// </summary>
        [Fact]
        public void ДляУстановленнойИгрыСмотримНаЗапретОбновления() {
            var probe = new UiProbe();
            var runner = NewRunner(new FakeSync(), probe, out _);
            runner.Maintenance = () => new MaintenanceStateView(BlocksInstall: false, BlocksUpdate: true, "Идут работы");

            Assert.False(runner.TryBegin("game", isInstalled: true));
            Assert.True(runner.TryBegin("game", isInstalled: false));
        }

        /// <summary>Работы не идут — операция начинается, кнопки трогать незачем.</summary>
        [Fact]
        public void БезРаботОперацияНачинается() {
            var probe = new UiProbe();
            var runner = NewRunner(new FakeSync(), probe, out _);

            Assert.True(runner.TryBegin("game", isInstalled: true));
            Assert.Equal(0, probe.MaintenanceApplied);
        }

        private static GameSyncRequest Request(
            bool confirmDeletions = false,
            string? exeRelativePath = null)
            => new GameSyncRequest(
                "game",
                "1.2.0",
                "https://example.test",
                @"C:\games\game",
                exeRelativePath,
                confirmDeletions);

        /// <summary>
        /// Установка модпака обязана отчитываться о прогрессе тем же путём, что и игра.
        /// <para>
        /// Раньше в <c>ModsService.EnsureAsync</c> уходил <c>null</c>: полтора гигабайта
        /// модов уезжали при неподвижной полосе и одной строке «Установка модов…». Со
        /// стороны игрока это неотличимо от зависшего лаунчера — с чем он и пришёл.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПрогрессМодпакаДоходитДоЭкранаИПомеченКакМоды() {
            using var dir = new TempDir();
            var probe = new UiProbe();
            var plan = new DiffPlan { TotalDownloadBytes = 4096 };
            plan.Downloads.Add(new FileTask { RelativePath = "winhttp.dll" });
            var sync = new FakeSync { Plan = plan };
            sync.OnProgress = p => p.Report(new SyncProgress {
                Stage = "Downloading",
                BytesDownloaded = 2048,
                TotalBytes = 4096,
                FilesDownloaded = 1,
                TotalFiles = 2,
            });

            var runner = NewRunner(sync, probe, out _);
            var game = new GameInfo {
                GameId = "game",
                Mods = new ModsInfo {
                    HasLatest = true,
                    Version = "ASTeam-LethalReloaded-2.2.12",
                    ManifestUrl = "/manifests/_mods/game/v.json",
                    ContentBaseUrl = "/content/_mods/game/v/files",
                },
            };
            var request = new GameSyncRequest(
                "game", "1.2.0", "https://example.test", dir.Root, null, false, SyncKind.Update, game);

            await runner.RunAsync(request, CancellationToken.None);

            var reports = await probe.WaitForProgress(all =>
                all.Any(p => p.Scope == ModsService.ScopeName) && all.Any(p => string.IsNullOrEmpty(p.Scope)));

            Assert.Contains(reports, p => p.Scope == ModsService.ScopeName);
            Assert.Contains(reports, p => string.IsNullOrEmpty(p.Scope));
        }

        /// <summary>
        /// Строки «Скорость» и «файлов • байт» от закончившегося модпака не должны
        /// висеть над начавшейся закачкой игры: объём в них уже не тот.
        /// </summary>
        [Fact]
        public async Task ПослеМодпакаСтрокиОбъёмаОчищаются() {
            using var dir = new TempDir();
            var probe = new UiProbe();
            var sync = new FakeSync { Plan = new DiffPlan() };
            var runner = NewRunner(sync, probe, out _);
            var game = new GameInfo {
                GameId = "game",
                Mods = new ModsInfo {
                    HasLatest = true,
                    Version = "v1",
                    ManifestUrl = "/m.json",
                    ContentBaseUrl = "/c",
                },
            };
            var request = new GameSyncRequest(
                "game", "1.2.0", "https://example.test", dir.Root, null, false, SyncKind.Update, game);

            await runner.RunAsync(request, CancellationToken.None);

            Assert.Equal(string.Empty, probe.LastSpeedEta);
        }

        private static DiffPlan PlanWith(long totalBytes = 0, List<string>? toDelete = null)
            => new DiffPlan { TotalDownloadBytes = totalBytes, ToDelete = toDelete ?? new List<string>() };

        private static GameSyncRunner NewRunner(FakeSync sync, UiProbe probe, out List<(string? GameId, string? Version)> written) {
            var log = new List<(string? GameId, string? Version)>();
            written = log;
            var runner = new GameSyncRunner(sync, probe.ToUi());
            runner.Maintenance = () => new MaintenanceStateView(false, false, string.Empty);
            runner.FreeSpaceFor = _ => long.MaxValue;
            runner.WriteLocalVersion = (gid, version) => log.Add((gid, version));
            return runner;
        }

        /// <summary>Ошибка, показанная пользователю, вместе с местом, где её поймали.</summary>
        private sealed record ShownError(string Message, string? Context);

        /// <summary>Заданный пользователю вопрос.</summary>
        private sealed record AskedQuestion(string Text, string Title);

        /// <summary>
        /// Подставная служба синхронизации: даёт заранее заданный план и позволяет
        /// уронить любой шаг — манифест, план или саму закачку.
        /// </summary>
        private sealed class FakeSync : ISyncService {
            internal DiffPlan Plan { get; set; } = new DiffPlan();

            internal Action? OnManifest { get; set; }

            internal Action? OnExecute { get; set; }

            internal Action<IProgress<SyncProgress>>? OnProgress { get; set; }

            internal bool Executed { get; private set; }

            public Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
                this.OnManifest?.Invoke();
                return Task.FromResult(new Manifest());
            }

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => Task.FromResult(this.Plan);

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => Task.FromResult(this.Plan);

            public Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
                this.OnExecute?.Invoke();
                this.OnProgress?.Invoke(progress);
                this.Executed = true;
                return Task.CompletedTask;
            }
        }

        /// <summary>Экран страницы игры, заменённый на список того, что на нём написали.</summary>
        private sealed class UiProbe {
            internal List<string> Statuses { get; } = new();

            internal List<ShownError> Errors { get; } = new();

            internal List<AskedQuestion> Questions { get; } = new();

            /// <summary>
            /// Отчёты о прогрессе. Читать только через <see cref="ProgressSnapshot"/>.
            /// <para>
            /// ОТЧЁТЫ ПРИХОДЯТ НЕ ИЗ ТОГО ПОТОКА, ГДЕ ИДЁТ ТЕСТ. Прогресс модпака
            /// доставляет <see cref="System.Progress{T}"/>, а он вызывает обработчик
            /// через контекст синхронизации; в тестовом прогоне контекста нет, и
            /// вызов уходит в пул потоков — то есть может случиться уже после того,
            /// как RunAsync вернул управление. Перебор этого списка прямо в
            /// утверждении падал с «Collection was modified» — не всегда, а когда
            /// повезёт с расписанием, отчего на машине разработчика тест проходил, а
            /// на CI нет.
            /// </para>
            /// </summary>
            private List<SyncProgress> Progress { get; } = new();

            /// <summary>Копия отчётов на текущий момент.</summary>
            internal SyncProgress[] ProgressSnapshot() {
                lock (this.Progress) {
                    return this.Progress.ToArray();
                }
            }

            /// <summary>
            /// Ждёт, пока в отчётах не появится нужное, и возвращает их копию.
            /// Срок — на случай, если не появится вовсе: тест обязан падать
            /// утверждением, а не зависанием.
            /// </summary>
            /// <param name="ready">Условие на накопленные отчёты.</param>
            /// <returns>Отчёты на момент выполнения условия или истечения срока.</returns>
            internal async Task<SyncProgress[]> WaitForProgress(Func<SyncProgress[], bool> ready) {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (true) {
                    var snapshot = this.ProgressSnapshot();
                    if (ready(snapshot) || DateTime.UtcNow > deadline) {
                        return snapshot;
                    }

                    await Task.Delay(10).ConfigureAwait(false);
                }
            }

            internal int MaintenanceApplied { get; private set; }

            internal bool ConfirmAnswer { get; set; }

            internal string? LastStatus => this.Statuses.Count > 0 ? this.Statuses[^1] : null;

            internal string? LastSpeedEta { get; private set; }

            internal string? LastFilesSize { get; private set; }

            internal GameSyncUi ToUi() => new GameSyncUi {
                SetStatus = text => this.Statuses.Add(text),
                SetSpeedEta = text => this.LastSpeedEta = text,
                SetFilesSize = text => this.LastFilesSize = text,
                ApplyMaintenanceToButtons = () => this.MaintenanceApplied++,
                ReportProgress = (p, _) => {
                    lock (this.Progress) {
                        this.Progress.Add(p);
                    }
                },
                Confirm = (text, title) => {
                    this.Questions.Add(new AskedQuestion(text, title));
                    return this.ConfirmAnswer;
                },
                ShowUserError = (message, ex, context) => this.Errors.Add(new ShownError(message, context)),
            };
        }

        /// <summary>
        /// Делает вид, что процесс с таким именем запущен. Настоящий опрос процессов
        /// зависит от того, что открыто на машине, — тест на нём был бы недетерминированным.
        /// </summary>
        private sealed class RunningProcessScope : IDisposable {
            private readonly Func<string, int> previous;

            internal RunningProcessScope(string runningName) {
                this.previous = GameDiskInfo.ProcessCountByName;
                GameDiskInfo.ProcessCountByName = name =>
                    string.Equals(name, runningName, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }

            public void Dispose() => GameDiskInfo.ProcessCountByName = this.previous;
        }
    }
}
