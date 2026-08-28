// <copyright file="ActionButtonStateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Home;
    using ChillHub.Core.Maintenance;

    using Xunit;

    /// <summary>
    /// Что написано на единой кнопке действия.
    /// <para>
    /// Надпись — единственное, по чему пользователь понимает, что произойдёт по нажатию.
    /// «Играть» поверх наполовину обновлённой игры запускает смесь двух версий, а «Установить»
    /// вместо «Обновить» тянет с сервера сборку целиком вместо нескольких изменившихся файлов.
    /// </para>
    /// </summary>
    public class ActionButtonStateTests {
        /// <summary>Игры на диске нет — предлагаем установку.</summary>
        [Fact]
        public void НеустановленнаяИграЭтоУстановка() {
            Assert.Equal(
                ActionMode.Install,
                ActionButtonState.Decide(hasUpdateError: false, unfinishedUpdate: false, isInstalled: false, needsUpdate: false));
        }

        /// <summary>Установленная и совпадающая с эталоном игра готова к запуску.</summary>
        [Fact]
        public void АктуальнаяИграЭтоЗапуск() {
            Assert.Equal(
                ActionMode.Play,
                ActionButtonState.Decide(hasUpdateError: false, unfinishedUpdate: false, isInstalled: true, needsUpdate: false));
        }

        /// <summary>Установленная игра с расхождением требует обновления.</summary>
        [Fact]
        public void УстаревшаяИграЭтоОбновление() {
            Assert.Equal(
                ActionMode.Update,
                ActionButtonState.Decide(hasUpdateError: false, unfinishedUpdate: false, isInstalled: true, needsUpdate: true));
        }

        /// <summary>
        /// Оборванное обновление перебивает «Играть»: файлы игры смешаны из двух версий,
        /// и запускать такую сборку нельзя — её нужно докатить (C2).
        /// </summary>
        [Fact]
        public void ОборванноеОбновлениеПеребиваетЗапуск() {
            Assert.Equal(
                ActionMode.Update,
                ActionButtonState.Decide(hasUpdateError: false, unfinishedUpdate: true, isInstalled: true, needsUpdate: false));
        }

        /// <summary>
        /// След оборванного обновления в пустой папке тоже даёт «Обновить», а не «Установить»:
        /// разница в объёме закачки здесь ни при чём, восстановление всё равно идёт через ту же операцию.
        /// </summary>
        [Fact]
        public void ОборванноеОбновлениеБезФайловТожеОбновление() {
            Assert.Equal(
                ActionMode.Update,
                ActionButtonState.Decide(hasUpdateError: false, unfinishedUpdate: true, isInstalled: false, needsUpdate: false));
        }

        /// <summary>Сорвавшаяся попытка важнее всего остального: пользователю предлагают повторить именно её.</summary>
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, false, false)]
        public void СорвавшаясяПопыткаДаётПовтор(bool unfinished, bool installed, bool needsUpdate) {
            Assert.Equal(
                ActionMode.Retry,
                ActionButtonState.Decide(hasUpdateError: true, unfinishedUpdate: unfinished, isInstalled: installed, needsUpdate: needsUpdate));
        }

        /// <summary>Технических работ нет — не запрещено ничего.</summary>
        [Fact]
        public void БезТехработНичегоНеЗапрещено() {
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Install, MaintenanceState.Off));
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Update, MaintenanceState.Off));
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Retry, MaintenanceState.Off));
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Play, MaintenanceState.Off));
        }

        /// <summary>
        /// Режим техработ без явных флагов запрещает установку и обновление, но не запуск:
        /// игра уже лежит на диске и обычно работает без сервера.
        /// </summary>
        [Fact]
        public void ТехработыБезФлаговЗапрещаютЗакачкуНоНеЗапуск() {
            var state = new MaintenanceState { Enabled = true };

            Assert.True(ActionButtonState.IsBlockedByMaintenance(ActionMode.Install, state));
            Assert.True(ActionButtonState.IsBlockedByMaintenance(ActionMode.Update, state));
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Play, state));
        }

        /// <summary>
        /// «Повторить» приравнено к обновлению: за кнопкой стоит та же закачка, и запрет
        /// на обновление обязан её накрыть — иначе техработы обходятся одним нажатием.
        /// </summary>
        [Fact]
        public void ПовторПодчиняетсяЗапретуОбновления() {
            var state = new MaintenanceState { Enabled = true, Blocks = new MaintenanceBlocks { Update = true, Install = false } };

            Assert.True(ActionButtonState.IsBlockedByMaintenance(ActionMode.Retry, state));
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Install, state));
        }

        /// <summary>Сервер может запретить и запуск — например, когда работы идут на игровых серверах.</summary>
        [Fact]
        public void ЗапускЗапрещаетсяОтдельнымФлагом() {
            var state = new MaintenanceState { Enabled = true, Blocks = new MaintenanceBlocks { Launch = true } };

            Assert.True(ActionButtonState.IsBlockedByMaintenance(ActionMode.Play, state));
        }

        /// <summary>Служебные режимы кнопки под запрет не попадают: они и так ничего не запускают.</summary>
        [Fact]
        public void СлужебныеРежимыНеЗапрещаются() {
            var state = new MaintenanceState { Enabled = true };

            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Checking, state));
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Cancel, state));
            Assert.False(ActionButtonState.IsBlockedByMaintenance(ActionMode.Maintenance, state));
        }

        /// <summary>Надписи и стили кнопки: их читает пользователь, поэтому проверяем дословно.</summary>
        [Fact]
        public void ОформлениеКнопкиСоответствуетРежиму() {
            AssertLook(ActionMode.Install, "Установить", true, "Style.ActionButton.Install");
            AssertLook(ActionMode.Update, "Обновить", true, "Style.ActionButton.Update");
            AssertLook(ActionMode.Play, "Играть", true, "Style.ActionButton.Play");
            AssertLook(ActionMode.Retry, "Повторить", true, "Style.ActionButton.Retry");
            AssertLook(ActionMode.Cancel, "Отмена", true, "Style.ActionButton.Cancel");

            // Ждущая позиция — нейтральная кнопка: красная «Отмена» обещает остановить
            // процесс, а процесса ещё нет.
            AssertLook(ActionMode.Dequeue, "Убрать из очереди", true, "Style.ActionButton.Dequeue");
            AssertLook(ActionMode.Deleting, "Удаление…", false, "Style.ActionButton.Checking");
            AssertLook(ActionMode.Checking, "Проверка…", false, "Style.ActionButton.Checking");
            AssertLook(ActionMode.Maintenance, "Технические работы", false, "Style.ActionButton.Checking");
        }

        /// <summary>
        /// Недоступные режимы обязаны быть именно недоступными: кнопка «Проверка…», которую
        /// можно нажать, запускает закачку по ещё не проверенным файлам.
        /// </summary>
        [Fact]
        public void ЗапрещающиеРежимыНеНажимаются() {
            Assert.False(ActionButtonState.Appearance(ActionMode.Checking).IsEnabled);
            Assert.False(ActionButtonState.Appearance(ActionMode.Maintenance).IsEnabled);
            Assert.False(ActionButtonState.Appearance(ActionMode.Deleting).IsEnabled);
        }

        private static void AssertLook(ActionMode mode, string content, bool enabled, string styleKey) {
            var look = ActionButtonState.Appearance(mode);

            Assert.Equal(content, look.Content);
            Assert.Equal(enabled, look.IsEnabled);
            Assert.Equal(styleKey, look.StyleKey);
        }
    }

    /// <summary>
    /// Набор игр с уже проверенным статусом.
    /// <para>
    /// Пока статус игры неизвестен, кнопка действия заблокирована. Игра, потерявшаяся
    /// в этом наборе, оставит пользователя с вечной «Проверкой…» и невозможностью играть (C4).
    /// </para>
    /// </summary>
    public class VerifiedGamesTests {
        /// <summary>Непроверенная игра — статус неизвестен, действия блокируются.</summary>
        [Fact]
        public void НепровереннаяИграСчитаетсяНеизвестной() {
            Assert.False(new VerifiedGames().IsKnown("game"));
        }

        /// <summary>Отметка о проверке видна сразу.</summary>
        [Fact]
        public void ПослеОтметкиСтатусИзвестен() {
            var verified = new VerifiedGames();
            verified.MarkKnown("game");

            Assert.True(verified.IsKnown("game"));
        }

        /// <summary>Регистр идентификатора не должен превращать проверенную игру в непроверенную.</summary>
        [Fact]
        public void РегистрИдентификатораНеВажен() {
            var verified = new VerifiedGames();
            verified.MarkKnown("Lethal-Company");

            Assert.True(verified.IsKnown("lethal-company"));
        }

        /// <summary>
        /// Нет выбранной игры — блокировать нечего: иначе кнопка залипала бы в «Проверке…»
        /// на пустом списке.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойИдентификаторСчитаетсяИзвестным(string? gameId) {
            var verified = new VerifiedGames();
            verified.MarkKnown(gameId);

            Assert.True(verified.IsKnown(gameId));
        }

        /// <summary>Сброс заставляет пересчитать статусы заново — так делают при обновлении списка игр.</summary>
        [Fact]
        public void СбросЗабываетВсеПроверки() {
            var verified = new VerifiedGames();
            verified.MarkKnown("a");
            verified.MarkKnown("b");

            verified.Reset();

            Assert.False(verified.IsKnown("a"));
            Assert.False(verified.IsKnown("b"));
        }
    }
}
