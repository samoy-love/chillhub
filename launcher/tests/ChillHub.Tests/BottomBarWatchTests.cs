// <copyright file="BottomBarWatchTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
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

        private static (TextBlock Status, ProgressBar Progress, System.Collections.Generic.List<object?> Calls, BottomBarWatch Watch) Attach() {
            var status = new TextBlock();
            var progress = new ProgressBar();
            var calls = new System.Collections.Generic.List<object?>();
            var watch = BottomBarWatch.Attach(status, progress, (s, e) => calls.Add(s));
            return (status, progress, calls, watch);
        }
    }
}
