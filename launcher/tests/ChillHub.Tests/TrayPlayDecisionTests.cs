// <copyright file="TrayPlayDecisionTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Пункт «Играть» в меню значка в трее.
    /// <para>
    /// Игрок ставит игру, сворачивает лаунчер и жмёт в трее «Играть» — а закачка
    /// пропадает без единого сообщения: меню безусловно нажимало кнопку действия
    /// витрины, а та в это время называется «Отмена».
    /// </para>
    /// </summary>
    public class TrayPlayDecisionTests {
        /// <summary>Готовую игру трей запускает молча — ровно за этим пункт и нужен.</summary>
        [Fact]
        public void ГотовуюИгруЗапускаемНеПоднимаяОкно() {
            Assert.Equal(TrayPlay.Launch, TrayPlayDecision.For(canPlay: true, actionCancels: false));
        }

        /// <summary>
        /// Идёт установка (кнопка витрины — «Отмена») или позиция ждёт очереди («Убрать
        /// из очереди») — трей только показывает окно. Нажать отмену за игрока он права
        /// не имеет: потерянную установку не вернуть.
        /// </summary>
        [Fact]
        public void КачающуюсяИгруНеТрогаемТолькоПоказываемОкно() {
            Assert.Equal(TrayPlay.ShowWindow, TrayPlayDecision.For(canPlay: false, actionCancels: true));
        }

        /// <summary>
        /// Игру, которую надо поставить или обновить, трей по-прежнему запускает в работу
        /// и показывает окно: ничего не теряется, а происходящее видно.
        /// </summary>
        [Fact]
        public void НеустановленнойИгреПоказываемОкноИНачинаемРаботу() {
            Assert.Equal(TrayPlay.ShowWindowAndAct, TrayPlayDecision.For(canPlay: false, actionCancels: false));
        }
    }
}
