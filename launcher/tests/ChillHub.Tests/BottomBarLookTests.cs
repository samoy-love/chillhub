// <copyright file="BottomBarLookTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Нижняя панель главного экрана: что на ней видно.
    /// <para>
    /// Ошибка здесь не падает исключением — она молча оставляет внизу экрана строку
    /// пустоты. Ровно так и было: панель переживала конец работы и держала высоту под
    /// пустой строкой или под «Готово» до следующей закачки. Через живое окно это не
    /// проверить, потому решение и вынесено из страницы.
    /// </para>
    /// </summary>
    public class BottomBarLookTests {
        /// <summary>В покое панели нет вовсе: пустая полоса сообщает о процессе, которого нет.</summary>
        [Fact]
        public void ВПокоеПанелиНет() {
            var look = BottomBarLook.Decide(false, false, 0, "Готово", string.Empty, string.Empty);

            Assert.False(look.Panel);
            Assert.False(look.Progress);
            Assert.False(look.Status);
        }

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА: конец работы не оставляет за собой строку. Бегунок гасят в
        /// finally, значение полосы к этому времени ноль — и панель обязана уйти целиком,
        /// а не остаться пустой строкой высоты.
        /// </summary>
        [Fact]
        public void ПогасшийБегунокУноситПанель() {
            var look = BottomBarLook.Decide(false, false, 0, string.Empty, string.Empty, string.Empty);

            Assert.False(look.Panel);
            Assert.False(look.Status);
        }

        /// <summary>Пустой статус под идущей полосой не занимает строку.</summary>
        [Fact]
        public void ПустойСтатусПодПолосойСтрокиНеЗанимает() {
            var look = BottomBarLook.Decide(false, true, 0, string.Empty, string.Empty, string.Empty);

            Assert.True(look.Panel);
            Assert.True(look.Progress);
            Assert.False(look.Status);
        }

        /// <summary>
        /// «Готово.» с точкой — то же молчание. Общий с другими экранами код отчитывается
        /// о конце работы то с точкой, то без; панель, не узнавшая отчёт, оставалась
        /// висеть на экране вместе с ним.
        /// </summary>
        [Fact]
        public void ГотовоСТочкойПанельНеДержит() {
            var look = BottomBarLook.Decide(false, false, 0, "Готово.", string.Empty, string.Empty);

            Assert.False(look.Panel);
            Assert.False(look.Status);
        }

        /// <summary>«Готово» — тоже молчание: отчёт о конце работы поверх идущей работы сбивает с толку.</summary>
        [Fact]
        public void ГотовоПодПолосойСтрокиНеЗанимает() {
            var look = BottomBarLook.Decide(false, false, 42, "  Готово  ", string.Empty, string.Empty);

            Assert.True(look.Panel);
            Assert.False(look.Status);
        }

        /// <summary>Настоящее сообщение открывает панель даже без всякой полосы.</summary>
        [Fact]
        public void СообщениеОткрываетПанельБезПолосы() {
            var look = BottomBarLook.Decide(
                false, false, 0, "Моды восстановлены. Нажмите ещё раз, чтобы запустить игру.", string.Empty, string.Empty);

            Assert.True(look.Panel);
            Assert.True(look.Status);
            Assert.False(look.Progress);
        }

        /// <summary>Очередь держит панель открытой сама по себе: в ней карточки, а не статус.</summary>
        [Fact]
        public void ОчередьДержитПанель() {
            var look = BottomBarLook.Decide(true, false, 0, "Готово", string.Empty, string.Empty);

            Assert.True(look.Panel);
            Assert.False(look.Progress);
        }

        /// <summary>Полоса с известным процентом — тоже работа.</summary>
        [Fact]
        public void НенулеваяПолосаСчитаетсяРаботой() {
            var look = BottomBarLook.Decide(false, false, 7, "Готово", string.Empty, string.Empty);

            Assert.True(look.Progress);
            Assert.True(look.Panel);
        }

        /// <summary>Подписи скорости и объёма показываются только с текстом: пустые держали ~40px пустоты.</summary>
        [Fact]
        public void ПустыеПодписиПрячутся() {
            var empty = BottomBarLook.Decide(false, true, 0, "Обновление…", "   ", null);
            var full = BottomBarLook.Decide(false, true, 0, "Обновление…", "5 МБ/с · осталось 2 мин", "12 из 40 · 300 МБ");

            Assert.False(empty.SpeedEta);
            Assert.False(empty.FilesSize);
            Assert.True(full.SpeedEta);
            Assert.True(full.FilesSize);
        }
    }
}
