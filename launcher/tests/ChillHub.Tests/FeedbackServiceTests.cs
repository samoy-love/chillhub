// <copyright file="FeedbackServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Обратная связь: отправка на сервер и оффлайн-очередь.
    /// <para>
    /// Очередь — единственное место, где лаунчер хранит НАПИСАННЫЙ ПОЛЬЗОВАТЕЛЕМ текст.
    /// Потерять сообщение здесь — значит потерять его насовсем: формы уже нет, а человек
    /// уверен, что отправил. Поэтому проверяется, что сообщение уходит из очереди ровно
    /// тогда, когда сервер его принял, и остаётся в ней во всех остальных случаях.
    /// </para>
    /// <para>
    /// Каждый тест, который доводит сообщение до доставки, уводит очередь на временный файл
    /// (<see cref="TempQueue"/>): после успешной отправки <c>FlushNowAsync</c> сохраняет
    /// очередь на диск, и без подмены пути прогон затирал бы настоящий
    /// %APPDATA%\ChillHub\feedback_queue.json с неотправленными сообщениями разработчика.
    /// </para>
    /// </summary>
    public class FeedbackServiceTests {
        /// <summary>Принятое сервером сообщение считается доставленным.</summary>
        [Fact]
        public async Task УспешныйОтветСчитаетсяДоставкой() {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var svc = NewService(handler, out _, out _);

            Assert.True(await svc.TrySendAsync(Draft(), silent: true));
            Assert.Single(handler.Requests);
        }

        /// <summary>
        /// Отказ сервера — это НЕ доставка. Иначе сообщение вычёркивается из очереди,
        /// а до адресата не доходит.
        /// </summary>
        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        public async Task ОтказСервераНеСчитаетсяДоставкой(HttpStatusCode code) {
            var handler = new FakeHandler(_ => new HttpResponseMessage(code));
            var svc = NewService(handler, out _, out _);

            Assert.False(await svc.TrySendAsync(Draft(), silent: true));
        }

        /// <summary>Обрыв сети — тоже не доставка: сообщение обязано дождаться следующей попытки.</summary>
        [Fact]
        public async Task ОбрывСетиНеСчитаетсяДоставкой() {
            var handler = new FakeHandler(_ => throw new HttpRequestException("сеть недоступна"));
            var svc = NewService(handler, out _, out _);

            Assert.False(await svc.TrySendAsync(Draft(), silent: true));
        }

        /// <summary>Сообщение уходит на /feedback/submit, а хвостовой слеш адреса не двоится.</summary>
        [Fact]
        public async Task АдресСобираетсяБезДвойногоСлеша() {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var svc = new FeedbackService(new HttpClient(handler), () => "https://example.test/", _ => { }, _ => { });

            await svc.TrySendAsync(Draft(), silent: true);

            Assert.Equal("https://example.test/feedback/submit", handler.Requests[0].ToString());
        }

        /// <summary>
        /// Фоновый ретрай НЕ тратит квоту ручных отправок. Иначе при лежащем сервере
        /// очередь за полминуты выжигала все пять попыток, и живой человек получал отказ
        /// ровно тогда, когда обратная связь нужнее всего.
        /// </summary>
        [Fact]
        public async Task ФоновыйРетрайНеТратитКвотуРучныхОтправок() {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var svc = NewService(handler, out var toasts, out _);

            for (var i = 0; i < 20; i++) {
                Assert.True(await svc.TrySendAsync(Draft(), silent: true));
            }

            Assert.Equal(20, handler.Requests.Count);
            Assert.DoesNotContain(toasts, t => t.Contains("Лимит", StringComparison.Ordinal));
        }

        /// <summary>
        /// Разбор очереди отправляет не больше пяти сообщений за проход: иначе накопленная
        /// за неделю оффлайна очередь уходит на сервер одним залпом.
        /// </summary>
        [Fact]
        public async Task ЗаОдинПроходУходитНеБольшеПяти() {
            using var queue = TempQueue();
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var svc = NewService(handler, out _, out _);
            SetQueue(svc, Enumerable.Range(0, 9).Select(i => Draft("сообщение " + i)));

            await svc.FlushNowAsync();

            Assert.Equal(5, handler.Requests.Count);
            Assert.Equal(4, svc.PendingCount);
        }

        /// <summary>Доставленные сообщения уходят из очереди, остальные остаются.</summary>
        [Fact]
        public async Task ДоставленноеУходитИзОчереди() {
            using var queue = TempQueue();
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var svc = NewService(handler, out _, out _);
            SetQueue(svc, new[] { Draft("первое"), Draft("второе") });

            await svc.FlushNowAsync();

            Assert.Equal(0, svc.PendingCount);
        }

        /// <summary>
        /// Недоставленное остаётся в очереди — это и есть весь смысл оффлайн-очереди.
        /// Перед сдачей делается одна повторная попытка, поэтому запросов вдвое больше.
        /// </summary>
        [Fact]
        public async Task НедоставленноеОстаётсяВОчереди() {
            using var queue = TempQueue();
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var svc = NewService(handler, out _, out _);
            SetQueue(svc, new[] { Draft("не уйдёт") });

            await svc.FlushNowAsync();

            Assert.Equal(1, svc.PendingCount);
            Assert.Equal(2, handler.Requests.Count);
        }

        /// <summary>Пустую очередь разбирать нечего — в сеть не ходим вовсе.</summary>
        [Fact]
        public async Task ПустаяОчередьНеХодитВСеть() {
            using var queue = TempQueue();
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var svc = NewService(handler, out _, out _);

            await svc.FlushNowAsync();

            Assert.Empty(handler.Requests);
        }

        /// <summary>
        /// Два прохода одновременно не накладываются.
        /// <para>
        /// Таймер тикает раз в 10 секунд, а таймаут HTTP — 100 секунд: при недоступном
        /// сервере проходы наезжали друг на друга, отправляли одни и те же сообщения по
        /// второму разу и вперемешку двигали индексы очереди при удалении.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПроходыПоОчередиНеНакладываются() {
            using var queue = TempQueue();
            var gate = new SemaphoreSlim(0, 1);
            var handler = new FakeHandler(_ => {
                gate.Wait();
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var svc = NewService(handler, out _, out _);
            SetQueue(svc, new[] { Draft("одно") });

            var first = Task.Run(() => svc.FlushNowAsync());

            // Ждём, пока первый проход упрётся в удерживаемый ответ сервера.
            while (handler.Requests.Count == 0) {
                await Task.Delay(5);
            }

            await svc.FlushNowAsync();          // второй проход обязан выйти сразу
            Assert.Single(handler.Requests);

            gate.Release();
            await first;
            Assert.Equal(0, svc.PendingCount);
        }

        /// <summary>Локальная разработка: недоступный API повторяется на порт админки.</summary>
        [Theory]
        [InlineData("http://localhost:8080", "http://localhost:55777/feedback/submit")]
        [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:55777/feedback/submit")]
        [InlineData("http://LOCALHOST:8080", "http://localhost:55777/feedback/submit")]
        public void ЛокальныйАдресДаётЗапаснойПортАдминки(string baseApi, string expected) {
            Assert.True(FeedbackService.TryBuildLocalAdminUrl(baseApi, out var adminUrl));
            Assert.Equal(expected, adminUrl);
        }

        /// <summary>
        /// Для прода запасного порта нет. Иначе сообщение пользователя ушло бы
        /// на посторонний порт публичного хоста.
        /// </summary>
        [Theory]
        [InlineData("https://launcher.samoy.love")]
        [InlineData("http://attacker.invalid")]
        [InlineData("не адрес вовсе")]
        [InlineData("")]
        public void ДляНеЛокальногоАдресаЗапасногоПортаНет(string baseApi) {
            Assert.False(FeedbackService.TryBuildLocalAdminUrl(baseApi, out var adminUrl));
            Assert.Equal(string.Empty, adminUrl);
        }

        /// <summary>Отказ API на localhost добирается до админки и засчитывается как доставка.</summary>
        [Fact]
        public async Task ОтказАпиНаLocalhostУходитВАдминку() {
            var handler = new FakeHandler(req =>
                new HttpResponseMessage(req.RequestUri!.Port == 55777 ? HttpStatusCode.OK : HttpStatusCode.NotFound));
            var svc = new FeedbackService(new HttpClient(handler), () => "http://localhost:8080", _ => { }, _ => { });

            Assert.True(await svc.TrySendAsync(Draft(), silent: true));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(55777, handler.Requests[1].Port);
        }

        /// <summary>Диагностика в сообщение о системе не тащит имя машины: оно часто содержит имя владельца.</summary>
        [Fact]
        public void СведенияОСистемеНеСодержатИмениМашины() {
            var info = FeedbackService.CollectSystemInfo();

            Assert.DoesNotContain("machineName", info.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                info.Values,
                v => v.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
            Assert.True(info.ContainsKey("os"));
            Assert.True(info.ContainsKey("appVersion"));
        }

        /// <summary>Очередь файла на диске не касается: путь обязан лежать в роуминге, а не в каталоге установки.</summary>
        [Fact]
        public void ОчередьЛежитВРоуминге() {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            Assert.StartsWith(roaming, FeedbackService.QueuePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("feedback_queue.json", System.IO.Path.GetFileName(FeedbackService.QueuePath));
        }

        /// <summary>
        /// Уводит очередь на временный файл. Обязателен в каждом тесте, который доводит
        /// сообщение до доставки: после успешной отправки <c>FlushNowAsync</c> сохраняет
        /// очередь на диск, и без подмены пути прогон затирал бы настоящий
        /// %APPDATA%\ChillHub\feedback_queue.json с неотправленными сообщениями разработчика.
        /// </summary>
        private static QueueSeam TempQueue() => new QueueSeam();

        private static FeedbackService NewService(
            FakeHandler handler, out List<string> toasts, out List<string> statuses) {
            var t = new List<string>();
            var s = new List<string>();
            toasts = t;
            statuses = s;
            return new FeedbackService(
                new HttpClient(handler), () => "https://example.test", msg => t.Add(msg), msg => s.Add(msg));
        }

        private static FeedbackService.FeedbackDraft Draft(string comment = "всё сломалось")
            => new FeedbackService.FeedbackDraft("Аноним", string.Empty, "bug", comment, false, null);

        /// <summary>
        /// Кладёт сообщения прямо в поле очереди, минуя Enqueue: тот сохраняет очередь
        /// в настоящий %APPDATA% и затёр бы неотправленные сообщения разработчика.
        /// </summary>
        private static void SetQueue(FeedbackService svc, IEnumerable<FeedbackService.FeedbackDraft> drafts) {
            var field = typeof(FeedbackService).GetField(
                "queue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            field.SetValue(svc, new List<FeedbackService.FeedbackDraft>(drafts));
        }

        /// <summary>Временный файл очереди на время одного теста.</summary>
        private sealed class QueueSeam : IDisposable {
            private readonly TempDir dir = new TempDir();
            private readonly IDisposable seam;

            internal QueueSeam() {
                this.seam = FeedbackService.OverrideQueuePathForTests(
                    System.IO.Path.Combine(this.dir.Root, "feedback_queue.json"));
            }

            public void Dispose() {
                this.seam.Dispose();
                this.dir.Dispose();
            }
        }

        /// <summary>Подставной транспорт: отвечает по заданному правилу и запоминает адреса запросов.</summary>
        private sealed class FakeHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;
            private readonly ConcurrentQueue<Uri> seen = new();

            internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => this.reply = reply;

            internal IReadOnlyList<Uri> Requests => this.seen.ToArray();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
                this.seen.Enqueue(request.RequestUri!);
                return Task.FromResult(this.reply(request));
            }
        }
    }
}
