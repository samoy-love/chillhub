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
        /// Свежий конфиг — окно минимального размера: 1180×760 по умолчанию на ноутбуке с
        /// масштабом 125–150 % уходили за край экрана. Мусор в конфиге (меньше минимума,
        /// NaN) — тоже минимальный размер.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 980, 640)]
        [InlineData(500, 400, 980, 640)]
        [InlineData(double.NaN, 700, 980, 640)]
        [InlineData(1400, 900, 1400, 900)]
        public void ОкноОткрываетсяМинимальнымПокаЕгоНеМеняли(double savedW, double savedH, double expectW, double expectH) {
            var cfg = new ChillHub.Core.AppConfig { WindowWidth = savedW, WindowHeight = savedH };

            var size = WindowSizeMemory.Restore(cfg, 980, 640);

            Assert.Equal(expectW, size.Width);
            Assert.Equal(expectH, size.Height);
            Assert.False(size.Maximized);
        }

        /// <summary>
        /// Растянутое пользователем окно запоминается, развёрнутое — флагом плюс размером
        /// нормального состояния; тот же размер повторно конфиг не трогает.
        /// </summary>
        [Fact]
        public void РазмерОкнаЗапоминаетсяТолькоКогдаМенялся() {
            var cfg = new ChillHub.Core.AppConfig();

            Assert.True(WindowSizeMemory.Remember(cfg, 1400, 900, maximized: false));
            Assert.Equal(1400, cfg.WindowWidth);
            Assert.False(WindowSizeMemory.Remember(cfg, 1400.4, 900, maximized: false));

            Assert.True(WindowSizeMemory.Remember(cfg, 1400, 900, maximized: true));
            Assert.True(cfg.WindowMaximized);
            Assert.True(WindowSizeMemory.Restore(cfg, 980, 640).Maximized);
            Assert.Equal(1400, WindowSizeMemory.Restore(cfg, 980, 640).Width);
        }

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
        /// Причина и срок отдаются порознь: баннер набирает их разным весом, чтобы главное
        /// (что случилось) читалось раньше второстепенного (когда кончится). Без срока
        /// вторая строка пуста, а не «null» и не общий текст.
        /// </summary>
        [Fact]
        public void БаннерОтдаётПричинуИСрокПорознь() {
            var now = DateTimeOffset.Now;
            var withEta = MaintenanceBannerView.For(new MaintenanceState {
                Enabled = true,
                Reason = "Обновляем раздачу",
                ServerTime = now,
                EndsAt = now.AddMinutes(30),
            });
            Assert.Equal("Обновляем раздачу.", withEta.Reason);
            Assert.StartsWith("Ожидаемое окончание", withEta.Eta, StringComparison.Ordinal);
            Assert.Equal($"{withEta.Reason} {withEta.Eta}", withEta.Text);

            var noEta = MaintenanceBannerView.For(new MaintenanceState { Enabled = true, Reason = "Обновляем раздачу" });
            Assert.Equal("Обновляем раздачу.", noEta.Reason);
            Assert.Equal(string.Empty, noEta.Eta);
            Assert.Equal(noEta.Reason, noEta.Text);
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
            Assert.Equal("Chill Hub", TrayService.BuildTip(null));
            Assert.Equal("Chill Hub", TrayService.BuildTip("  "));
            Assert.Equal("Chill Hub — 38% · ещё 2", TrayService.BuildTip("38% · ещё 2"));

            var longTip = TrayService.BuildTip(new string('x', 200));
            Assert.True(longTip.Length <= 63);
            Assert.EndsWith("…", longTip, StringComparison.Ordinal);

            UiThread.Run(() => {
                using var tray = new TrayService();
                tray.SetStatus("38%");
                Assert.Equal("Chill Hub — 38%", tray.TipText);
                tray.SetStatus(string.Empty);
                Assert.Equal("Chill Hub", tray.TipText);
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
