// <copyright file="ImageDownloadTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Сетевая половина загрузчика картинок: одна загрузка на адрес и поведение при отказе.
    /// <para>
    /// Главный экран рисует десятки картинок сразу, и одна и та же иконка попадает в список,
    /// в шапку и в новости. Если каждый элемент полезет за ней сам — это лишние запросы к
    /// серверу на ровном месте; если, наоборот, лишние элементы просто выйдут из метода —
    /// вместо иконок останутся дырки. Проверяется именно эта середина.
    /// </para>
    /// </summary>
    [Collection(ImageLoaderCollection.Name)]
    public class ImageDownloadTests : IDisposable {
        private const string Url = "https://images.invalid/icon.png";
        private static readonly byte[] Payload = { 1, 2, 3, 4, 5 };

        public ImageDownloadTests() => ImageLoader.ResetForTests();

        public void Dispose() => ImageLoader.ResetForTests();

        /// <summary>
        /// Два элемента, попросившие одну картинку одновременно, обязаны обойтись ОДНИМ
        /// запросом и оба получить результат. Раньше второй просто выходил из метода —
        /// повторная иконка (та же игра в списке и в шапке) оставалась пустой.
        /// </summary>
        [Fact]
        public async Task ОдновременныеЗапросыОдногоАдресаДаютОднуЗагрузку() {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeImageHandler(async (_, _) => {
                await gate.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            });
            ImageLoader.Http = handler.Client();

            var first = ImageLoader.FetchBytesAsync(Url);
            var second = ImageLoader.FetchBytesAsync(Url);
            gate.SetResult(true);

            Assert.Equal(Payload, await first);
            Assert.Equal(Payload, await second);
            Assert.Equal(1, handler.Calls);
        }

        /// <summary>Разные адреса — разные картинки: склеивать их дедупликация не имеет права.</summary>
        [Fact]
        public async Task РазныеАдресаКачаютсяПоОтдельности() {
            var handler = FakeImageHandler.Ok(Payload);
            ImageLoader.Http = handler.Client();

            await Task.WhenAll(
                ImageLoader.FetchBytesAsync("https://images.invalid/a.png"),
                ImageLoader.FetchBytesAsync("https://images.invalid/b.png"));

            Assert.Equal(2, handler.Calls);
        }

        /// <summary>
        /// Регистр в адресе картинки роли не играет: один и тот же файл приходит из манифеста
        /// и из новости в разном написании, и качать его дважды незачем.
        /// </summary>
        [Fact]
        public async Task РегистрАдресаНеПлодитВторуюЗагрузку() {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeImageHandler(async (_, _) => {
                await gate.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            });
            ImageLoader.Http = handler.Client();

            var first = ImageLoader.FetchBytesAsync("https://images.invalid/Icon.PNG");
            var second = ImageLoader.FetchBytesAsync("https://images.invalid/icon.png");
            gate.SetResult(true);

            await Task.WhenAll(first, second);
            Assert.Equal(1, handler.Calls);
        }

        /// <summary>
        /// Неудачная загрузка не должна залипать в списке идущих: иначе одна потерянная
        /// картинка становится вечной — второй заход отдал бы ту же ошибку, не сходив в сеть,
        /// и элемент не восстановился бы даже после возврата сети.
        /// </summary>
        [Fact]
        public async Task ПровалЗагрузкиНеЗапрещаетПовторнуюПопытку() {
            var handler = FakeImageHandler.Broken();
            ImageLoader.Http = handler.Client();

            await Assert.ThrowsAsync<HttpRequestException>(() => ImageLoader.FetchBytesAsync(Url));
            await Assert.ThrowsAsync<HttpRequestException>(() => ImageLoader.FetchBytesAsync(Url));

            Assert.Equal(2, handler.Calls);
        }

        /// <summary>
        /// Ответ с кодом ошибки — не картинка. Тело у 404 обычно есть (страница ошибки),
        /// и без проверки кода в кеш легла бы HTML-страница вместо иконки.
        /// </summary>
        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        public async Task ОтветНеУспехаСчитаетсяОтказом(HttpStatusCode code) {
            var handler = FakeImageHandler.Status(code);
            ImageLoader.Http = handler.Client();

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ImageLoader.FetchBytesAsync(Url));
            Assert.Contains(((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Отмена (окно закрыли, пока грузились обложки) обязана дойти до вызывающего как
        /// отмена, а не зависнуть: ждущий элемент иначе держит задачу до конца процесса.
        /// </summary>
        [Fact]
        public async Task ОтменаЗапросаНеЗависает() {
            var handler = new FakeImageHandler(async (_, token) => {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("сюда не доходим");
            });
            var client = handler.Client();
            client.Timeout = TimeSpan.FromMilliseconds(150);
            ImageLoader.Http = client;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageLoader.FetchBytesAsync(Url));
        }

        /// <summary>
        /// Отменённая загрузка тоже обязана уйти из списка идущих: повисший ключ навсегда
        /// закрывает адрес — все следующие элементы получали бы ту же отмену без запроса.
        /// </summary>
        [Fact]
        public async Task ОтменённаяЗагрузкаНеЗакрываетАдресНавсегда() {
            var slow = new FakeImageHandler(async (_, token) => {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("сюда не доходим");
            });
            var client = slow.Client();
            client.Timeout = TimeSpan.FromMilliseconds(150);
            ImageLoader.Http = client;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageLoader.FetchBytesAsync(Url));

            var good = FakeImageHandler.Ok(Payload);
            ImageLoader.Http = good.Client();

            Assert.Equal(Payload, await ImageLoader.FetchBytesAsync(Url));
        }

        /// <summary>
        /// Мусор вместо картинки — обычное дело: прокси отдаёт страницу-заглушку с кодом 200.
        /// Загрузчик обязан донести байты как есть, а разбираться с форматом — уже при показе.
        /// </summary>
        [Fact]
        public async Task МусорСКодомДвестиДоходитДоРазбораКакЕсть() {
            var junk = System.Text.Encoding.UTF8.GetBytes("<html>not an image</html>");
            var handler = FakeImageHandler.Ok(junk);
            ImageLoader.Http = handler.Client();

            Assert.Equal(junk, await ImageLoader.FetchBytesAsync(Url));
        }

        /// <summary>Пустой ответ — не исключение: элемент просто останется без картинки.</summary>
        [Fact]
        public async Task ПустойОтветНеПадает() {
            ImageLoader.Http = FakeImageHandler.Ok(Array.Empty<byte>()).Client();

            Assert.Empty(await ImageLoader.FetchBytesAsync(Url));
        }

        /// <summary>
        /// Сброс клиента возвращает загрузчику рабочий HttpClient. Тесты подменяют его на
        /// подставной, и если бы сброс оставлял null, следующий тест ронял бы загрузку.
        /// </summary>
        [Fact]
        public void СбросВозвращаетРабочийКлиент() {
            ImageLoader.Http = FakeImageHandler.Ok(Payload).Client();
            ImageLoader.ResetForTests();

            Assert.NotNull(ImageLoader.Http);
        }

        /// <summary>Пустой клиент подменить нельзя — загрузчик обязан остаться работоспособным.</summary>
        [Fact]
        public void ОтсутствующийКлиентЗаменяетсяКлиентомПоУмолчанию() {
            ImageLoader.Http = null!;

            Assert.NotNull(ImageLoader.Http);
        }

        /// <summary>Пока картинку не показали, кеш пуст — в него кладут только раскодированное.</summary>
        [Fact]
        public async Task СкачанныеБайтыСамиПоСебеВКешНеПопадают() {
            ImageLoader.Http = FakeImageHandler.Ok(Payload).Client();

            await ImageLoader.FetchBytesAsync(Url);

            Assert.False(ImageLoader.IsCached(Url));
        }

        /// <summary>Кеш и список загрузок общие на приложение: одновременный доступ не должен их рвать.</summary>
        [Fact]
        public async Task ДесяткиЭлементовЗаОднойКартинкойНеРоняютЗагрузчик() {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeImageHandler(async (_, _) => {
                await gate.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            });
            ImageLoader.Http = handler.Client();

            var waiters = new Task<byte[]>[32];
            for (var i = 0; i < waiters.Length; i++) {
                waiters[i] = Task.Run(() => ImageLoader.FetchBytesAsync(Url));
            }

            gate.SetResult(true);
            var results = await Task.WhenAll(waiters);

            Assert.All(results, r => Assert.Equal(Payload, r));
            Assert.InRange(handler.Calls, 1, waiters.Length);
        }
    }

    /// <summary>
    /// Откуда берётся адрес картинки, когда его не положили в Tag.
    /// <para>
    /// Разметка привязывает Tag не везде: в шаблонах он теряется при перестроении, а в
    /// новостях элемент приходит уже с назначенным Source. Пока разбор источника жил внутри
    /// UI-кода, любая посторонняя модель в DataContext (заглушка «нет игр», строка-разделитель)
    /// уходила в загрузчик как адрес.
    /// </para>
    /// </summary>
    public class ImageUrlSourceTests {
        /// <summary>У карточки игры адрес лежит в IconUrl — другого поля с картинкой у неё нет.</summary>
        [Fact]
        public void АдресКарточкиИгрыБерётсяИзIconUrl() {
            var game = new GameInfo { IconUrl = "/manifests/game/icon.png" };

            Assert.Equal("/manifests/game/icon.png", ImageLoader.ExtractUrlFromDataContext(game));
        }

        /// <summary>
        /// У новости поле называется иначе — CoverUrl. Перепутанное имя не даёт ни ошибки,
        /// ни картинки: обложки просто молча пропадают со всей ленты.
        /// </summary>
        [Fact]
        public void АдресНовостиБерётсяИзCoverUrl() {
            var news = new NewsItem { CoverUrl = "https://cdn.invalid/cover.jpg" };

            Assert.Equal("https://cdn.invalid/cover.jpg", ImageLoader.ExtractUrlFromDataContext(news));
        }

        /// <summary>
        /// Посторонняя модель в DataContext не адрес. Отдать её строкой — значит увести
        /// загрузчик в запрос по мусорному пути на каждой перерисовке списка.
        /// </summary>
        [Fact]
        public void ПостороннийОбъектАдресаНеДаёт() {
            Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromDataContext(new object()));
            Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromDataContext("https://images.invalid/a.png"));
            Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromDataContext(42));
        }

        /// <summary>Пустой DataContext — обычное состояние ещё не привязанного элемента.</summary>
        [Fact]
        public void ПустойDataContextАдресаНеДаёт() {
            Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromDataContext(null));
        }

        /// <summary>
        /// Игра без иконки и новость без обложки — штатный случай: сервер отдаёт пустое поле.
        /// Ответ обязан быть пустым, иначе загрузчик пойдёт за адресом origin целиком.
        /// </summary>
        [Fact]
        public void МодельБезКартинкиДаётПустойАдрес() {
            Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromDataContext(new GameInfo()));
            Assert.Equal(string.Empty, ImageLoader.ExtractUrlFromDataContext(new NewsItem()));
        }
    }
}
