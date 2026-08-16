// <copyright file="ShellTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Maintenance;
    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Оболочка лаунчера: переходы между страницами и баннер технических работ.
    /// <para>
    /// Переход на уже открытую страницу — не просто лишняя работа. Каждый такой переход
    /// создаёт ВТОРОЙ экземпляр страницы: у каталога это второй <c>FeedbackService</c>
    /// со своей копией очереди и своим таймером, и таймер старой страницы перезаписывал
    /// feedback_queue.json без нового сообщения — оно терялось навсегда.
    /// </para>
    /// <para>
    /// Баннер работ проверяется здесь с той стороны, которой не касается
    /// <c>MaintenanceStateTests</c>: не «как сервер описал работы», а «показала ли
    /// оболочка баннер и убрала ли его, когда работы закончились».
    /// </para>
    /// </summary>
    public class ShellTests {
        /// <summary>
        /// Пустая область содержимого — переход нужен. Иначе окно осталось бы белым.
        /// </summary>
        [Fact]
        public void ПустаяОбластьСодержимогоТребуетПерехода()
            => Assert.True(ShellNavigation.NeedsNavigation(null, typeof(HomePageStub)));

        /// <summary>
        /// Стартовая страница — каталог: окно открывает его сразу, и повторный переход
        /// на каталог из «Назад» или из кнопки не должен создавать вторую копию.
        /// </summary>
        [Fact]
        public void ПовторныйПереходНаОткрытуюСтраницуНеНужен()
            => Assert.False(ShellNavigation.NeedsNavigation(new HomePageStub(), typeof(HomePageStub)));

        /// <summary>
        /// Открыта другая страница — переход нужен: иначе кнопка «Настройки» перестала бы
        /// работать после первого возврата на каталог.
        /// </summary>
        [Fact]
        public void ПереходНаДругуюСтраницуНужен()
            => Assert.True(ShellNavigation.NeedsNavigation(new HomePageStub(), typeof(SettingsPageStub)));

        /// <summary>
        /// Открытая страница-наследник считается той же самой: проверка идёт как <c>is</c>,
        /// а не по точному совпадению типа.
        /// </summary>
        [Fact]
        public void НаследникОткрытойСтраницыСчитаетсяТойЖе()
            => Assert.False(ShellNavigation.NeedsNavigation(new DerivedHomePageStub(), typeof(HomePageStub)));

        /// <summary>
        /// «Назад» со страницы настроек идёт по истории, если она есть: пользователь
        /// вернётся туда, откуда пришёл, а не на каталог с потерянным местом в списке.
        /// </summary>
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void КнопкаНазадИдётПоИсторииТолькоКогдаЕстьКуда(bool hasService, bool canGoBack, bool expected)
            => Assert.Equal(expected, ShellNavigation.ShouldGoBack(hasService, canGoBack));

        // ---- Баннер технических работ ----

        /// <summary>
        /// Работ нет — баннера нет и текста нет. Пустой текст важен не меньше скрытого
        /// баннера: разметка шапки резервирует под него место.
        /// </summary>
        [Fact]
        public void БезРаботБаннерНеПоказывается() {
            var view = MaintenanceBannerView.For(new MaintenanceState { Enabled = false, Reason = "уже закончили" });

            Assert.False(view.Visible);
            Assert.Equal(string.Empty, view.Text);
        }

        /// <summary>
        /// Сервер молчит (недоступен, ответ не разобрался) — баннер не показываем.
        /// Пугать человека работами из-за оборванной сети нельзя: лаунчер и без сервера
        /// умеет запускать уже установленную игру.
        /// </summary>
        [Fact]
        public void МолчаниеСервераБаннерНеПоказывает() {
            var view = MaintenanceBannerView.For(null);

            Assert.False(view.Visible);
            Assert.Equal(string.Empty, view.Text);
        }

        /// <summary>Работы идут — баннер появляется и называет причину.</summary>
        [Fact]
        public void ВоВремяРаботБаннерПоявляетсяСПричиной() {
            var view = MaintenanceBannerView.For(new MaintenanceState {
                Enabled = true,
                Reason = "Меняем диск на сервере раздачи",
            });

            Assert.True(view.Visible);
            Assert.Contains("Меняем диск на сервере раздачи", view.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Причину сервер назвать не обязан — тогда показывается общий текст. Пустой
        /// баннер выглядел бы сбоем разметки, а не сообщением.
        /// </summary>
        [Fact]
        public void БезПричиныБаннерПоказываетОбщийТекст() {
            var view = MaintenanceBannerView.For(new MaintenanceState { Enabled = true });

            Assert.True(view.Visible);
            Assert.False(string.IsNullOrWhiteSpace(view.Text));
        }

        /// <summary>
        /// Главное свойство баннера — он ИСЧЕЗАЕТ. Сервер сообщает об окончании работ
        /// тем же опросом, и оставшийся висеть баннер уверял бы человека, что установка
        /// по-прежнему запрещена, когда всё уже работает.
        /// </summary>
        [Fact]
        public void ПослеОкончанияРаботБаннерУбирается() {
            var during = MaintenanceBannerView.For(new MaintenanceState { Enabled = true, Reason = "работы" });
            Assert.True(during.Visible);

            var after = MaintenanceBannerView.For(new MaintenanceState { Enabled = false });

            Assert.False(after.Visible);
            Assert.Equal(string.Empty, after.Text);
        }

        /// <summary>
        /// Названный сервером срок окончания попадает в баннер: без него человек не знает,
        /// подождать ему десять минут или закрыть лаунчер до вечера.
        /// </summary>
        [Fact]
        public void СрокОкончанияРаботПопадаетВБаннер() {
            var now = DateTimeOffset.Now;
            var view = MaintenanceBannerView.For(new MaintenanceState {
                Enabled = true,
                Reason = "Обновляем раздачу",
                ServerTime = now,
                EndsAt = now.AddMinutes(30),
            });

            Assert.Contains("Обновляем раздачу", view.Text, StringComparison.Ordinal);
            Assert.Contains("Ожидаемое окончание", view.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Пункт запуска в трее называет игру, а без выбранной игры выключен: нажатие,
        /// которое ничего не делает, читается как сломанное меню.
        /// </summary>
        [Fact]
        public void ПунктТреяНазываетВыбраннуюИгру() {
            UiThread.Run(() => {
                using var tray = new TrayService();

                tray.SetCurrentGame("Lethal Company");
                Assert.Equal("Играть: Lethal Company", tray.PlayItemText);
                Assert.True(tray.PlayItemEnabled);

                tray.SetCurrentGame(null);
                Assert.Equal(TrayService.NoGamePlayText, tray.PlayItemText);
                Assert.False(tray.PlayItemEnabled);

                // Пробелы — это тоже «имени нет»: подпись «Играть:  » выглядела бы сбоем.
                tray.SetCurrentGame("   ");
                Assert.Equal(TrayService.NoGamePlayText, tray.PlayItemText);
                Assert.False(tray.PlayItemEnabled);

                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        /// <summary>
        /// Подсказка над значком показывает ход загрузок и не превышает потолок NotifyIcon:
        /// слишком длинный текст там не «обрезается», а бросает исключение.
        /// </summary>
        [Fact]
        public void ПодсказкаТреяПоказываетЗагрузкиИНеПревышаетЛимит() {
            Assert.Equal("ChillHub", TrayService.BuildTip(null));
            Assert.Equal("ChillHub", TrayService.BuildTip("  "));
            Assert.Equal("ChillHub — 38% · ещё 2", TrayService.BuildTip("38% · ещё 2"));

            var longTip = TrayService.BuildTip(new string('x', 200));
            Assert.True(longTip.Length <= 63);
            Assert.EndsWith("…", longTip, StringComparison.Ordinal);

            UiThread.Run(() => {
                using var tray = new TrayService();
                tray.SetStatus("38%");
                Assert.Equal("ChillHub — 38%", tray.TipText);
                tray.SetStatus(string.Empty);
                Assert.Equal("ChillHub", tray.TipText);
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        /// <summary>
        /// Страницы изображают заглушки: настоящие требуют STA-потока и лезут в сеть
        /// за списком игр, а решение о переходе от их содержимого не зависит.
        /// </summary>
        private class HomePageStub {
        }

        /// <summary>Страница другого рода — «Настройки».</summary>
        private sealed class SettingsPageStub {
        }

        /// <summary>Наследник главной страницы: проверка типа обязана считать его ею же.</summary>
        private sealed class DerivedHomePageStub : HomePageStub {
        }
    }
}
