// <copyright file="MaintenanceStateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Maintenance;

    using Xunit;

    /// <summary>
    /// Режим технических работ. Решения этого класса напрямую запрещают пользователю
    /// ставить, обновлять и запускать игры, поэтому умолчания важнее самих флагов:
    /// ошибка здесь либо молча снимает блокировку, либо отбирает уже установленную игру.
    /// </summary>
    public class MaintenanceStateTests {
        /// <summary>Выключенный режим не запрещает ничего, что бы ни лежало в Blocks.</summary>
        [Fact]
        public void ВыключенныйРежимНичегоНеЗапрещает() {
            var s = new MaintenanceState {
                Enabled = false,
                Blocks = new MaintenanceBlocks { Install = true, Update = true, Launch = true },
            };

            Assert.False(s.BlocksInstall);
            Assert.False(s.BlocksUpdate);
            Assert.False(s.BlocksPlay);
        }

        /// <summary>
        /// Умолчания при включённом режиме без явных флагов: установка и обновление
        /// запрещены (качать всё равно нечего), а запуск разрешён — игра уже на диске
        /// и обычно работает без сервера.
        /// </summary>
        [Fact]
        public void УмолчанияПриВключённомРежиме() {
            var s = new MaintenanceState { Enabled = true, Blocks = null };

            Assert.True(s.BlocksInstall);
            Assert.True(s.BlocksUpdate);
            Assert.False(s.BlocksPlay);
        }

        /// <summary>Явные флаги перекрывают умолчания в обе стороны.</summary>
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        public void ЯвныеФлагиПерекрываютУмолчания(bool install, bool update, bool launch) {
            var s = new MaintenanceState {
                Enabled = true,
                Blocks = new MaintenanceBlocks { Install = install, Update = update, Launch = launch },
            };

            Assert.Equal(install, s.BlocksInstall);
            Assert.Equal(update, s.BlocksUpdate);
            Assert.Equal(launch, s.BlocksPlay);
        }

        /// <summary>Готовое «выключено» не должно ничего запрещать.</summary>
        [Fact]
        public void ГотовоеВыключенноеСостояниеБезопасно() {
            Assert.False(MaintenanceState.Off.Enabled);
            Assert.False(MaintenanceState.Off.BlocksInstall);
            Assert.False(MaintenanceState.Off.BlocksUpdate);
            Assert.False(MaintenanceState.Off.BlocksPlay);
        }

        /// <summary>Без указанной причины показывается общий текст, а не пустая строка.</summary>
        [Fact]
        public void БезПричиныПоказываетсяОбщийТекст() {
            var text = new MaintenanceState { Enabled = true }.BuildBannerText();
            Assert.False(string.IsNullOrWhiteSpace(text));
        }

        /// <summary>Указанная причина попадает в баннер.</summary>
        [Fact]
        public void ПричинаПопадаетВБаннер() {
            var s = new MaintenanceState { Enabled = true, Reason = "  Переезд базы  " };
            Assert.Contains("Переезд базы", s.BuildBannerText(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Причину администратор пишет без точки, а баннер приписывает к ней срок. Без знака
        /// на стыке получалось «…раздачи Ожидаемое окончание…» — два предложения одним.
        /// </summary>
        [Fact]
        public void ПричинаБезТочкиПолучаетТочкуПередСроком() {
            var serverNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var s = new MaintenanceState {
                Enabled = true,
                Reason = "Меняем диск на сервере раздачи",
                ServerTime = serverNow,
                EndsAt = serverNow.AddHours(1),
            };

            Assert.Equal("Меняем диск на сервере раздачи.", s.BuildReasonText());
            Assert.Contains("раздачи. Ожидаемое окончание", s.BuildBannerText(), StringComparison.Ordinal);
        }

        /// <summary>Свой знак в конце причины не удваивается: «Скоро вернёмся!» остаётся как есть.</summary>
        /// <param name="reason">Причина с завершающим знаком.</param>
        [Theory]
        [InlineData("Скоро вернёмся!")]
        [InlineData("Переезд базы.")]
        [InlineData("Что-то пошло не так?")]
        [InlineData("Работы затянулись…")]
        public void СвойЗнакВКонцеПричиныСохраняется(string reason) {
            Assert.Equal(reason, new MaintenanceState { Enabled = true, Reason = reason }.BuildReasonText());
        }

        /// <summary>Без срока окончания обещаний о времени быть не должно.</summary>
        [Fact]
        public void БезСрокаНетОбещанийОВремени() {
            Assert.Equal(string.Empty, new MaintenanceState { Enabled = true }.BuildEtaText());
        }

        /// <summary>
        /// Остаток считается по часам СЕРВЕРА. Если бы считали по локальным, у пользователя
        /// со сбитыми часами корректный срок выглядел бы истёкшим — или наоборот.
        /// </summary>
        [Fact]
        public void ОстатокСчитаетсяПоЧасамСервера() {
            var serverNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var s = new MaintenanceState {
                Enabled = true,
                ServerTime = serverNow,
                EndsAt = serverNow.AddHours(2),
            };

            var eta = s.BuildEtaText();
            Assert.Contains("Ожидаемое окончание", eta, StringComparison.Ordinal);
            Assert.DoesNotContain("затянулись", eta, StringComparison.Ordinal);
        }

        /// <summary>
        /// Срок вышел, а сервер всё ещё сообщает о работах: обещать время нельзя,
        /// иначе баннер показывает прошедшее время как будущее.
        /// </summary>
        [Fact]
        public void ИстёкшийСрокНеОбещаетВремя() {
            var serverNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var s = new MaintenanceState {
                Enabled = true,
                ServerTime = serverNow,
                EndsAt = serverNow.AddHours(-1),
            };

            var eta = s.BuildEtaText();
            Assert.Contains("затянулись", eta, StringComparison.Ordinal);
            Assert.DoesNotContain("Ожидаемое окончание", eta, StringComparison.Ordinal);
        }

        /// <summary>Баннер объединяет причину и срок.</summary>
        [Fact]
        public void БаннерОбъединяетПричинуИСрок() {
            var serverNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var s = new MaintenanceState {
                Enabled = true,
                Reason = "Обновление сервера",
                ServerTime = serverNow,
                EndsAt = serverNow.AddHours(3),
            };

            var text = s.BuildBannerText();
            Assert.Contains("Обновление сервера", text, StringComparison.Ordinal);
            Assert.Contains("Ожидаемое окончание", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Неизменившийся ответ сервера не считается новым состоянием: опрос идёт раз в
        /// минуту, и без этого сравнения баннер перерисовывался бы и писал в лог на каждом
        /// круге. ServerTime в сравнение не входит намеренно — он меняется всегда.
        /// </summary>
        [Fact]
        public void ОдинаковыеСостоянияСчитаютсяРавными() {
            var a = Enabled("Меняем диск", install: true, update: true, launch: false);
            var b = Enabled("Меняем диск", install: true, update: true, launch: false);
            b.ServerTime = DateTimeOffset.UtcNow;

            Assert.True(a.SameAs(b));
            Assert.True(b.SameAs(a));
        }

        /// <summary>Выход из режима обязан считаться изменением, иначе баннер не снимется.</summary>
        [Fact]
        public void ВыключениеРежимаЭтоИзменение() {
            var on = Enabled("Работы", install: true, update: true, launch: true);

            Assert.False(on.SameAs(MaintenanceState.Off));
            Assert.False(MaintenanceState.Off.SameAs(on));
        }

        /// <summary>Смена причины меняет текст баннера — значит это другое состояние.</summary>
        [Fact]
        public void СменаПричиныЭтоИзменение() {
            var a = Enabled("Меняем диск", install: true, update: true, launch: false);
            var b = Enabled("Обновляем раздачу", install: true, update: true, launch: false);

            Assert.False(a.SameAs(b));
        }

        /// <summary>Отсутствующая причина и пустая строка — одно и то же, баннер одинаков.</summary>
        [Fact]
        public void ПустаяИОтсутствующаяПричинаРавны() {
            var a = Enabled(null, install: true, update: true, launch: false);
            var b = Enabled(string.Empty, install: true, update: true, launch: false);

            Assert.True(a.SameAs(b));
        }

        /// <summary>Сдвиг срока окончания виден пользователю в баннере — это изменение.</summary>
        [Fact]
        public void СменаСрокаЭтоИзменение() {
            var a = Enabled("Работы", install: true, update: true, launch: false);
            a.EndsAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var b = Enabled("Работы", install: true, update: true, launch: false);
            b.EndsAt = a.EndsAt.Value.AddHours(1);

            Assert.False(a.SameAs(b));
        }

        /// <summary>
        /// Смена любого флага блокировки — изменение: именно по ним страницы решают,
        /// гасить ли кнопки «Установить», «Обновить» и «Играть».
        /// </summary>
        [Theory]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, true)]
        public void СменаЛюбойБлокировкиЭтоИзменение(bool install, bool update, bool launch) {
            var a = Enabled("Работы", install: true, update: true, launch: false);
            var b = Enabled("Работы", install, update, launch);

            Assert.False(a.SameAs(b));
        }

        /// <summary>
        /// Пропущенный блок <c>blocks</c> и явные умолчания (install/update запрещены,
        /// launch разрешён) дают одинаковый набор запретов — состояние одно и то же.
        /// Сравнивать надо вычисленные запреты, а не сырые поля.
        /// </summary>
        [Fact]
        public void ОтсутствующиеФлагиРавныСвоимУмолчаниям() {
            var noBlocks = new MaintenanceState { Enabled = true, Reason = "Работы" };
            var explicitBlocks = Enabled("Работы", install: true, update: true, launch: false);

            Assert.True(noBlocks.SameAs(explicitBlocks));
        }

        /// <summary>Сравнение с null — не «то же самое», иначе первый ответ сервера потерялся бы.</summary>
        [Fact]
        public void СравнениеСNullВсегдаЛожь() {
            Assert.False(MaintenanceState.Off.SameAs(null));
        }

        private static MaintenanceState Enabled(string? reason, bool install, bool update, bool launch)
            => new MaintenanceState {
                Enabled = true,
                Reason = reason,
                Blocks = new MaintenanceBlocks { Install = install, Update = update, Launch = launch },
            };
    }
}
