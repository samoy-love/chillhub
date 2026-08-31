// <copyright file="BottomBarWatchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Windows;
    using System.Windows.Controls;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Подписка, по которой пересчитывается нижняя панель.
    /// <para>
    /// Забытое свойство здесь не падает и не пишется в лог: панель просто перестаёт
    /// пересчитываться и остаётся висеть пустой строкой внизу экрана. Так и было —
    /// слушали текст и значение полосы, а гасят её через IsIndeterminate, почти всегда
    /// при нулевом значении. Поэтому список наблюдаемого проверяется тестом, а не глазом.
    /// </para>
    /// <para>
    /// Прогон идёт на STA-потоке: контролы WPF на другом не создать.
    /// </para>
    /// </summary>
    public class BottomBarWatchTests {
        /// <summary>Смена текста статуса будит пересчёт: статус пишут два десятка мест страницы.</summary>
        [Fact]
        public void СменаСтатусаБудитПересчёт() {
            UiThread.Run(() => {
                var (status, progress, calls, watch) = Attach();
                using (watch) {
                    status.Text = "Проверяем файлы игры…";

                    Assert.True(calls.Count > 0);
                }
            });
        }

        /// <summary>Полоса, ушедшая с нуля, будит пересчёт.</summary>
        [Fact]
        public void ЗначениеПолосыБудитПересчёт() {
            UiThread.Run(() => {
                var (status, progress, calls, watch) = Attach();
                using (watch) {
                    progress.Value = 42;

                    Assert.True(calls.Count > 0);
                }
            });
        }

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА: погасший бегунок будит пересчёт. Его гасят в finally, где
        /// значение полосы уже ноль, — без этой подписки конец работы не менял на экране
        /// ничего, и панель оставалась висеть пустой строкой до следующей закачки.
        /// </summary>
        [Fact]
        public void ПогасшийБегунокБудитПересчёт() {
            UiThread.Run(() => {
                var (status, progress, calls, watch) = Attach();
                using (watch) {
                    progress.IsIndeterminate = true;
                    calls.Clear();

                    progress.IsIndeterminate = false;

                    Assert.True(calls.Count > 0);
                }
            });
        }

        /// <summary>
        /// После освобождения подписки нет: AddValueChanged держит контрол сильной ссылкой,
        /// и без снятия страница не собиралась бы сборщиком мусора.
        /// </summary>
        [Fact]
        public void ПослеОсвобожденияПодпискиНет() {
            UiThread.Run(() => {
                var (status, progress, calls, watch) = Attach();
                watch.Dispose();
                calls.Clear();

                status.Text = "Готово";
                progress.Value = 7;
                progress.IsIndeterminate = true;

                Assert.Empty(calls);
            });
        }

        /// <summary>Повторное освобождение ничего не ломает: Unloaded у страницы приходит не по одному разу.</summary>
        [Fact]
        public void ПовторноеОсвобождениеБезопасно() {
            UiThread.Run(() => {
                var (_, _, _, watch) = Attach();

                watch.Dispose();
                watch.Dispose();
            });
        }

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА ЖИЗНИ ПОДПИСКИ: со страницы уходят в игру и в новость и
        /// возвращаются обратно. Одноразовая подписка после первого такого захода умирала
        /// навсегда — панель замирала в том виде, в каком её застал уход, и после удаления
        /// игры внизу оставалось «Готово» под панелью, которой полагалось исчезнуть.
        /// </summary>
        [Fact]
        public void ВозвратНаСтраницуВоскрешаетПодписку() {
            UiThread.Run(() => {
                var (page, status, progress, calls, watch) = Follow();
                using (watch) {
                    Leave(page);
                    Enter(page);
                    calls.Clear();

                    status.Text = "Удаление файлов Risk of Rain 2…";
                    progress.IsIndeterminate = true;
                    progress.IsIndeterminate = false;

                    Assert.True(calls.Count > 0);
                }
            });
        }

        /// <summary>Возврат на страницу сам будит пересчёт: статус мог смениться, пока её не было.</summary>
        [Fact]
        public void ВозвратНаСтраницуБудитПересчёт() {
            UiThread.Run(() => {
                var (page, _, _, calls, watch) = Follow();
                using (watch) {
                    Leave(page);
                    calls.Clear();

                    Enter(page);

                    Assert.True(calls.Count > 0);
                }
            });
        }

        /// <summary>
        /// Пока страницы нет на экране, подписок нет: AddValueChanged держит контрол
        /// сильной ссылкой.
        /// </summary>
        [Fact]
        public void ВнеЭкранаПодписокНет() {
            UiThread.Run(() => {
                var (page, status, progress, calls, watch) = Follow();
                using (watch) {
                    Leave(page);
                    calls.Clear();

                    status.Text = "Готово";
                    progress.Value = 7;
                    progress.IsIndeterminate = true;

                    Assert.Empty(calls);
                }
            });
        }

        /// <summary>После освобождения возврат на страницу подписку уже не воскрешает.</summary>
        [Fact]
        public void ПослеОсвобожденияВозвратНичегоНеВоскрешает() {
            UiThread.Run(() => {
                var (page, status, _, calls, watch) = Follow();
                watch.Dispose();
                calls.Clear();

                Enter(page);
                status.Text = "Проверяем файлы игры…";

                Assert.Empty(calls);
            });
        }

        /// <summary>
        /// Повторный вход не плодит подписок: Loaded приходит не по одному разу, а вторая
        /// подписка на то же свойство звала бы пересчёт панели дважды на каждое изменение.
        /// </summary>
        [Fact]
        public void ПовторныйВходНеПлодитПодписок() {
            UiThread.Run(() => {
                var (page, status, _, calls, watch) = Follow();
                using (watch) {
                    Enter(page);
                    Enter(page);
                    calls.Clear();

                    status.Text = "Проверяем файлы игры…";

                    Assert.Single(calls);
                }
            });
        }

        /// <summary>Повторный уход со страницы безопасен: Unloaded приходит не по одному разу.</summary>
        [Fact]
        public void ПовторныйУходСоСтраницыБезопасен() {
            UiThread.Run(() => {
                var (page, _, _, _, watch) = Follow();
                using (watch) {
                    Leave(page);
                    Leave(page);
                }
            });
        }

        private static void Enter(FrameworkElement page)
            => page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, page));

        private static void Leave(FrameworkElement page)
            => page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, page));

        private static (Grid Page, TextBlock Status, ProgressBar Progress, System.Collections.Generic.List<object?> Calls, BottomBarWatch Watch) Follow() {
            var page = new Grid();
            var status = new TextBlock();
            var progress = new ProgressBar();
            page.Children.Add(status);
            page.Children.Add(progress);
            var calls = new System.Collections.Generic.List<object?>();
            var watch = BottomBarWatch.Follow(page, status, progress, (s, e) => calls.Add(s));
            return (page, status, progress, calls, watch);
        }

        private static (TextBlock Status, ProgressBar Progress, System.Collections.Generic.List<object?> Calls, BottomBarWatch Watch) Attach() {
            var status = new TextBlock();
            var progress = new ProgressBar();
            var calls = new System.Collections.Generic.List<object?>();
            var watch = BottomBarWatch.Attach(status, progress, (s, e) => calls.Add(s));
            return (status, progress, calls, watch);
        }
    }
}
