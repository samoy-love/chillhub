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
    }
}
