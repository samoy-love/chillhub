// <copyright file="VersionSwitchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Блок «переключение версии» на странице игры.
    /// <para>
    /// Это единственный способ откатиться на старую сборку — и единственное место, где
    /// пользователь может незаметно для себя стереть новый контент. Поэтому проверяется
    /// и то, что кнопка доступна ровно тогда, когда переключение имеет смысл, и то, что
    /// откат назван откатом в тексте вопроса: без предупреждения человек соглашается,
    /// а потом не может играть с теми, у кого версия новее.
    /// </para>
    /// </summary>
    public class VersionSwitchTests {
        /// <summary>Пока версия не выбрана, кнопка недоступна, а подсказку не трогаем.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void БезВыбраннойВерсииКнопкаНедоступна(string? selected) {
            var view = VersionSwitch.Compute(selected, "1.0.0", "1.1.0", false, false, false);

            Assert.False(view.CanSwitch);
            Assert.Null(view.Hint);
        }

        /// <summary>Уже установленную версию ставить незачем — кнопка гаснет, подсказка объясняет почему.</summary>
        [Fact]
        public void УстановленнуюВерсиюПереключатьНекуда() {
            var view = VersionSwitch.Compute("1.0.0", "1.0.0", "1.0.0", false, false, false);

            Assert.False(view.CanSwitch);
            Assert.Equal("Эта версия уже установлена.", view.Hint);
        }

        /// <summary>
        /// При незавершённом обновлении повторная установка ТОЙ ЖЕ версии осмысленна:
        /// на диске лежит смесь двух сборок, и другого способа её собрать нет.
        /// </summary>
        [Fact]
        public void ПриНезавершённомОбновленииТаЖеВерсияДоступна() {
            var view = VersionSwitch.Compute("1.0.0", "1.0.0", "1.0.0", unfinished: true, isBusy: false, maintenanceBlocked: false);

            Assert.True(view.CanSwitch);
            Assert.Equal("Выбрана последняя версия.", view.Hint);
        }

        /// <summary>Выбор не последней версии заранее называет операцию откатом.</summary>
        [Fact]
        public void ВыборСтаройВерсииПредупреждаетОбОткате() {
            var view = VersionSwitch.Compute("1.0.0", "1.2.0", "1.2.0", false, false, false);

            Assert.True(view.CanSwitch);
            Assert.Equal("Внимание: 1.0.0 — не последняя версия. Установка будет откатом с 1.2.0.", view.Hint);
        }

        /// <summary>Идёт закачка — переключать версию нельзя, иначе две операции полезут в одну папку.</summary>
        [Fact]
        public void ВоВремяЗакачкиПереключениеЗапрещено() {
            var view = VersionSwitch.Compute("1.0.0", "1.2.0", "1.2.0", false, isBusy: true, maintenanceBlocked: false);

            Assert.False(view.CanSwitch);
        }

        /// <summary>
        /// Технические работы запрещают переключение и говорят об этом прямо: неактивная
        /// кнопка без объяснения выглядит поломкой лаунчера.
        /// </summary>
        [Fact]
        public void ТехническиеРаботыБлокируютПереключение() {
            var view = VersionSwitch.Compute("1.0.0", "1.2.0", "1.2.0", unfinished: true, isBusy: false, maintenanceBlocked: true);

            Assert.False(view.CanSwitch);
            Assert.Equal("Переключение версии недоступно: на сервере идут технические работы.", view.Hint);
        }

        /// <summary>
        /// Неизвестная последняя версия не превращает установку в откат: список игр мог
        /// не загрузиться, и пугать пользователя потерей контента не за что.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void БезИзвестнойПоследнейВерсииОткатаНет(string? latest) {
            var view = VersionSwitch.Compute("1.0.0", string.Empty, latest, false, false, false);

            Assert.Equal("Выбрана последняя версия.", view.Hint);
            Assert.False(VersionSwitch.BuildPrompt("1.0.0", latest).IsRollback);
        }

        /// <summary>Регистр версии не должен выдавать ту же сборку за откат.</summary>
        [Fact]
        public void РегистрВерсииНеДелаетИзУстановкиОткат() {
            Assert.False(VersionSwitch.BuildPrompt("1.0.0-RC", "1.0.0-rc").IsRollback);
        }

        /// <summary>Вопрос об откате называет обе версии и последствия, а не только «продолжить?».</summary>
        [Fact]
        public void ВопросОбОткатеНазываетПоследствия() {
            var prompt = VersionSwitch.BuildPrompt("1.0.0", "1.2.0");

            Assert.True(prompt.IsRollback);
            Assert.Equal("Переключение версии (откат)", prompt.Title);
            Assert.Contains("Это откат, а не обновление", prompt.Text, StringComparison.Ordinal);
            Assert.Contains("1.0.0", prompt.Text, StringComparison.Ordinal);
            Assert.Contains("1.2.0", prompt.Text, StringComparison.Ordinal);
            Assert.Contains("Сетевая игра", prompt.Text, StringComparison.Ordinal);
        }

        /// <summary>Переустановка последней версии откатом не называется — иначе предупреждение обесценится.</summary>
        [Fact]
        public void ВопросОПоследнейВерсииНеПугаетОткатом() {
            var prompt = VersionSwitch.BuildPrompt("1.2.0", "1.2.0");

            Assert.False(prompt.IsRollback);
            Assert.Equal("Переключение версии", prompt.Title);
            Assert.DoesNotContain("откат", prompt.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
