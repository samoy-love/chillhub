// <copyright file="ToastHostTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Всплывающие уведомления главного экрана.
    /// <para>
    /// Тост — единственный способ сказать пользователю «скопировано», «сохранено»,
    /// «не получилось» без модального окна. Отказ здесь тихий и потому опасный: сообщение
    /// либо не появляется вовсе, либо остаётся висеть навсегда поверх интерфейса, либо
    /// показывает предыдущий текст вместо нового. Ни одно из этих состояний не заметно
    /// ни в логах, ни в падениях.
    /// </para>
    /// <para>
    /// Прогон идёт на выделенном STA-потоке с работающим диспетчером и внеэкранным корнем
    /// визуального дерева (см. <see cref="OffscreenVisualRoot"/>): без цели отрисовки
    /// анимации WPF не тикают, и ни одна проверка «анимация дошла до конца» не наступила бы.
    /// Окон при этом не открывается.
    /// </para>
    /// <para>
    /// Длительности подменяются на миллисекунды: честные три секунды на каждый тост
    /// превратили бы прогон в минуту ожидания.
    /// </para>
    /// </summary>
    public class ToastHostTests : IDisposable {
        public ToastHostTests() {
            ToastHost.DefaultDuration = TimeSpan.FromMilliseconds(10);
            ToastHost.OverwriteOutDuration = TimeSpan.FromMilliseconds(10);
            ToastHost.FadeInDuration = TimeSpan.FromMilliseconds(10);
            ToastHost.FadeOutDuration = TimeSpan.FromMilliseconds(10);
        }

        public void Dispose() => ToastHost.ResetDurationsForTests();

        /// <summary>
        /// Основной сценарий целиком: текст встал на место, контейнер показался, а по истечении
        /// времени показа снова спрятался. Незакрывшийся тост навсегда закрыл бы угол экрана —
        /// поверх него уже ничего не нажать.
        /// </summary>
        [Fact]
        public void УведомлениеПоказываетсяИСамоУбирается() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var host = visual.Add(new Border());
                var text = new TextBlock();
                var toast = new ToastHost(host, text);

                toast.Show("файлы удалены");
                await toast.Current!;

                Assert.Equal("файлы удалены", text.Text);
                Assert.Equal(Visibility.Collapsed, host.Visibility);
                Assert.Equal(0.0, host.Opacity);
            });
        }

        /// <summary>
        /// Показ начинается со скрытого и полностью прозрачного состояния, даже если
        /// в разметке контейнер объявлен видимым. Иначе первый же тост моргнул бы готовым
        /// текстом до начала анимации.
        /// </summary>
        [Fact]
        public void ПередПервымПоказомКонтейнерПриводитсяВИсходноеСостояние() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var host = visual.Add(new Border { Visibility = Visibility.Visible, Opacity = 1.0 });
                var toast = new ToastHost(host, new TextBlock());

                toast.Show("привет");

                // Сдвиг подготовлен сразу, синхронно: анимация без него анимировать нечего.
                Assert.IsType<TranslateTransform>(host.RenderTransform);
                await toast.Current!;
            });
        }

        /// <summary>
        /// Чужой сдвиг не затирается: контейнер тоста в разметке уже может ехать по своей
        /// траектории, и подмена трансформации сбросила бы её на середине.
        /// </summary>
        [Fact]
        public void УжеЗаданныйСдвигСохраняется() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var mine = new TranslateTransform(3, 7);
                var host = visual.Add(new Border { RenderTransform = mine });
                var toast = new ToastHost(host, new TextBlock());

                toast.Show("привет");

                Assert.Same(mine, host.RenderTransform);
                await toast.Current!;
            });
        }

        /// <summary>
        /// Второе сообщение перебивает первое, а не встаёт в очередь: пользователю нужен
        /// свежий результат его последнего действия, а не пересказ предыдущего.
        /// </summary>
        [Fact]
        public void НовоеСообщениеПеребиваетПредыдущее() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var host = visual.Add(new Border());
                var text = new TextBlock();
                var toast = new ToastHost(host, text);

                ToastHost.DefaultDuration = TimeSpan.FromSeconds(30);
                toast.Show("первое");
                var first = toast.Current!;
                await UiThread.WaitUntil(() => text.Text == "первое" && host.Opacity >= 1.0, "первый тост появился");
                await UiThread.Settle(5);
                Assert.Equal(1.0, host.Opacity);

                ToastHost.DefaultDuration = TimeSpan.FromMilliseconds(10);
                toast.Show("второе");
                var second = toast.Current!;

                await first;
                await second;

                Assert.NotSame(first, second);
                Assert.Equal("второе", text.Text);
                Assert.Equal(Visibility.Collapsed, host.Visibility);
            });
        }

        /// <summary>
        /// Перебитый показ завершается молча. Он живёт в задаче, которую никто не ждёт:
        /// вылети из неё исключение — оно всплыло бы как необработанное и уронило процесс.
        /// </summary>
        [Fact]
        public void ПеребитыйПоказЗавершаетсяБезИсключения() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var host = visual.Add(new Border());
                var toast = new ToastHost(host, new TextBlock());

                ToastHost.DefaultDuration = TimeSpan.FromSeconds(30);
                toast.Show("первое");
                var first = toast.Current!;

                ToastHost.DefaultDuration = TimeSpan.FromMilliseconds(10);
                toast.Show("второе");
                var second = toast.Current!;

                await first;
                await second;

                Assert.Equal(TaskStatus.RanToCompletion, first.Status);
                Assert.Equal(TaskStatus.RanToCompletion, second.Status);
            });
        }

        /// <summary>
        /// Уже видимый тост сначала убирается и только потом меняет текст. Мгновенная подмена
        /// строки под пользовательским взглядом читается как сбой, а не как новое сообщение.
        /// </summary>
        [Fact]
        public void ВидимыйТостСначалаУбираетсяИЛишьПотомМеняетТекст() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var host = visual.Add(new Border());
                var text = new TextBlock();
                var toast = new ToastHost(host, text);

                ToastHost.DefaultDuration = TimeSpan.FromSeconds(30);
                toast.Show("первое");
                var first = toast.Current!;
                await UiThread.WaitUntil(() => text.Text == "первое" && host.Opacity >= 1.0, "первый тост появился");
                await UiThread.Settle(5);
                Assert.Equal(1.0, host.Opacity);

                ToastHost.DefaultDuration = TimeSpan.FromMilliseconds(10);
                ToastHost.OverwriteOutDuration = TimeSpan.FromSeconds(30);
                toast.Show("второе");
                var second = toast.Current!;

                // Показ дошёл ровно до ожидания анимации ухода — текст пока прежний.
                Assert.Equal("первое", text.Text);

                ToastHost.OverwriteOutDuration = TimeSpan.FromMilliseconds(10);
                toast.Show("третье");
                await first;
                await second;
                await toast.Current!;

                Assert.Equal("третье", text.Text);
            });
        }

        /// <summary>
        /// Длительность показа берётся из аргумента, когда она задана: у сообщения об ошибке
        /// и у «скопировано» разная цена невнимательности, и три секунды подходят не всем.
        /// </summary>
        [Fact]
        public void ЯвнаяДлительностьПеребиваетЗначениеПоУмолчанию() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var host = visual.Add(new Border());
                var text = new TextBlock();
                var toast = new ToastHost(host, text);

                // По умолчанию тост висел бы полминуты — тест не дождался бы конца показа.
                ToastHost.DefaultDuration = TimeSpan.FromSeconds(30);
                toast.Show("быстрое", TimeSpan.FromMilliseconds(10));
                await toast.Current!;

                Assert.Equal("быстрое", text.Text);
                Assert.Equal(Visibility.Collapsed, host.Visibility);
            });
        }

        /// <summary>
        /// Анимация может не завестись — например, сдвиг пришёл замороженным из общего ресурса
        /// разметки. Тогда тост обязан появиться мгновенно: сообщение важнее плавности,
        /// а необработанный сбой в фоновой задаче уронил бы весь лаунчер.
        /// </summary>
        [Fact]
        public void СбойАнимацииНеОтменяетСообщение() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var frozen = new TranslateTransform(0, 20);
                frozen.Freeze();
                var host = visual.Add(new Border { RenderTransform = frozen });
                var text = new TextBlock();
                var toast = new ToastHost(host, text);

                toast.Show("сообщение");
                await toast.Current!;

                Assert.Equal("сообщение", text.Text);
                Assert.Equal(Visibility.Visible, host.Visibility);

                // Прозрачность выставлена мгновенно, но поверх неё доигрывает уже
                // запущенная анимация — окончательное значение видно после её конца.
                await UiThread.WaitUntil(() => host.Opacity >= 1.0, "тост стал непрозрачным");
            });
        }

        /// <summary>
        /// Тостов за сессию сотни, и каждый заводит свой источник отмены. Показ обязан
        /// освобождать его и не оставлять себя «текущим» — иначе утечки копятся молча.
        /// </summary>
        [Fact]
        public void ПодрядИдущиеПоказыНеНакапливаютСостояние() {
            UiThread.Run(async () => {
                using var visual = new OffscreenVisualRoot();
                var host = visual.Add(new Border());
                var text = new TextBlock();
                var toast = new ToastHost(host, text);

                for (var i = 0; i < 5; i++) {
                    toast.Show($"сообщение {i}");
                    await toast.Current!;
                }

                Assert.Equal("сообщение 4", text.Text);
                Assert.Equal(Visibility.Collapsed, host.Visibility);
            });
        }
    }
}
