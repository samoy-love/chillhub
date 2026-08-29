// <copyright file="SelfUpdatePromptTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.SelfUpdate;

    using Xunit;

    /// <summary>
    /// Когда лаунчер показывает диалог своего обновления.
    /// <para>
    /// РЕГРЕССИЯ: при возврате фокуса на окно обновление не появлялось. Причин было
    /// две, и они складывались. Фоновая проверка ходит по таймеру независимо от того,
    /// видно ли окно; найдя обновление у свёрнутого окна, она молча уходила ни с чем —
    /// результат выбрасывался. А возврат фокуса за новой проверкой не шёл: ограничитель
    /// частоты пропускал её не чаще раза в десять минут, и обычно молчал. Теперь
    /// проверка идёт на каждый возврат фокуса, а найденное не теряется.
    /// </para>
    /// </summary>
    public class SelfUpdatePromptTests {
        /// <summary>Обычное состояние — окно на экране, работы нет: диалог показываем.</summary>
        [Fact]
        public void ОкнуНаЭкранеДиалогПоказываем() {
            Assert.True(SelfUpdatePrompt.CanShowNow(windowVisible: true, minimized: false, busy: false));
        }

        /// <summary>
        /// Свёрнутому и спрятанному в трей показывать некому, а посреди закачки или
        /// проверки файлов диалог читается как требование бросить начатое.
        /// </summary>
        [Theory]
        [InlineData(false, false, false)] // в трее
        [InlineData(true, true, false)]   // свёрнуто
        [InlineData(true, false, true)]   // идёт работа в очереди
        public void НеподходящийМоментДиалогаНеДаёт(bool visible, bool minimized, bool busy) {
            Assert.False(SelfUpdatePrompt.CanShowNow(visible, minimized, busy));
        }

        /// <summary>
        /// ГЛАВНОЕ ПРО АВТОМАТ. Обновление нашли, а показывать некому — оно ждёт, и
        /// первый же подходящий момент его отдаёт. Ровно это и выбрасывалось.
        /// </summary>
        [Fact]
        public void НайденноеПриСвёрнутомОкнеЖдётИДожидается() {
            var gate = new SelfUpdateGate();
            var found = Precheck("1.6.20");

            // Окно свёрнуто — показывать некому.
            Assert.Null(gate.Offer(found, windowVisible: true, minimized: true, busy: false));
            Assert.True(gate.Waiting);

            // Человек вернулся к окну — обновление отдаётся ему же, не потерявшись.
            var ready = gate.Offer(found, windowVisible: true, minimized: false, busy: false);
            Assert.NotNull(ready);
            Assert.Equal("1.6.20", ready!.Value.Decision.RemoteVersion);
            Assert.False(gate.Waiting);
        }

        /// <summary>
        /// Обновления больше нет — уже поставили или откатили на сервере. Отложенное
        /// забывается, иначе диалог всплыл бы с версией, которой уже не существует.
        /// </summary>
        [Fact]
        public void ИсчезнувшееОбновлениеЗабывается() {
            var gate = new SelfUpdateGate();
            gate.Offer(Precheck(), windowVisible: false, minimized: false, busy: false);
            Assert.True(gate.Waiting);

            gate.Forget();

            Assert.False(gate.Waiting);
        }

        /// <summary>
        /// Показанное не показывается второй раз: иначе следующий возврат фокуса открыл
        /// бы диалог поверх только что закрытого.
        /// </summary>
        [Fact]
        public void ПоказанноеВторойРазНеВсплывает() {
            var gate = new SelfUpdateGate();

            Assert.NotNull(gate.Offer(Precheck(), windowVisible: true, minimized: false, busy: false));

            Assert.False(gate.Waiting);
        }

        /// <summary>Проверка нашла обновление указанной версии.</summary>
        /// <param name="version">Версия на сервере.</param>
        /// <returns>Решение проверки.</returns>
        private static SelfUpdatePrecheck Precheck(string version = "1.6.20") => new SelfUpdatePrecheck {
            Decision = new SelfUpdateDecision {
                State = SelfUpdateState.UpdateAvailable,
                RemoteVersion = version,
            },
        };

    }
}
