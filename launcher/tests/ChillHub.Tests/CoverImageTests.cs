// <copyright file="CoverImageTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Threading;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Картинка следует за своей строкой.
    /// <para>
    /// В очереди загрузок у PEAK стоял значок Drive Beyond Horizons: строка списка
    /// доставалась другой игре, а картинка в ней оставалась прежней. Причин было две, и
    /// каждой хватало в одиночку — переиспользование строки без повторного
    /// <c>Loaded</c> и уже приехавшая загрузка прошлой игры, которая ложилась в элемент
    /// вслепую. Оба случая проверяются здесь.
    /// </para>
    /// </summary>
    [Collection(ImageLoaderCollection.Name)]
    public class CoverImageTests : IDisposable {
        private const string Peak = "https://images.invalid/peak.png";
        private const string Drive = "https://images.invalid/drive.png";

        /// <summary>
        /// Значки различаются пропорциями: квадрат против вытянутого вдвое. Именно
        /// пропорциями, а не размером, — загрузчик раскодирует картинку под размер
        /// элемента, и по одной высоте два значка не различить.
        /// </summary>
        private const int Side = 36;

        public CoverImageTests() => ImageLoader.ResetForTests();

        public void Dispose() => ImageLoader.ResetForTests();

        /// <summary>
        /// ГЛАВНАЯ ПРОВЕРКА. Строку очереди отдали другой игре — значок обязан
        /// смениться. WPF при замене элемента коллекции не пересоздаёт строку, а
        /// подставляет ей новые данные в тот же самый <see cref="Image"/>: обработчик
        /// <c>Loaded</c> второй раз не приходит, и загрузка нового значка не начиналась
        /// никогда.
        /// </summary>
        [Fact]
        public void СтрокаДосталасьДругойИгреЗначокСменился() {
            OnUi(async () => {
                var net = ByUrl();
                ImageLoader.Http = net.Client();

                var rows = new ObservableCollection<Row> { new Row(Peak), new Row(Drive) };
                var list = Dock(rows);
                await Settle();

                var img = ImageOfRow(list, 0);
                Assert.Equal(Peak, CoverImage.GetUrl(img));

                // Значки различаются размером — проверяем показанную картинку, а не
                // строку адреса: адрес мог смениться и при мёртвой привязке Source.
                Assert.Equal(Side, ((BitmapSource)img.Source).PixelWidth);

                // Ровно то, что делает QueueDockLayout.ApplyVisible при смене порядка.
                rows[0] = new Row(Drive);
                list.UpdateLayout();
                await Settle();

                // Строка та же самая — WPF её переиспользовал, — а картинка другая.
                Assert.Same(img, ImageOfRow(list, 0));
                Assert.Equal(Drive, CoverImage.GetUrl(img));
                Assert.Equal(Side * 2, ((BitmapSource)img.Source).PixelWidth);
                Assert.Contains(Drive, net.Requested);
            });
        }

        /// <summary>
        /// Загрузка прошлой игры доехала, когда строка уже про другую: её результату
        /// здесь больше не место. Иначе значок «догонял» строку через секунду после того,
        /// как она стала чужой, — и в очереди снова стояла не та картинка.
        /// </summary>
        [Fact]
        public void ОпоздавшаяЗагрузкаНеЛожитсяВЧужуюСтроку() {
            OnUi(async () => {
                var slow = new TaskCompletionSource<bool>();
                ImageLoader.Http = new FakeImageHandler(async (req, _) => {
                    if ((req.RequestUri?.ToString() ?? string.Empty) == Peak) {
                        await slow.Task;
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new ByteArrayContent(Sized(req)),
                    };
                }).Client();

                var img = new Image { Width = 36, Height = 36 };
                CoverImage.SetUrl(img, Peak);
                await Settle();

                // Строку отдали другой игре, пока значок первой ещё качался.
                CoverImage.SetUrl(img, Drive);
                await Settle();
                var afterDrive = img.Source;
                Assert.NotNull(afterDrive);

                // Теперь доезжает опоздавшая: она обязана пройти мимо.
                slow.SetResult(true);
                await Settle();

                Assert.Same(afterDrive, img.Source);
            });
        }

        /// <summary>
        /// Отказ по опоздавшей загрузке тоже никого не гасит: неудача прошлой игры
        /// прятала бы уже показанный значок новой.
        /// </summary>
        [Fact]
        public void ОпоздавшийОтказНеГаситЧужуюСтроку() {
            OnUi(async () => {
                var img = new Image { Width = 36, Height = 36 };

                ImageLoader.Http = FakeImageHandler.Ok(Png(64, 64)).Client();
                CoverImage.SetUrl(img, Drive);
                await Settle();
                Assert.Equal(Visibility.Visible, img.Visibility);

                // Пришёл ответ по адресу, которого элемент уже не ждёт.
                ImageLoader.Http = FakeImageHandler.Broken().Client();
                await ImageLoader.LoadImageAsync(img, Peak);
                await Settle();

                Assert.Equal(Visibility.Visible, img.Visibility);
                Assert.NotNull(img.Source);
            });
        }

        /// <summary>
        /// Игра без значка не донашивает чужой: пустой адрес — это «картинки нет», а не
        /// «оставить что было».
        /// </summary>
        [Fact]
        public void ПустойАдресУбираетПрежнийЗначок() {
            OnUi(async () => {
                ImageLoader.Http = FakeImageHandler.Ok(Png(64, 64)).Client();
                var img = new Image { Width = 36, Height = 36 };

                CoverImage.SetUrl(img, Peak);
                await Settle();
                Assert.NotNull(img.Source);

                CoverImage.SetUrl(img, string.Empty);
                await Settle();

                Assert.Null(img.Source);
                Assert.Equal(Visibility.Collapsed, img.Visibility);
            });
        }

        /// <summary>
        /// Один и тот же адрес, поставленный заново, сеть не дёргает: строка очереди
        /// перерисовывается по четыре раза в секунду, пока идёт закачка.
        /// </summary>
        [Fact]
        public void ТотЖеАдресСетьНеДёргает() {
            OnUi(async () => {
                var net = FakeImageHandler.Ok(Png(64, 64));
                ImageLoader.Http = net.Client();
                var img = new Image { Width = 36, Height = 36 };

                CoverImage.SetUrl(img, Peak);
                await Settle();
                var calls = net.Calls;

                CoverImage.SetUrl(img, Peak);
                await Settle();

                Assert.Equal(calls, net.Calls);
            });
        }

        /// <summary>Свойство висит на картинке; на чужом элементе оно молчит, а не падает.</summary>
        [Fact]
        public void НаНеКартинкеСвойствоМолчит() {
            OnUi(() => {
                var border = new Border();
                CoverImage.SetUrl(border, Peak);

                Assert.Equal(Peak, CoverImage.GetUrl(border));
                return Task.CompletedTask;
            });
        }

        /// <summary>Одна строка очереди: то же, что в разметке дока.</summary>
        private sealed class Row {
            internal Row(string iconUrl) => this.IconUrl = iconUrl;

            public string IconUrl { get; }
        }

        /// <summary>Док очереди в миниатюре: тот же ItemsControl с тем же шаблоном строки.</summary>
        private static ItemsControl Dock(ObservableCollection<Row> rows) {
            var template = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:ui='clr-namespace:ChillHub.Core.UI;assembly=ChillHub'>" +
                "<Image Width='36' Height='36' ui:CoverImage.Url='{Binding IconUrl}'/>" +
                "</DataTemplate>");

            var list = new ItemsControl { ItemsSource = rows, ItemTemplate = template };
            var host = new Border { Child = list, Width = 200, Height = 200 };
            host.Measure(new Size(200, 200));
            host.Arrange(new Rect(0, 0, 200, 200));
            list.UpdateLayout();
            return list;
        }

        private static Image ImageOfRow(ItemsControl list, int index) {
            var container = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(index);
            container.ApplyTemplate();
            return Find(container) ?? throw new InvalidOperationException("в строке нет картинки");
        }

        private static Image? Find(DependencyObject root) {
            if (root is Image image) {
                return image;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++) {
                if (Find(VisualTreeHelper.GetChild(root, i)) is Image found) {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Каждому адресу — своя по размеру картинка, чтобы их было чем различать.</summary>
        private static FakeImageHandler ByUrl() => new((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new ByteArrayContent(Sized(req)),
            }));

        /// <summary>Квадратный значок для одной игры, вдвое более широкий — для другой.</summary>
        private static byte[] Sized(HttpRequestMessage req)
            => (req.RequestUri?.ToString() ?? string.Empty) == Peak ? Png(64, 64) : Png(128, 64);

        private static byte[] Png(int width, int height) {
            var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>Даёт диспетчеру доработать поставленное в очередь: загрузка идёт в фоне.</summary>
        private static async Task Settle() {
            for (var i = 0; i < 40; i++) {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Yield();
            }
        }

        private static void OnUi(Func<Task> body) => UiThread.Run(body);
    }
}
