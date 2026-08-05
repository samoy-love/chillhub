// <copyright file="IntegrityPanelTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Settings;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Проверка целостности и восстановление файлов глазами страницы настроек.
    /// <para>
    /// Сюда приходят, когда игра уже не запускается, и цена ошибки одинаково высока
    /// в обе стороны: молча сказать «всё в порядке» — оставить человека с неработающей
    /// игрой, молча сказать «не удалось» — отправить его качать гигабайты заново.
    /// Сам счёт расхождений проверяется в <c>IntegrityCheckerTests</c>; здесь — то, что
    /// видит и чем управляет пользователь: строка состояния, кнопка восстановления,
    /// занятость панели, вопрос перед перекачиванием и отмена.
    /// </para>
    /// <para>
    /// Ни один тест не поднимает окно: панель про контролы не знает, всё видимое уходит
    /// в колбэки, а вопрос перед восстановлением — за швом <see cref="SettingsDialogs.Confirm"/>.
    /// </para>
    /// </summary>
    [Collection(ConfigStorageCollection.Name)]
    public class IntegrityPanelTests : IDisposable {
        public void Dispose() => SettingsDialogs.ResetDialogsForTests();

        // ---- Проверка ----

        /// <summary>
        /// Игру не выбрали — панель говорит об этом и НЕ уходит в занятое состояние:
        /// иначе кнопка проверки осталась бы заблокированной навсегда.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task БезВыбраннойИгрыПанельПроситВыбратьИНеЗанимается(string? gameId) {
            using var scope = new PanelScope();
            var game = gameId == null ? null : new GameInfo { GameId = gameId };

            await scope.Panel.CheckAsync(game);

            Assert.Equal("Выберите игру для проверки.", scope.LastStatus);
            Assert.False(scope.Panel.Busy);
            Assert.Empty(scope.BusyStates);
        }

        /// <summary>
        /// Игра не установлена — это отдельный, понятный человеку ответ. Иначе он выглядел
        /// бы как сбой проверки, и человек чинил бы то, чего у него нет. Путь к папке
        /// в текст не попадает: он уезжает в скриншоты и автоотчёты вместе с именем
        /// пользователя Windows.
        /// </summary>
        [Fact]
        public async Task НеустановленнаяИграДаётПонятныйОтветБезПути() {
            using var scope = new PanelScope();

            await scope.Panel.CheckAsync(new GameInfo { GameId = "нет-такой", LatestVersion = "1.0.0" });

            Assert.Contains("не установлена", scope.LastStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scope.GamesRoot, scope.LastStatus, StringComparison.OrdinalIgnoreCase);
            Assert.False(scope.RepairVisible);
        }

        /// <summary>
        /// Целая установка: панель говорит, что всё в порядке, и НЕ показывает кнопку
        /// восстановления. Лишняя кнопка «починить» на исправной игре толкает человека
        /// перекачивать гигабайты без причины.
        /// </summary>
        [Fact]
        public async Task ЦелаяУстановкаНеПредлагаетВосстановление() {
            using var scope = new PanelScope();
            scope.InstallGame(intact: 1, missing: 0, extra: 0);

            await scope.CheckAsync();

            Assert.Contains("Всё в порядке", scope.LastStatus, StringComparison.Ordinal);
            Assert.False(scope.RepairVisible);
            Assert.NotNull(scope.Panel.LastReport);
            Assert.False(scope.Panel.LastReport!.NeedsRepair);
        }

        /// <summary>
        /// Нашли расхождения — панель называет их и открывает восстановление. Без кнопки
        /// человеку остаётся только удалить игру и качать заново.
        /// </summary>
        [Fact]
        public async Task РасхожденияПоказываютсяИОткрываютВосстановление() {
            using var scope = new PanelScope();
            scope.InstallGame(intact: 1, missing: 2, extra: 1);

            await scope.CheckAsync();

            Assert.Contains("отсутствует — 2", scope.LastStatus, StringComparison.Ordinal);
            Assert.Contains("лишних — 1", scope.LastStatus, StringComparison.Ordinal);
            Assert.True(scope.RepairVisible);
            Assert.True(scope.Panel.LastReport!.NeedsRepair);
        }

        /// <summary>
        /// Панель занята на всё время проверки и освобождается после неё — в том числе
        /// когда проверка упала. Незанятая во время проверки означала бы вторую проверку
        /// поверх первой, занятая после — намертво заблокированную кнопку.
        /// </summary>
        [Fact]
        public async Task ПанельЗанимаетсяНаВремяПроверкиИОсвобождаетсяПослеСбоя() {
            using var scope = new PanelScope();
            scope.InstallGame(intact: 1, missing: 0, extra: 0);
            scope.Sync.ManifestError = new InvalidOperationException("сервер недоступен");

            await scope.CheckAsync();

            Assert.Equal(new[] { true, false }, scope.BusyStates);
            Assert.False(scope.Panel.Busy);
            Assert.Contains("сервер недоступен", scope.LastStatus, StringComparison.Ordinal);
        }

        /// <summary>Вторую проверку поверх идущей панель не начинает.</summary>
        [Fact]
        public async Task ВтораяПроверкаПоверхИдущейНеЗапускается() {
            using var scope = new PanelScope();
            scope.InstallGame(intact: 1, missing: 0, extra: 0);
            using var gate = new ManualResetEventSlim(false);
            scope.Sync.ManifestGate = gate;

            var first = scope.CheckAsync();
            await WaitUntil(() => scope.Panel.Busy, "панель должна занять себя первой проверкой");

            await scope.CheckAsync();
            Assert.Equal(new[] { true }, scope.BusyStates);

            gate.Set();
            await first;
        }

        /// <summary>
        /// Отмена остаётся отменой: подменять её словом «ошибка» нельзя — человек решит,
        /// что проверка сломана, и запустит её ещё раз.
        /// </summary>
        [Fact]
        public async Task ОтменённаяПроверкаНазываетсяОтменой() {
            using var scope = new PanelScope();
            scope.InstallGame(intact: 1, missing: 0, extra: 0);
            using var gate = new ManualResetEventSlim(false);
            scope.Sync.ManifestGate = gate;
            scope.Sync.ManifestError = new OperationCanceledException();

            var running = scope.CheckAsync();
            await WaitUntil(() => scope.Panel.Busy, "панель должна занять себя проверкой");

            scope.Panel.Cancel();
            Assert.Equal("Отмена…", scope.LastStatus);

            gate.Set();
            await running;

            Assert.Equal("Проверка отменена.", scope.LastStatus);
            Assert.False(scope.Panel.Busy);
        }

        /// <summary>Отмена без идущей проверки ничего не ломает — кнопку нажимают когда угодно.</summary>
        [Fact]
        public void ОтменаБезИдущейПроверкиНеПадает() {
            using var scope = new PanelScope();

            scope.Panel.Cancel();

            Assert.Equal("Отмена…", scope.LastStatus);
        }

        /// <summary>
        /// Уход со страницы отменяет проверку: она читает диск целыми гигабайтами,
        /// а результат уже некуда показать.
        /// </summary>
        [Fact]
        public async Task УходСоСтраницыОтменяетПроверку() {
            using var scope = new PanelScope();
            scope.InstallGame(intact: 1, missing: 0, extra: 0);
            using var gate = new ManualResetEventSlim(false);
            scope.Sync.ManifestGate = gate;
            scope.Sync.ManifestError = new OperationCanceledException();

            var running = scope.CheckAsync();
            await WaitUntil(() => scope.Panel.Busy, "панель должна занять себя проверкой");

            scope.Panel.LeavePage();
            gate.Set();
            await running;

            Assert.Equal("Проверка отменена.", scope.LastStatus);
        }

        // ---- Восстановление ----

        /// <summary>
        /// Восстанавливать без проверки нечего, и панель говорит именно это, а не молчит:
        /// иначе кнопка выглядит сломанной.
        /// </summary>
        [Fact]
        public async Task ВосстановлениеБезПроверкиОбъясняетПричину() {
            using var scope = new PanelScope();

            await scope.Panel.RepairAsync();

            Assert.Contains("сначала выполните проверку", scope.LastStatus, StringComparison.Ordinal);
        }

        /// <summary>
        /// Вопрос перед восстановлением обязан назвать числа: человек соглашается на
        /// перекачивание файлов и удаление лишних, а согласие вслепую тут стоит дорого —
        /// удалённое из папки игры не вернуть.
        /// </summary>
        [Fact]
        public async Task ВопросПередВосстановлениемНазываетЧислаФайлов() {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 3, extra: 2);
            scope.Answer = false;

            await scope.Panel.RepairAsync();

            var asked = Assert.Single(scope.Asked);
            Assert.Contains("перекачано файлов: 3", asked, StringComparison.Ordinal);
            Assert.Contains("удалено лишних: 2", asked, StringComparison.Ordinal);
        }

        /// <summary>
        /// Отказ от восстановления не должен трогать ничего: план остаётся, панель
        /// не занимается, файлы не качаются. «Отмена» обязана значить «ничего не делай».
        /// </summary>
        [Fact]
        public async Task ОтказОтВосстановленияНичегоНеДелает() {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 1, extra: 0);
            scope.BusyStates.Clear();
            scope.Answer = false;

            await scope.Panel.RepairAsync();

            Assert.Empty(scope.BusyStates);
            Assert.Equal(0, scope.Sync.ExecuteCalls);
            Assert.NotNull(scope.Panel.LastReport);
        }

        /// <summary>
        /// Согласие запускает восстановление, а после успеха панель предлагает проверить
        /// ещё раз: восстановление могло закрыть не всё, и «готово» без перепроверки
        /// звучит убедительнее, чем есть на самом деле.
        /// </summary>
        [Fact]
        public async Task УспешноеВосстановлениеПредлагаетПроверитьЕщёРаз() {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 1, extra: 0);
            scope.BusyStates.Clear();
            scope.Answer = true;

            await scope.Panel.RepairAsync();

            Assert.Equal(1, scope.Sync.ExecuteCalls);
            Assert.Contains("проверить целостность ещё раз", scope.LastStatus, StringComparison.Ordinal);
            Assert.Null(scope.Panel.LastReport);
            Assert.Equal(new[] { true, false }, scope.BusyStates);
        }

        /// <summary>
        /// Прерванное восстановление оставляет игру в незавершённом состоянии, и человека
        /// обязаны об этом предупредить: сухое «отменено» читается как «ничего не изменилось».
        /// </summary>
        [Fact]
        public async Task ОтменённоеВосстановлениеПредупреждаетОНезавершённомСостоянии() {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 1, extra: 0);
            scope.Sync.ExecuteError = new OperationCanceledException();
            scope.Answer = true;

            await scope.Panel.RepairAsync();

            Assert.Contains("незавершённом состоянии", scope.LastStatus, StringComparison.Ordinal);
            Assert.False(scope.Panel.Busy);
        }

        /// <summary>Сбой восстановления называется вслух, панель освобождается.</summary>
        [Fact]
        public async Task СбойВосстановленияПоказываетсяИОсвобождаетПанель() {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 1, extra: 0);
            scope.Sync.ExecuteError = new IOException("диск отвалился");
            scope.Answer = true;

            await scope.Panel.RepairAsync();

            Assert.Contains("диск отвалился", scope.LastStatus, StringComparison.Ordinal);
            Assert.False(scope.Panel.Busy);
        }

        /// <summary>
        /// Уход со страницы восстановление НЕ обрывает: обрыв на фазе активации оставит
        /// маркер .updating и наполовину обновлённую игру, поэтому его доводят до конца
        /// в фоне.
        /// </summary>
        [Fact]
        public async Task УходСоСтраницыНеОбрываетВосстановление() {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 1, extra: 0);
            using var gate = new ManualResetEventSlim(false);
            scope.Sync.ExecuteGate = gate;
            scope.Answer = true;

            var running = scope.Panel.RepairAsync();
            await WaitUntil(() => scope.Panel.Busy, "панель должна занять себя восстановлением");

            scope.Panel.LeavePage();
            gate.Set();
            await running;

            Assert.False(scope.Sync.ExecuteCancelled, "восстановление обрывать нельзя");
            Assert.Contains("проверить целостность ещё раз", scope.LastStatus, StringComparison.Ordinal);
        }

        // ---- Прогресс ----

        /// <summary>
        /// Процент прогресса обязан остаться в пределах шкалы: значение больше 100
        /// полоса рисует как пустую, и человек решит, что всё встало.
        /// </summary>
        [Theory]
        [InlineData(0, 10, 0.0)]
        [InlineData(5, 10, 50.0)]
        [InlineData(10, 10, 100.0)]
        [InlineData(20, 10, 100.0)]
        public async Task ПроцентПрогрессаНеВыходитЗаШкалу(int done, int total, double expected) {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 1, extra: 0);
            scope.Sync.ExecuteProgress = new SyncProgress { FilesDownloaded = done, TotalFiles = total, Stage = "Downloading" };
            scope.Answer = true;

            await scope.Panel.RepairAsync();
            await WaitUntil(() => scope.ProgressTexts.Count > 0, "прогресс должен дойти до панели");

            Assert.Equal(expected, scope.LastPercent);
            Assert.Contains($"Скачано: {done} из {total}", scope.ProgressTexts[0], StringComparison.Ordinal);
        }

        /// <summary>
        /// Общее число файлов ещё не известно — делить на ноль нельзя, полоса стоит на нуле.
        /// </summary>
        [Fact]
        public async Task НеизвестноеЧислоФайловНеДаётДеленияНаНоль() {
            using var scope = new PanelScope();
            await scope.SeedRepairPlan(missing: 1, extra: 0);
            scope.Sync.ExecuteProgress = new SyncProgress { FilesDownloaded = 0, TotalFiles = 0, Stage = "Checking" };
            scope.Answer = true;

            await scope.Panel.RepairAsync();
            await WaitUntil(() => scope.ProgressTexts.Count > 0, "прогресс должен дойти до панели");

            Assert.Equal(0.0, scope.LastPercent);
            Assert.Contains("Подготовка: 0 из 0", scope.ProgressTexts[0], StringComparison.Ordinal);
        }

        /// <summary>
        /// Этапы называются по-русски: строка состояния — единственное, что объясняет
        /// человеку, чем занят лаунчер несколько минут подряд.
        /// </summary>
        [Theory]
        [InlineData("Checking", "Подготовка")]
        [InlineData("Downloading", "Скачано")]
        [InlineData("Verifying", "Проверка")]
        [InlineData("Activating", "Установка")]
        [InlineData("Completed", "Готово")]
        [InlineData("", "Обработано")]
        [InlineData("НечтоНовое", "Обработано")]
        public void ЭтапыВосстановленияНазываютсяПоРусски(string stage, string expected)
            => Assert.Equal(expected, IntegrityPanel.StageToRu(stage));

        // ---- Подстановка игры в списке ----

        /// <summary>Пустой список — подставлять нечего, а не падение.</summary>
        [Fact]
        public void ПустойСписокИгрНичегоНеПодставляет()
            => Assert.Null(IntegrityPanel.Preselect(Array.Empty<GameInfo>(), @"D:\Games", "g"));

        /// <summary>
        /// Последняя запускавшаяся игра — самый вероятный кандидат на проверку: именно
        /// она только что и не заработала.
        /// </summary>
        [Fact]
        public void ПодставляетсяПоследняяЗапускавшаясяИгра() {
            var games = new List<GameInfo> {
                new GameInfo { GameId = "first" },
                new GameInfo { GameId = "lethal" },
            };

            Assert.Equal("lethal", IntegrityPanel.Preselect(games, @"D:\Games", "LETHAL")!.GameId);
        }

        /// <summary>
        /// Последней игры в списке нет (её сняли с раздачи) — берётся первая установленная.
        /// Предлагать проверить неустановленную игру бессмысленно.
        /// </summary>
        [Fact]
        public void БезПоследнейИгрыБерётсяПерваяУстановленная() {
            using var dir = new TempDir();
            dir.WriteFile("installed/game.exe", "данные");
            var games = new List<GameInfo> {
                new GameInfo { GameId = "missing" },
                new GameInfo { GameId = "installed" },
            };

            Assert.Equal("installed", IntegrityPanel.Preselect(games, dir.Root, "снятая-с-раздачи")!.GameId);
        }

        /// <summary>Ничего не установлено — берётся первая в списке, чтобы поле не было пустым.</summary>
        [Fact]
        public void БезУстановленныхИгрБерётсяПерваяВСписке() {
            using var dir = new TempDir();
            var games = new List<GameInfo> {
                new GameInfo { GameId = "a" },
                new GameInfo { GameId = "b" },
            };

            Assert.Equal("a", IntegrityPanel.Preselect(games, dir.Root, string.Empty)!.GameId);
        }

        /// <summary>
        /// Ждёт условия, крутя пул задач. Ожиданием по таймеру пользоваться нельзя:
        /// на загруженной машине оно мигает.
        /// </summary>
        private static async Task WaitUntil(Func<bool> condition, string what) {
            var sw = Stopwatch.StartNew();
            while (!condition()) {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"не дождались: {what}");
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Панель вместе с подставными диалогами, временной папкой игр и записью всего,
        /// что она сказала в интерфейс.
        /// </summary>
        private sealed class PanelScope : IDisposable {
            private const string GameId = "g";

            private readonly ConfigDirsScope config = new ConfigDirsScope();
            private readonly TempDir games = new TempDir();
            private Manifest manifest = new Manifest();

            internal PanelScope() {
                // Под подставленным конфигом разворачиваются умолчания, а в них отправка
                // отчётов и статистики включена: тест ушёл бы в сеть.
                ConfigService.Current.AutoErrorReports = false;
                ConfigService.Current.SendUsageMetrics = false;
                ConfigService.Current.GamesPath = this.games.Root;

                this.Sync = new StubSync(() => this.manifest);
                this.Panel = new IntegrityPanel(this.Sync) {
                    ShowStatus = text => this.LastStatus = text,
                    ShowBusy = busy => this.BusyStates.Add(busy),
                    ShowRepairButton = visible => this.RepairVisible = visible,
                    ShowProgress = (percent, text) => {
                        this.LastPercent = percent;
                        this.ProgressTexts.Add(text);
                    },
                };

                SettingsDialogs.Confirm = (message, caption) => {
                    this.Asked.Add(message);
                    return this.Answer;
                };
            }

            internal IntegrityPanel Panel { get; }

            internal StubSync Sync { get; }

            /// <summary>Папка игр, подставленная в конфиг.</summary>
            internal string GamesRoot => this.games.Root;

            /// <summary>Ответ пользователя на вопрос перед восстановлением.</summary>
            internal bool Answer { get; set; }

            /// <summary>Последнее, что панель написала в строку состояния.</summary>
            internal string LastStatus { get; private set; } = string.Empty;

            /// <summary>Последний показанный процент.</summary>
            internal double LastPercent { get; private set; } = -1;

            /// <summary>Видна ли кнопка восстановления.</summary>
            internal bool RepairVisible { get; private set; }

            /// <summary>Все переходы «занята / свободна» по порядку.</summary>
            internal List<bool> BusyStates { get; } = new();

            /// <summary>Все строки прогресса по порядку.</summary>
            internal List<string> ProgressTexts { get; } = new();

            /// <summary>Заданные пользователю вопросы.</summary>
            internal List<string> Asked { get; } = new();

            /// <summary>
            /// Раскладывает на диске игру и собирает под неё манифест: <paramref name="intact"/>
            /// целых файлов, <paramref name="missing"/> объявленных в манифесте, но
            /// отсутствующих, и <paramref name="extra"/> лежащих на диске, но не объявленных.
            /// </summary>
            internal void InstallGame(int intact, int missing, int extra) {
                var files = new List<ManifestFile>();
                for (var i = 0; i < intact; i++) {
                    var rel = $"ok{i}.dat";
                    var path = this.games.WriteFile($"{GameId}/{rel}", $"содержимое {i}");
                    files.Add(PlanTestData.File(rel, new FileInfo(path).Length, TestHash.Sha256OfFile(path)));
                }

                for (var i = 0; i < missing; i++) {
                    files.Add(PlanTestData.File($"gone{i}.dat", 100, "00"));
                }

                for (var i = 0; i < extra; i++) {
                    this.games.WriteFile($"{GameId}/лишний{i}.txt", "мод или сейв");
                }

                this.manifest = PlanTestData.Manifest(files.ToArray());
            }

            /// <summary>Прогоняет проверку выбранной игры.</summary>
            internal Task CheckAsync()
                => this.Panel.CheckAsync(new GameInfo { GameId = GameId, LatestVersion = "1.0.0" });

            /// <summary>
            /// Доводит панель до состояния «есть что восстанавливать» настоящей проверкой,
            /// а не подстановкой отчёта: восстановление работает по плану, который построила
            /// проверка, и подсунутый план проверял бы не то.
            /// </summary>
            internal async Task SeedRepairPlan(int missing, int extra) {
                this.InstallGame(intact: 1, missing: missing, extra: extra);
                await this.CheckAsync();
                Assert.True(this.Panel.LastReport?.NeedsRepair, "проверка должна найти что чинить");
                this.Asked.Clear();
                this.ProgressTexts.Clear();
            }

            public void Dispose() {
                SettingsDialogs.ResetDialogsForTests();
                try {
                    FileHashCache.Remove(this.manifest.GameId);
                }
                catch (Exception) {
                    // Кеш хешей — служебный файл; уборка best effort.
                }

                this.games.Dispose();
                this.config.Dispose();
            }
        }

        /// <summary>
        /// Отдаёт заранее подготовленный манифест (или заданную ошибку) вместо похода
        /// в сеть и умеет подождать на воротах, чтобы тест успел нажать «отмена».
        /// Планирование при этом настоящее — именно его результат и чинится.
        /// </summary>
        private sealed class StubSync : ISyncService {
            private readonly Func<Manifest> manifest;

            internal StubSync(Func<Manifest> manifest) => this.manifest = manifest;

            /// <summary>Чем ответить вместо манифеста.</summary>
            internal Exception? ManifestError { get; set; }

            /// <summary>Ворота, на которых манифест ждёт разрешения теста.</summary>
            internal ManualResetEventSlim? ManifestGate { get; set; }

            /// <summary>Чем закончить восстановление.</summary>
            internal Exception? ExecuteError { get; set; }

            /// <summary>Ворота, на которых ждёт восстановление.</summary>
            internal ManualResetEventSlim? ExecuteGate { get; set; }

            /// <summary>Что сообщить о прогрессе восстановления.</summary>
            internal SyncProgress? ExecuteProgress { get; set; }

            /// <summary>Сколько раз запускали восстановление.</summary>
            internal int ExecuteCalls { get; private set; }

            /// <summary>Был ли токен отменён к концу восстановления.</summary>
            internal bool ExecuteCancelled { get; private set; }

            public async Task<Manifest> GetManifestAsync(string manifestUrl, CancellationToken ct) {
                if (this.ManifestGate != null) {
                    await Task.Run(() => this.ManifestGate.Wait(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
                }

                if (this.ManifestError != null) {
                    throw this.ManifestError is OperationCanceledException
                        ? new OperationCanceledException()
                        : this.ManifestError;
                }

                return this.manifest();
            }

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken ct)
                => this.PlanAsync(manifest, localRoot, contentBaseUrl, PlanOptions.Default, ct);

            public Task<DiffPlan> PlanAsync(Manifest manifest, string localRoot, string contentBaseUrl, PlanOptions options, CancellationToken ct)
                => new SimpleSyncService(new System.Net.Http.HttpClient()).PlanAsync(manifest, localRoot, contentBaseUrl, options, ct);

            public async Task ExecuteAsync(DiffPlan plan, IProgress<SyncProgress> progress, CancellationToken ct) {
                this.ExecuteCalls++;
                if (this.ExecuteProgress != null) {
                    progress?.Report(this.ExecuteProgress);
                }

                if (this.ExecuteGate != null) {
                    await Task.Run(() => this.ExecuteGate.Wait(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
                }

                this.ExecuteCancelled = ct.IsCancellationRequested;
                if (this.ExecuteError != null) {
                    throw this.ExecuteError;
                }
            }
        }
    }
}
