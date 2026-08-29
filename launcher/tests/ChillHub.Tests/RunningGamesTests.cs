// <copyright file="RunningGamesTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using ChillHub.Core;
    using ChillHub.Core.Game;
    using ChillHub.Core.Home;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Что лаунчер знает и показывает о запущенной игре.
    /// <para>
    /// ЗАПУСК, ПОСЛЕ КОТОРОГО НИЧЕГО НЕ ПРОИСХОДИТ, ЧИТАЕТСЯ КАК СЛОМАННАЯ КНОПКА.
    /// Игра поднимается секунды, а через Steam — до минуты, и всё это время витрина
    /// выглядела ровно как до нажатия: те же кнопки, тот же бейдж, та же строка
    /// состояния. Второе и третье нажатие поднимали вторую и третью копию игры.
    /// </para>
    /// </summary>
    public class RunningGamesTests : IDisposable {
        public RunningGamesTests() => RunningGames.ResetForTests();

        public void Dispose() => RunningGames.ResetForTests();

        /// <summary>Про игру, которую никто не запускал, сказать нечего.</summary>
        [Fact]
        public void НезапущеннаяИграНичегоНеЗначит() {
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
            Assert.Equal(GameRunState.None, RunningGames.StateOf(null));
        }

        /// <summary>Найденный процесс делает игру запущенной, его выход — снимает отметку.</summary>
        [Fact]
        public void ПроцессПоднимаетИСнимаетОтметку() {
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);
            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));

            RunningGames.ClearRunning(4242);
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Пока процесса ещё не видно, игра «запускается»: этим состоянием и заполнена
        /// та самая минута между командой Steam и окном игры.
        /// </summary>
        [Fact]
        public void ОжиданиеПроцессаЭтоЗапускается() {
            RunningGames.BeginStarting("repo", LaunchTarget.LocalModded);
            Assert.Equal(GameRunState.Starting, RunningGames.StateOf("repo"));

            RunningGames.EndStarting("repo", LaunchTarget.LocalModded);
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Найденный процесс перебивает ожидание: между «нашёлся» и «ждать перестали»
        /// проходит мгновение, и в это мгновение игра не должна выглядеть незапущенной.
        /// </summary>
        [Fact]
        public void НайденныйПроцессВажнееОжидания() {
            RunningGames.BeginStarting("repo", LaunchTarget.LocalModded);
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);
            RunningGames.EndStarting("repo", LaunchTarget.LocalModded);

            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Два ожидания на одну игру снимаются по одному: первое закончившееся не
        /// отменяет второе, иначе кнопки ожили бы посреди ещё идущего запуска.
        /// </summary>
        [Fact]
        public void ДваОжиданияСнимаютсяПоОдному() {
            RunningGames.BeginStarting("repo", LaunchTarget.LocalModded);
            RunningGames.BeginStarting("repo", LaunchTarget.LocalModded);

            RunningGames.EndStarting("repo", LaunchTarget.LocalModded);
            Assert.Equal(GameRunState.Starting, RunningGames.StateOf("repo"));

            RunningGames.EndStarting("repo", LaunchTarget.LocalModded);
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Выход одной копии не гасит отметку о второй: одна игра может идти дважды —
        /// например, вторую подняли из самого Steam, мимо лаунчера.
        /// </summary>
        [Fact]
        public void ВыходОднойКопииНеГаситВторую() {
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 1);
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 2);

            RunningGames.ClearRunning(1);

            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА. Запустили копию из Steam — «запускается…» подписана
        /// ТОЛЬКО она. Пока состояние считалось на игру целиком, обе кнопки разом
        /// уходили в «запускается…»: и Steam, и Пиратка.
        /// </summary>
        [Fact]
        public void ЗапускОднойВерсииНеПодписываетСоседнюю() {
            RunningGames.BeginStarting("repo", LaunchTarget.SteamModded);

            Assert.Equal(GameRunState.Starting, RunningGames.StateOf("repo", LaunchTarget.SteamModded));
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo", LaunchTarget.LocalModded));
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo", LaunchTarget.SteamVanilla));

            // А игра в целом — запускается: это для строки списка и бейджа витрины.
            Assert.Equal(GameRunState.Starting, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Две версии одной игры идут разом — обе видны каждая своей. Копия из Steam и
        /// сборка с сервера лежат в разных папках, и запуск одной другой не мешает.
        /// </summary>
        [Fact]
        public void ДвеВерсииОднойИгрыВиднаКаждаяСвоей() {
            RunningGames.MarkRunning("repo", LaunchTarget.SteamModded, 1);
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 2);

            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo", LaunchTarget.SteamModded));
            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo", LaunchTarget.LocalModded));

            // Закрыли пиратку — Steam остаётся запущенным.
            RunningGames.ClearRunning(2);
            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo", LaunchTarget.SteamModded));
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo", LaunchTarget.LocalModded));
        }

        /// <summary>
        /// Витрина: выключается кнопка запущенной версии, соседняя остаётся живой.
        /// Ровно то, что на скриншоте выглядело как «запускаются обе».
        /// </summary>
        [Fact]
        public void ВыключаетсяКнопкаТолькоЗапущеннойВерсии() {
            var view = LaunchButtons.Compute(
                Pack(), playMode: true, steamAllowed: false, All(), remembered: null,
                runOf: t => t == LaunchTarget.SteamModded ? GameRunState.Starting : GameRunState.None);

            var steam = view.Buttons.Single(b => b.Target == LaunchTarget.SteamModded);
            var local = view.Buttons.Single(b => b.Target == LaunchTarget.LocalModded);

            Assert.False(steam.Enabled);
            Assert.Equal("запускается…", steam.Subtitle);

            Assert.True(local.Enabled);
            Assert.Equal("с модами", local.Subtitle);
        }

        /// <summary>Состояние — про свою игру: соседняя от него не меняется.</summary>
        [Fact]
        public void СостояниеНеПротекаетНаСоседнююИгру() {
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);

            Assert.Equal(GameRunState.None, RunningGames.StateOf("peak"));
        }

        /// <summary>
        /// Игра без имени ничего не отмечает: запуск бывает и без выбранной игры
        /// («Играть» из трея на пустом списке), и заводить под него безымянную
        /// запись значило бы запереть кнопки неизвестно чьей игры.
        /// </summary>
        [Fact]
        public void БезымяннаяИграНичегоНеОтмечает() {
            RunningGames.BeginStarting(null, LaunchTarget.LocalModded);
            RunningGames.BeginStarting("   ", LaunchTarget.LocalModded);
            RunningGames.MarkRunning(null, LaunchTarget.LocalModded, 4242);
            RunningGames.EndStarting(null, LaunchTarget.LocalModded);

            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
            Assert.Equal(GameRunState.None, RunningGames.StateOf("   "));
        }

        /// <summary>
        /// Снять ожидание, которого не заводили, — не ошибка: поиск процесса мог
        /// сорваться и после того, как его уже сняли по таймауту.
        /// </summary>
        [Fact]
        public void СнятиеНесуществующегоОжиданияНичегоНеЛомает() {
            RunningGames.EndStarting("repo", LaunchTarget.LocalModded);

            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Упавший подписчик не уносит с собой сам учёт: событие приходит из фоновой
        /// задачи, ждущей выхода процесса, и её падение оставило бы игру навсегда
        /// «запущенной» — то есть витрину без кнопок запуска.
        /// </summary>
        [Fact]
        public void УпавшийПодписчикНеЛомаетУчёт() {
            void Broken() => throw new InvalidOperationException("страница уже закрыта");

            RunningGames.Changed += Broken;
            try {
                RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);
                Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));

                RunningGames.ClearRunning(4242);
                Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
            }
            finally {
                RunningGames.Changed -= Broken;
            }
        }

        /// <summary>О каждой перемене подписчик узнаёт событием, а не опросом.</summary>
        [Fact]
        public void ПеременаПриходитСобытием() {
            var calls = 0;
            void Handler() => calls++;

            RunningGames.Changed += Handler;
            try {
                RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);
                RunningGames.ClearRunning(4242);
            }
            finally {
                RunningGames.Changed -= Handler;
            }

            Assert.Equal(2, calls);
        }

        /// <summary>Повторная отметка того же процесса тишину не нарушает.</summary>
        [Fact]
        public void ПовторнаяОтметкаНеБудитПодписчиков() {
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);

            var calls = 0;
            void Handler() => calls++;

            RunningGames.Changed += Handler;
            try {
                RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);
                RunningGames.ClearRunning(777);
            }
            finally {
                RunningGames.Changed -= Handler;
            }

            Assert.Equal(0, calls);
        }

        /// <summary>
        /// Кнопки запуска у идущей игры остаются на месте, но не нажимаются и говорят,
        /// что происходит. Пропади они — витрина выглядела бы сломанной, останься
        /// живыми — второе нажатие подняло бы вторую копию игры.
        /// </summary>
        [Fact]
        public void УЗапущеннойИгрыКнопкиВыключеныИОбъясняют() {
            var view = LaunchButtons.Compute(
                Pack(), playMode: true, steamAllowed: false, All(), remembered: null, runOf: _ => GameRunState.Running);

            Assert.Equal(2, view.Buttons.Count);
            Assert.All(view.Buttons, b => Assert.False(b.Enabled));
            Assert.All(view.Buttons, b => Assert.Equal("игра запущена", b.Subtitle));
        }

        /// <summary>Пока игра только запускается — то же самое, другими словами.</summary>
        [Fact]
        public void ПокаИграЗапускаетсяКнопкиТожеЖдут() {
            var view = LaunchButtons.Compute(
                Pack(), playMode: true, steamAllowed: false, All(), remembered: null, runOf: _ => GameRunState.Starting);

            Assert.All(view.Buttons, b => Assert.False(b.Enabled));
            Assert.All(view.Buttons, b => Assert.Equal("запускается…", b.Subtitle));
        }

        /// <summary>Незапущенная игра ничего не теряет: подписи прежние, кнопки живые.</summary>
        [Fact]
        public void БезЗапускаКнопкиОстаютсяПрежними() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, All(), remembered: null);

            Assert.True(view.MenuVisible);
            Assert.All(view.Buttons, b => Assert.True(b.Enabled));
            Assert.All(view.Buttons, b => Assert.Equal("с модами", b.Subtitle));
        }

        /// <summary>«Играть» под открытой игрой называет происходящее, а не предлагает второй запуск.</summary>
        [Fact]
        public void КнопкаДействияПодОткрытойИгройНеПредлагаетЗапуск() {
            var running = ActionButtonState.Appearance(ActionMode.Play, GameRunState.Running);
            Assert.Equal("Игра запущена", running.Content);
            Assert.False(running.IsEnabled);

            var starting = ActionButtonState.Appearance(ActionMode.Play, GameRunState.Starting);
            Assert.Equal("Запускается…", starting.Content);
            Assert.False(starting.IsEnabled);
        }

        /// <summary>
        /// Установка и обновление к открытой игре отношения не имеют: их кнопка не
        /// меняется. Запрет на «докатить поверх идущей игры» живёт не здесь.
        /// </summary>
        [Fact]
        public void ОстальныеРежимыКнопкиЗапущеннаяИграНеТрогает() {
            Assert.Equal("Обновить", ActionButtonState.Appearance(ActionMode.Update, GameRunState.Running).Content);
            Assert.Equal("Установить", ActionButtonState.Appearance(ActionMode.Install, GameRunState.Running).Content);
            Assert.Equal("Отмена", ActionButtonState.Appearance(ActionMode.Cancel, GameRunState.Running).Content);
        }

        /// <summary>
        /// Одно состояние — одно имя во всех четырёх местах экрана. Разойдись слова,
        /// и «Играет» в списке против «Запущена» на витрине читались бы как разные
        /// состояния одной игры.
        /// </summary>
        [Fact]
        public void УСостоянияОдноИмяВоВсехМестахЭкрана() {
            Assert.Equal("игра запущена", RunningGameLook.ButtonNote(GameRunState.Running));
            Assert.Equal("Игра запущена", RunningGameLook.Headline(GameRunState.Running));
            Assert.Equal("Играет", RunningGameLook.RowLabel(GameRunState.Running));
            Assert.Equal("Игра уже запущена.", RunningGameLook.Refusal(GameRunState.Running));

            Assert.Equal("запускается…", RunningGameLook.ButtonNote(GameRunState.Starting));
            Assert.Equal("Запускается…", RunningGameLook.Headline(GameRunState.Starting));
            Assert.Equal("Запускается…", RunningGameLook.RowLabel(GameRunState.Starting));
            Assert.Equal(
                "Игра уже запускается. Подождите — это может занять до минуты.",
                RunningGameLook.Refusal(GameRunState.Starting));
        }

        /// <summary>
        /// О незапущенной игре сказать нечего — пустая строка, а не слово. По ней
        /// витрина и решает, показывать ли бейдж и отказывать ли в запуске.
        /// </summary>
        [Fact]
        public void ПроНезапущеннуюИгруСловНет() {
            Assert.Empty(RunningGameLook.ButtonNote(GameRunState.None));
            Assert.Empty(RunningGameLook.Headline(GameRunState.None));
            Assert.Empty(RunningGameLook.RowLabel(GameRunState.None));
            Assert.Empty(RunningGameLook.Refusal(GameRunState.None));
        }

        /// <summary>
        /// Подписи расставляются по строкам списка: запущенной — «Играет», соседним —
        /// ничего. Список пересобирается со своими строками, и подпись обязана
        /// приезжать на новые объекты.
        /// </summary>
        [Fact]
        public void ПодписиРасставляютсяПоСтрокамСписка() {
            var open = new GameInfo { GameId = "repo" };
            var idle = new GameInfo { GameId = "peak" };
            RunningGames.MarkRunning("repo", LaunchTarget.LocalModded, 4242);

            RunningGameLook.ApplyLabels(new[] { open, idle });

            Assert.Equal("Играет", open.RunLabel);
            Assert.Empty(idle.RunLabel);
        }

        /// <summary>Закрытая игра теряет подпись при следующей расстановке.</summary>
        [Fact]
        public void ЗакрытаяИграТеряетПодпись() {
            var game = new GameInfo { GameId = "repo", RunLabel = "Играет" };

            RunningGameLook.ApplyLabels(new[] { game });

            Assert.Empty(game.RunLabel);
        }

        /// <summary>Списка ещё нет — расставлять нечего, и падать не из-за чего.</summary>
        [Fact]
        public void БезСпискаРасстановкаМолчит() {
            RunningGameLook.ApplyLabels(null);
        }

        /// <summary>
        /// Строка списка предпочитает «Играет» статусу на диске, но уступает очереди:
        /// у качающейся игры важнее проценты, а «Установлена» под открытой игрой —
        /// вчерашняя новость.
        /// </summary>
        [Fact]
        public void СтрокаСпискаСтавитИграетПослеОчередиНоПередСтатусом() {
            var game = new GameInfo { IsInstalled = true, NeedsUpdate = false };
            var text = new Core.UI.GameRowStatusTextConverter();
            var brush = new Core.UI.GameRowStatusBrushConverter();

            Assert.Equal(
                "Играет",
                text.Convert(new object[] { game, string.Empty, "Играет" }, typeof(string), null!, Culture));
            Assert.Equal(
                "Скачивание · 38%",
                text.Convert(new object[] { game, "Скачивание · 38%", "Играет" }, typeof(string), null!, Culture));
            Assert.Equal(
                "Установлена",
                text.Convert(new object[] { game, string.Empty, string.Empty }, typeof(string), null!, Culture));

            var playing = (System.Windows.Media.SolidColorBrush)brush.Convert(
                new object[] { game, string.Empty, "Играет" }, typeof(System.Windows.Media.Brush), null!, Culture);
            Assert.Equal(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#57C98A"),
                playing.Color);
        }

        private static System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.InvariantCulture;

        private static ModsInfo Pack() => new ModsInfo { SteamAppId = "3527290", Version = "1.0" };

        private static List<LaunchOption> All() => new List<LaunchOption> {
            Option(LaunchTarget.SteamModded),
            Option(LaunchTarget.SteamVanilla),
            Option(LaunchTarget.LocalModded),
            Option(LaunchTarget.LocalVanilla),
        };

        private static LaunchOption Option(LaunchTarget target) =>
            new LaunchOption(target, ModsLaunch.TitleOf(target, Pack()), "C:/games", true, LaunchAction.Play, string.Empty);
    }
}
