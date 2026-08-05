// <copyright file="ImageLoaderVisualTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Threading;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Половина загрузчика, которая трогает интерфейс: показ картинки, скелетон-заглушка
    /// и поведение при негодном ответе.
    /// <para>
    /// Главный экран рисует чужой контент — обложки новостей и иконки игр приходят по сети
    /// и могут оказаться чем угодно. Ни битый адрес, ни страница ошибки вместо картинки не
    /// имеют права уронить страницу: пустое место вместо иконки — приемлемо, пустое окно — нет.
    /// </para>
    /// <para>
    /// Тесты идут на выделенном STA-потоке с работающим диспетчером: WPF-элементы вне такого
    /// потока не создаются, а <see cref="ImageLoader"/> возвращается в UI именно через диспетчер.
    /// Окна и Application не поднимаются — ни то, ни другое загрузчику не нужно.
    /// </para>
    /// </summary>
    [Collection(ImageLoaderCollection.Name)]
    public class ImageLoaderVisualTests : IDisposable {
        private const string Url = "https://images.invalid/icon.png";

        public ImageLoaderVisualTests() => ImageLoader.ResetForTests();

        public void Dispose() => ImageLoader.ResetForTests();

        /// <summary>
        /// Обычный успешный случай: байты превращаются в картинку, элемент становится видимым,
        /// готовая картинка ложится в кеш — иначе тот же значок в списке и в шапке качался бы дважды.
        /// </summary>
        [Fact]
        public void УспешнаяЗагрузкаПоказываетКартинкуИКладётЕёВКеш() {
            OnUi(async () => {
                var handler = FakeImageHandler.Ok(MakePng(64, 64));
                ImageLoader.Http = handler.Client();
                var img = new Image();

                await ImageLoader.LoadImageAsync(img, Url);

                Assert.NotNull(img.Source);
                Assert.Equal(Visibility.Visible, img.Visibility);
                Assert.True(ImageLoader.IsCached(Url));
                Assert.Equal(1, handler.Calls);
            });
        }

        /// <summary>
        /// Вторая картинка с тем же адресом берётся из кеша. Без этого прокрутка списка
        /// заново качала бы каждую иконку при каждом перестроении шаблона.
        /// </summary>
        [Fact]
        public void ПовторныйЗапросТогоЖеАдресаВСетьНеХодит() {
            OnUi(async () => {
                var handler = FakeImageHandler.Ok(MakePng(64, 64));
                ImageLoader.Http = handler.Client();

                await ImageLoader.LoadImageAsync(new Image(), Url);
                var second = new Image();
                await ImageLoader.LoadImageAsync(second, Url);

                Assert.Equal(1, handler.Calls);
                Assert.NotNull(second.Source);
                Assert.Equal(Visibility.Visible, second.Visibility);
            });
        }

        /// <summary>
        /// Пока элемент ждал байты, ту же картинку успел показать другой — значит она уже
        /// раскодирована. Раскодировать её второй раз незачем: это лишняя работа и вторая
        /// копия растра в памяти на каждый повтор иконки в списке.
        /// </summary>
        [Fact]
        public void ГотоваяКартинкаОтСоседаБерётсяБезПовторногоРаскодирования() {
            OnUi(async () => {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ImageLoader.Http = new FakeImageHandler(async (_, _) => {
                    await gate.Task;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                        Content = new ByteArrayContent(MakePng(64, 64)),
                    };
                }).Client();

                var waiting = new Image();
                var loading = ImageLoader.LoadImageAsync(waiting, Url);

                var neighbour = new Image();
                using (var ms = new MemoryStream(MakePng(64, 64))) {
                    ImageLoader.ApplyBitmap(neighbour, ms, Url);
                }

                gate.SetResult(true);
                await loading;

                Assert.Same(neighbour.Source, waiting.Source);
                Assert.Equal(Visibility.Visible, waiting.Visibility);
            });
        }

        /// <summary>
        /// Протокол-относительный адрес (<c>//host/path</c>) приходит из чужих CDN в новостях.
        /// Схему ему подставляет origin API: иначе такой адрес уедет в запрос как относительный
        /// путь и обложка потеряется.
        /// </summary>
        [Fact]
        public void ПротоколОтносительныйАдресПолучаетСхемуОтOrigin() {
            OnUi(async () => {
                var handler = FakeImageHandler.Ok(MakePng(64, 64));
                ImageLoader.Http = handler.Client();
                var img = new Image { Tag = "//cdn.invalid/cover.png" };

                ImageLoader.AttachAndLoad(img, "https://api.invalid/v1/");
                await Settle();

                Assert.Equal(new[] { "https://cdn.invalid/cover.png" }, handler.Requested);
            });
        }

        /// <summary>
        /// Вместо картинки пришла страница-заглушка от прокси. Раскодировать её нельзя,
        /// но исключение наружу — это падение главного экрана из-за одной иконки.
        /// </summary>
        [Fact]
        public void МусорВместоКартинкиПрячетЭлементИНеРоняетЭкран() {
            OnUi(async () => {
                ImageLoader.Http = FakeImageHandler.Ok(Encoding.UTF8.GetBytes("<html>404</html>")).Client();
                var img = new Image();

                await ImageLoader.LoadImageAsync(img, Url);

                Assert.Null(img.Source);
                Assert.Equal(Visibility.Collapsed, img.Visibility);
                Assert.False(ImageLoader.IsCached(Url));
            });
        }

        /// <summary>Недоступная сеть — тоже штатный случай: элемент прячется, ошибка наружу не идёт.</summary>
        [Fact]
        public void ОтказСетиПрячетЭлементБезИсключения() {
            OnUi(async () => {
                ImageLoader.Http = FakeImageHandler.Broken().Client();
                var img = new Image { Visibility = Visibility.Visible };

                await ImageLoader.LoadImageAsync(img, Url);

                Assert.Equal(Visibility.Collapsed, img.Visibility);
            });
        }

        /// <summary>Страница ошибки с кодом 404 не должна оказаться в кеше вместо иконки.</summary>
        [Fact]
        public void ОтветЧетырестаЧетыреНеПопадаетВКеш() {
            OnUi(async () => {
                ImageLoader.Http = FakeImageHandler.Status(System.Net.HttpStatusCode.NotFound).Client();
                var img = new Image();

                await ImageLoader.LoadImageAsync(img, Url);

                Assert.False(ImageLoader.IsCached(Url));
                Assert.Equal(Visibility.Collapsed, img.Visibility);
            });
        }

        /// <summary>
        /// Картинка раскодируется под размер элемента, а не в исходном разрешении.
        /// Обложка новости приходит шириной в пару тысяч точек, а показывается высотой 88:
        /// без этого лента из десятка новостей держала бы в памяти десятки мегабайт растра.
        /// </summary>
        [Fact]
        public void КартинкаРаскодируетсяПодРазмерЭлемента() {
            OnUi(() => {
                var img = new Image { Height = 40 };
                using var ms = new MemoryStream(MakePng(400, 400));

                ImageLoader.ApplyBitmap(img, ms, Url);

                var bi = Assert.IsType<BitmapImage>(img.Source);
                Assert.Equal(40, bi.PixelHeight);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// У элемента без заданной высоты берётся высота по умолчанию: нулевая привела бы
        /// к раскодированию в исходном разрешении, то есть ровно к той трате памяти,
        /// от которой уменьшение и заведено.
        /// </summary>
        [Fact]
        public void ЭлементБезВысотыРаскодируетсяВВысотуПоУмолчанию() {
            OnUi(() => {
                var img = new Image();
                using var ms = new MemoryStream(MakePng(400, 400));

                ImageLoader.ApplyBitmap(img, ms, Url);

                var bi = Assert.IsType<BitmapImage>(img.Source);
                Assert.Equal(88, bi.PixelHeight);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Высота у элемента задана не всегда — в списке её диктует разметка контейнера.
        /// Тогда берётся измеренный размер: иначе растянутая на всю карточку обложка
        /// раскодировалась бы в 88 точек и выглядела бы мыльной.
        /// </summary>
        [Fact]
        public void БезЯвнойВысотыБерётсяИзмеренныйРазмер() {
            OnUi(() => {
                var img = new Image { Stretch = Stretch.Uniform, Source = MakeBitmap(200, 200) };
                img.Measure(new Size(50, 50));
                Assert.True(img.DesiredSize.Height > 0, "элемент не измерился — проверять нечего");

                using var ms = new MemoryStream(MakePng(400, 400));
                ImageLoader.ApplyBitmap(img, ms, Url);

                var bi = Assert.IsType<BitmapImage>(img.Source);
                Assert.Equal((int)Math.Round(img.DesiredSize.Height), bi.PixelHeight);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Окно закрыли, пока грузились обложки: диспетчер уже мёртв, и спрятать картинку
        /// негде. Раньше такая попытка вылетала необработанной из фоновой задачи — то есть
        /// закрытие лаунчера с открытым главным экраном заканчивалось падением процесса.
        /// </summary>
        [Fact]
        public async Task ЗакрытыйДиспетчерНеПревращаетОтказЗагрузкиВПадение() {
            Image? img = null;
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();

            var thread = new Thread(() => {
                dispatcher = Dispatcher.CurrentDispatcher;
                img = new Image();
                ready.Set();
                Dispatcher.Run();
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "поток интерфейса не поднялся");
            dispatcher!.InvokeShutdown();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "поток интерфейса не завершился");

            ImageLoader.Http = FakeImageHandler.Broken().Client();

            await ImageLoader.LoadImageAsync(img!, Url);
        }

        /// <summary>Готовая картинка обязана быть заморожена — иначе её нельзя отдать другому потоку.</summary>
        [Fact]
        public void ГотоваяКартинкаЗаморожена() {
            OnUi(() => {
                var img = new Image();
                using var ms = new MemoryStream(MakePng(64, 64));

                ImageLoader.ApplyBitmap(img, ms, Url);

                Assert.True(((BitmapImage)img.Source).IsFrozen);
                return Task.CompletedTask;
            });
        }

        /// <summary>Скелетон ищется среди соседей по имени — так он объявлен в разметке.</summary>
        [Fact]
        public void СкелетонНаходитсяСредиСоседей() {
            OnUi(() => {
                var panel = new Grid();
                var skeleton = new Border { Name = "ImgSkeleton" };
                panel.Children.Add(new Border { Name = "Other" });
                panel.Children.Add(skeleton);

                Assert.Same(skeleton, ImageLoader.FindImgSkeleton(panel));
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Родителя может не быть вовсе: элемент вырван из шаблона или ещё не встроен в дерево.
        /// Поиск обязан вернуть «не нашёл», а не упасть на середине отрисовки списка.
        /// </summary>
        [Fact]
        public void ПоискСкелетонаБезРодителяНичегоНеНаходит() {
            OnUi(() => {
                Assert.Null(ImageLoader.FindImgSkeleton(null));
                Assert.Null(ImageLoader.FindImgSkeleton(new Grid()));
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// В качестве родителя может прийти узел не из визуального дерева — например, кусок
        /// оформленного текста. Спрашивать у такого детей нельзя, и падение поиска утащило бы
        /// за собой обработчик Loaded целой карточки.
        /// </summary>
        [Fact]
        public void УзелВнеВизуальногоДереваНеРоняетПоиск() {
            OnUi(() => {
                Assert.Null(ImageLoader.FindImgSkeleton(new System.Windows.Documents.Run("текст")));
                return Task.CompletedTask;
            });
        }

        /// <summary>Соседний Border с другим именем — не скелетон: прятать чужую рамку нельзя.</summary>
        [Fact]
        public void ЧужаяРамкаЗаСкелетонНеСчитается() {
            OnUi(() => {
                var panel = new Grid();
                panel.Children.Add(new Border { Name = "Divider" });

                Assert.Null(ImageLoader.FindImgSkeleton(panel));
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Скелетон прячется, когда судьба картинки решена. Оставить его — значит навсегда
        /// показать пользователю «идёт загрузка» на месте, где загрузка давно закончилась.
        /// </summary>
        [Fact]
        public void СкелетонПрячетсяРядомСКартинкой() {
            OnUi(() => {
                var panel = new Grid();
                var img = new Image();
                var skeleton = new Border { Name = "ImgSkeleton" };
                panel.Children.Add(img);
                panel.Children.Add(skeleton);

                ImageLoader.HideSkeleton(img);

                Assert.Equal(Visibility.Collapsed, skeleton.Visibility);
                return Task.CompletedTask;
            });
        }

        /// <summary>Картинки без скелетона рядом — обычное дело, и это не повод падать.</summary>
        [Fact]
        public void СкрытиеОтсутствующегоСкелетонаНичегоНеЛомает() {
            OnUi(() => {
                var panel = new Grid();
                var img = new Image();
                panel.Children.Add(img);

                ImageLoader.HideSkeleton(img);
                ImageLoader.HideSkeleton(new Image());
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Адрес в Tag — основной путь: именно так разметка передаёт загрузчику URL иконки.
        /// Относительный адрес обязан привязаться к origin API, иначе запрос уйдёт в никуда.
        /// </summary>
        [Fact]
        public void АдресИзTagУходитВЗагрузкуОтносительноOrigin() {
            OnUi(async () => {
                var handler = FakeImageHandler.Ok(MakePng(64, 64));
                ImageLoader.Http = handler.Client();
                var img = new Image { Tag = "/manifests/game/icon.png" };

                ImageLoader.AttachAndLoad(img, "https://api.invalid/v1/");
                await Settle();

                Assert.Equal(new[] { "https://api.invalid/manifests/game/icon.png" }, handler.Requested);
            });
        }

        /// <summary>
        /// Когда Tag пуст, адрес берётся из модели: в шаблонах списка Tag теряется при
        /// перестроении контейнера, и без этого запасного пути иконки пропадали при прокрутке.
        /// </summary>
        [Fact]
        public void ПриПустомTagАдресБерётсяИзМодели() {
            OnUi(async () => {
                var handler = FakeImageHandler.Ok(MakePng(64, 64));
                ImageLoader.Http = handler.Client();
                var img = new Image { DataContext = new NewsItem { CoverUrl = "https://cdn.invalid/cover.png" } };

                ImageLoader.AttachAndLoad(img, "https://api.invalid/v1/");
                await Settle();

                Assert.Equal(new[] { "https://cdn.invalid/cover.png" }, handler.Requested);
            });
        }

        /// <summary>
        /// Адреса нет нигде — показывать нечего. Элемент прячется и скелетон снимается:
        /// иначе на месте отсутствующей иконки навсегда останется мерцающая заглушка.
        /// </summary>
        [Fact]
        public void БезАдресаЭлементПрячетсяВместеСоСкелетоном() {
            OnUi(() => {
                var handler = FakeImageHandler.Ok(Array.Empty<byte>());
                ImageLoader.Http = handler.Client();
                var panel = new Grid();
                var img = new Image { Tag = "   " };
                var skeleton = new Border { Name = "ImgSkeleton" };
                panel.Children.Add(img);
                panel.Children.Add(skeleton);

                ImageLoader.AttachAndLoad(img, "https://api.invalid/v1/");

                Assert.Equal(Visibility.Collapsed, img.Visibility);
                Assert.Equal(Visibility.Collapsed, skeleton.Visibility);
                Assert.Equal(0, handler.Calls);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Битый адрес сервера в config.json ломает разбор URL. Это правится пользователем
        /// вручную, то есть встречается в жизни — и не имеет права уронить главный экран.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("не адрес")]
        [InlineData("/relative/only")]
        public void НеразбираемыйАдресСервераПрячетКартинкуБезПадения(string baseApi) {
            OnUi(() => {
                var handler = FakeImageHandler.Ok(Array.Empty<byte>());
                ImageLoader.Http = handler.Client();
                var img = new Image { Tag = "/icon.png" };

                ImageLoader.AttachAndLoad(img, baseApi);

                Assert.Equal(Visibility.Collapsed, img.Visibility);
                Assert.Equal(0, handler.Calls);
                return Task.CompletedTask;
            });
        }

        /// <summary>Обработчик Loaded вызывается и для уже отвязанных элементов — null не должен падать.</summary>
        [Fact]
        public void ЗагрузкаБезЭлементаНичегоНеДелает() {
            OnUi(() => {
                ImageLoader.AttachAndLoad(null!, "https://api.invalid/v1/");
                ImageLoader.HandleImageFailed(null!, new InvalidOperationException("нет элемента"));
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Элемент, у которого уже стоит картинка с тем же адресом, перезагружать незачем:
        /// повторный Loaded (а он приходит при каждом возврате на страницу) иначе означал бы
        /// новый запрос за каждой видимой иконкой.
        /// </summary>
        [Fact]
        public void УжеПоказаннаяКартинкаНеПерезагружается() {
            OnUi(() => {
                using var dir = new TempDir();
                var file = dir.WriteBytes("icon.png", MakePng(16, 16));
                var uri = new Uri("file:///" + file.Replace('\\', '/'));
                var handler = FakeImageHandler.Ok(Array.Empty<byte>());
                ImageLoader.Http = handler.Client();

                var img = new Image { Source = FromUri(uri), Visibility = Visibility.Collapsed };
                ImageLoader.AttachAndLoad(img, "https://api.invalid/v1/");

                Assert.Equal(Visibility.Visible, img.Visibility);
                Assert.Equal(0, handler.Calls);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Последний запасной путь: адрес вытаскивается из уже назначенного Source.
        /// Разметка привязывает Source напрямую к модели, и для таких элементов Tag пуст.
        /// </summary>
        [Fact]
        public void АдресВытаскиваетсяИзНазначенногоИсточника() {
            OnUi(() => {
                using var dir = new TempDir();
                var file = dir.WriteBytes("icon.png", MakePng(16, 16));
                var uri = new Uri("file:///" + file.Replace('\\', '/'));

                Assert.Equal(uri.OriginalString, ImageLoader.ExtractUrlFromSource(FromUri(uri)));
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Источника нет или он не картинка из сети (кисть, рисунок, растр из потока) —
        /// адреса взять неоткуда, и выдумывать его нельзя.
        /// </summary>
        [Fact]
        public void ИсточникБезАдресаНичегоНеДаёт() {
            OnUi(() => {
                Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromSource(null));
                Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromSource(new DrawingImage()));
                Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromSource(new BitmapImage()));
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// ImageFailed приходит, когда WPF сам не смог показать источник. Тогда прячется и
        /// картинка, и скелетон: иначе на месте несостоявшейся обложки останется вечная заглушка.
        /// </summary>
        [Fact]
        public void СбойПоказаПрячетКартинкуИСкелетон() {
            OnUi(() => {
                var panel = new Grid();
                var img = new Image { Visibility = Visibility.Visible };
                var skeleton = new Border { Name = "ImgSkeleton" };
                panel.Children.Add(img);
                panel.Children.Add(skeleton);

                ImageLoader.HandleImageFailed(img, new InvalidOperationException("формат не поддержан"));

                Assert.Equal(Visibility.Collapsed, img.Visibility);
                Assert.Equal(Visibility.Collapsed, skeleton.Visibility);
                return Task.CompletedTask;
            });
        }

        /// <summary>Причина сбоя может быть неизвестна — сообщение без деталей тоже не должно падать.</summary>
        [Fact]
        public void СбойПоказаБезПричиныОбрабатывается() {
            OnUi(() => {
                var img = new Image();
                ImageLoader.HandleImageFailed(img, null);

                Assert.Equal(Visibility.Collapsed, img.Visibility);
                return Task.CompletedTask;
            });
        }

        /// <summary>Замороженная картинка из файла — понадобится, чтобы у Source был адрес.</summary>
        private static BitmapImage FromUri(Uri uri) {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = uri;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        /// <summary>Растр заданного размера — когда элементу нужна любая картинка, лишь бы измерился.</summary>
        private static BitmapSource MakeBitmap(int width, int height)
            => new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        /// <summary>Настоящий PNG заданного размера: раскодирование проверяется без файлов-образцов.</summary>
        private static byte[] MakePng(int width, int height) {
            var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Даёт диспетчеру доработать то, что было поставлено в очередь.
        /// AttachAndLoad запускает загрузку «в фоне», и без этого проверять было бы нечего.
        /// </summary>
        private static async Task Settle() {
            for (var i = 0; i < 20; i++) {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Yield();
            }
        }

        /// <summary>
        /// Прогоняет тело теста на отдельном STA-потоке с работающим диспетчером.
        /// WPF-элементы создаются только там, а <see cref="ImageLoader"/> возвращает результат
        /// в интерфейс через <c>Dispatcher.InvokeAsync</c> — без запущенного цикла тест бы завис.
        /// </summary>
        private static void OnUi(Func<Task> body) {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() => {
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = dispatcher.InvokeAsync(async () => {
                    try {
                        await body();
                    }
                    catch (Exception ex) {
                        failure = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally {
                        dispatcher.InvokeShutdown();
                    }
                });

                Dispatcher.Run();
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "тест интерфейса не уложился в отведённое время");
            failure?.Throw();
        }
    }
}
