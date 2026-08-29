// <copyright file="RunningGamesTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;

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
            RunningGames.MarkRunning("repo", 4242);
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
            RunningGames.BeginStarting("repo");
            Assert.Equal(GameRunState.Starting, RunningGames.StateOf("repo"));

            RunningGames.EndStarting("repo");
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Найденный процесс перебивает ожидание: между «нашёлся» и «ждать перестали»
        /// проходит мгновение, и в это мгновение игра не должна выглядеть незапущенной.
        /// </summary>
        [Fact]
        public void НайденныйПроцессВажнееОжидания() {
            RunningGames.BeginStarting("repo");
            RunningGames.MarkRunning("repo", 4242);
            RunningGames.EndStarting("repo");

            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Два ожидания на одну игру снимаются по одному: первое закончившееся не
        /// отменяет второе, иначе кнопки ожили бы посреди ещё идущего запуска.
        /// </summary>
        [Fact]
        public void ДваОжиданияСнимаютсяПоОдному() {
            RunningGames.BeginStarting("repo");
            RunningGames.BeginStarting("repo");

            RunningGames.EndStarting("repo");
            Assert.Equal(GameRunState.Starting, RunningGames.StateOf("repo"));

            RunningGames.EndStarting("repo");
            Assert.Equal(GameRunState.None, RunningGames.StateOf("repo"));
        }

        /// <summary>
        /// Выход одной копии не гасит отметку о второй: одна игра может идти дважды —
        /// например, вторую подняли из самого Steam, мимо лаунчера.
        /// </summary>
        [Fact]
        public void ВыходОднойКопииНеГаситВторую() {
            RunningGames.MarkRunning("repo", 1);
            RunningGames.MarkRunning("repo", 2);

            RunningGames.ClearRunning(1);

            Assert.Equal(GameRunState.Running, RunningGames.StateOf("repo"));
        }

        /// <summary>Состояние — про свою игру: соседняя от него не меняется.</summary>
        [Fact]
        public void СостояниеНеПротекаетНаСоседнююИгру() {
            RunningGames.MarkRunning("repo", 4242);

            Assert.Equal(GameRunState.None, RunningGames.StateOf("peak"));
        }

        /// <summary>О каждой перемене подписчик узнаёт событием, а не опросом.</summary>
        [Fact]
        public void ПеременаПриходитСобытием() {
            var calls = 0;
            void Handler() => calls++;

            RunningGames.Changed += Handler;
            try {
                RunningGames.MarkRunning("repo", 4242);
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
            RunningGames.MarkRunning("repo", 4242);

            var calls = 0;
            void Handler() => calls++;

            RunningGames.Changed += Handler;
            try {
                RunningGames.MarkRunning("repo", 4242);
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
                Pack(), playMode: true, steamAllowed: false, All(), remembered: null, run: GameRunState.Running);

            Assert.Equal(2, view.Buttons.Count);
            Assert.All(view.Buttons, b => Assert.False(b.Enabled));
            Assert.All(view.Buttons, b => Assert.Equal("игра запущена", b.Subtitle));
            Assert.Equal(GameRunState.Running, view.Run);
        }

        /// <summary>Пока игра только запускается — то же самое, другими словами.</summary>
        [Fact]
        public void ПокаИграЗапускаетсяКнопкиТожеЖдут() {
            var view = LaunchButtons.Compute(
                Pack(), playMode: true, steamAllowed: false, All(), remembered: null, run: GameRunState.Starting);

            Assert.All(view.Buttons, b => Assert.False(b.Enabled));
            Assert.All(view.Buttons, b => Assert.Equal("запускается…", b.Subtitle));
        }

        /// <summary>Незапущенная игра ничего не теряет: подписи прежние, кнопки живые.</summary>
        [Fact]
        public void БезЗапускаКнопкиОстаютсяПрежними() {
            var view = LaunchButtons.Compute(Pack(), playMode: true, steamAllowed: false, All(), remembered: null);

            Assert.Equal(GameRunState.None, view.Run);
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
