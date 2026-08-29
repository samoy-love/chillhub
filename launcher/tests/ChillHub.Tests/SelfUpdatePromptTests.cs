// <copyright file="SelfUpdatePromptTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Когда лаунчер показывает диалог своего обновления.
    /// <para>
    /// РЕГРЕССИЯ: при возврате фокуса на окно обновление не появлялось. Причин было
    /// две, и они складывались. Фоновая проверка ходит по таймеру независимо от того,
    /// видно ли окно; найдя обновление у свёрнутого окна, она молча уходила ни с чем —
    /// результат выбрасывался. А возврат фокуса за новой проверкой не шёл: ограничитель
    /// частоты пропускает её не чаще раза в десять минут, и обычно молчал.
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
        /// ГЛАВНОЕ. Обновление уже найдено и ждёт — возврат фокуса обязан его показать,
        /// сколько бы ни оставалось до следующего разрешения ограничителя. Ровно здесь
        /// человек и оставался без окна обновления.
        /// </summary>
        [Fact]
        public void НайденноеОбновлениеПоказываетсяМимоОграничителя() {
            Assert.True(SelfUpdatePrompt.ShouldCheckOnActivate(updateWaiting: true, throttleAllows: false));
        }

        /// <summary>
        /// А без найденного обновления ограничитель работает как прежде: Alt+Tab между
        /// окнами даёт десятки активаций в минуту, и на каждой уходил бы запрос к серверу.
        /// </summary>
        [Fact]
        public void БезНайденногоОбновленияОграничительРаботает() {
            Assert.False(SelfUpdatePrompt.ShouldCheckOnActivate(updateWaiting: false, throttleAllows: false));
            Assert.True(SelfUpdatePrompt.ShouldCheckOnActivate(updateWaiting: false, throttleAllows: true));
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
        /// Пока обновление ждёт, возврат фокуса идёт за проверкой мимо ограничителя —
        /// а как только показали, ограничитель снова в силе.
        /// </summary>
        [Fact]
        public void ОжидающееОбновлениеОткрываетПроверкуМимоОграничителя() {
            var gate = new SelfUpdateGate();

            Assert.False(gate.ShouldCheckOnActivate(throttleAllows: false));

            gate.Offer(Precheck(), windowVisible: false, minimized: false, busy: false);
            Assert.True(gate.ShouldCheckOnActivate(throttleAllows: false));

            gate.Offer(Precheck(), windowVisible: true, minimized: false, busy: false);
            Assert.False(gate.ShouldCheckOnActivate(throttleAllows: false));
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
            Assert.False(gate.ShouldCheckOnActivate(throttleAllows: false));
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

        /// <summary>
        /// Сам ограничитель: первый возврат фокуса пропускает всегда, следующий — только
        /// когда срок вышел. Без этого правила проверка обновления при возврате к окну
        /// не запускалась бы вовсе.
        /// </summary>
        [Fact]
        public void ОграничительПропускаетПервыйВозвратИДальшеПоСроку() {
            var now = 0L;
            var throttle = new ActivationThrottle(TimeSpan.FromSeconds(10), () => now);

            Assert.True(throttle.Allow(), "первый возврат фокуса обязан пропускаться");
            Assert.False(throttle.Allow());

            // Шторм активаций (Alt+Tab между окнами) сервер не дёргает.
            now += (long)TimeSpan.FromSeconds(9).TotalMilliseconds;
            Assert.False(throttle.Allow());

            // А обычный возврат к лаунчеру — уже через десять секунд, а не через
            // десять минут: человек приходит за обновлением и должен получить проверку.
            now += (long)TimeSpan.FromSeconds(1).TotalMilliseconds;
            Assert.True(throttle.Allow());
        }
    }
}
