// <copyright file="MaintenanceServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Maintenance;

    using Xunit;

    /// <summary>
    /// Подставной транспорт: отвечает тем, что скажет тест, и считает запросы.
    /// Без него опрос техработ проверить нечем — сервис ходит в сеть.
    /// </summary>
    internal sealed class FakeMaintenanceHandler : HttpMessageHandler {
        private readonly Func<int, HttpResponseMessage> respond;
        private int calls;

        public FakeMaintenanceHandler(Func<int, HttpResponseMessage> respond) {
            this.respond = respond;
        }

        /// <summary>Сколько запросов пришло. По нему видно, идёт ли ещё опрос.</summary>
        public int Calls => Volatile.Read(ref this.calls);

        /// <summary>Адрес последнего запроса — проверяем, что сервис стучится куда надо.</summary>
        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var n = Interlocked.Increment(ref this.calls);
            this.LastUrl = request.RequestUri?.ToString() ?? string.Empty;
            return Task.FromResult(this.respond(n));
        }
    }

    /// <summary>
    /// Опрос режима технических работ.
    /// <para>
    /// Цена ошибки здесь несимметрична: лишний баннер всего лишь мешает, а пропущенный
    /// или незакрытый баннер оставляет пользователя либо без предупреждения, либо
    /// с заблокированным лаунчером до перезапуска. Поэтому проверяется прежде всего
    /// поведение на отказах: старый сервер без эндпоинта, обрыв сети, мусор в ответе.
    /// </para>
    /// <para>
    /// Состояние сервиса статическое, поэтому каждый тест начинается и заканчивается
    /// сбросом, а фоновый цикл дожидается остановки — иначе результат утекал бы в соседний тест.
    /// </para>
    /// </summary>
    public class MaintenanceServiceTests : IDisposable {
        private readonly List<HttpClient> clients = new List<HttpClient>();

        public MaintenanceServiceTests() {
            MaintenanceService.ResetForTests();
        }

        /// <summary>Сервер сообщил о работах — состояние обновилось, подписчики узнали.</summary>
        [Fact]
        public async Task ВключённыйРежимОбновляетСостояниеИПоднимаетСобытие() {
            var handler = this.UseHandler(_ => Json("{\"enabled\":true,\"reason\":\"Переезд базы\",\"blocks\":{\"install\":true,\"update\":true,\"launch\":false}}"));
            var raised = new List<MaintenanceState>();
            MaintenanceService.Changed += raised.Add;

            var state = await MaintenanceService.RefreshNowAsync();

            Assert.True(state.Enabled);
            Assert.True(MaintenanceService.Current.Enabled);
            Assert.Equal("Переезд базы", MaintenanceService.Current.Reason);
            Assert.True(MaintenanceService.Current.BlocksInstall);
            Assert.False(MaintenanceService.Current.BlocksPlay);
            Assert.Single(raised);
            Assert.Contains(MaintenanceService.EndpointPath, handler.LastUrl, StringComparison.Ordinal);
        }

        /// <summary>
        /// Ответ не изменился — событие не поднимается повторно.
        /// Иначе баннер перерисовывался бы, а лог пополнялся одинаковой записью
        /// на каждом круге опроса: раз в минуту всё время работ.
        /// </summary>
        [Fact]
        public async Task НеизменившийсяОтветНеПоднимаетСобытиеПовторно() {
            this.UseHandler(_ => Json("{\"enabled\":true,\"reason\":\"Переезд базы\"}"));
            var raised = 0;
            MaintenanceService.Changed += _ => Interlocked.Increment(ref raised);

            await MaintenanceService.RefreshNowAsync();
            await MaintenanceService.RefreshNowAsync();
            await MaintenanceService.RefreshNowAsync();

            Assert.Equal(1, raised);
        }

        /// <summary>
        /// Сервер старой версии эндпоинта не знает и отвечает 404/501. Это не отказ:
        /// лаунчер обязан работать с таким сервером ровно как раньше — без баннера
        /// и без блокировок, а не считать, что режим работ включён.
        /// </summary>
        /// <param name="code">Код ответа сервера.</param>
        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.NotImplemented)]
        public async Task ОтсутствиеЭндпоинтаСчитаетсяВыключеннымРежимом(HttpStatusCode code) {
            this.UseHandler(n => n == 1
                ? Json("{\"enabled\":true,\"reason\":\"Работы\"}")
                : new HttpResponseMessage(code));

            await MaintenanceService.RefreshNowAsync();
            Assert.True(MaintenanceService.Current.Enabled);

            var state = await MaintenanceService.RefreshNowAsync();

            Assert.False(state.Enabled);
            Assert.False(MaintenanceService.Current.BlocksInstall);
        }

        /// <summary>
        /// Сеть отвалилась на один запрос — прежнее состояние сохраняется.
        /// Снимать баннер, который сервер только что показывал, из-за одного
        /// таймаута нельзя: пользователь тут же уйдёт качать сборку в никуда.
        /// </summary>
        [Fact]
        public async Task СбойСетиСохраняетПрежнееСостояние() {
            this.UseHandler(n => n == 1
                ? Json("{\"enabled\":true,\"reason\":\"Работы\"}")
                : throw new HttpRequestException("сеть недоступна"));

            await MaintenanceService.RefreshNowAsync();
            var state = await MaintenanceService.RefreshNowAsync();

            Assert.True(state.Enabled);
            Assert.True(MaintenanceService.Current.Enabled);
            Assert.Equal("Работы", MaintenanceService.Current.Reason);
        }

        /// <summary>
        /// Вместо JSON пришла страница прокси или обрезанный ответ. Разобрать нечего,
        /// но и сбрасывать состояние в «работ нет» по такому поводу нельзя — причина та же,
        /// что и у сетевого сбоя.
        /// </summary>
        /// <param name="body">Тело ответа, которое не разбирается в состояние.</param>
        [Theory]
        [InlineData("<html>502 Bad Gateway</html>")]
        [InlineData("{\"enabled\":")]
        [InlineData("")]
        public async Task МусорВместоJsonСохраняетПрежнееСостояние(string body) {
            this.UseHandler(n => n == 1
                ? Json("{\"enabled\":true,\"reason\":\"Работы\"}")
                : Json(body));

            await MaintenanceService.RefreshNowAsync();
            await MaintenanceService.RefreshNowAsync();

            Assert.True(MaintenanceService.Current.Enabled);
        }

        /// <summary>
        /// Работы закончились — сервер отвечает enabled:false, событие поднимается,
        /// UI разблокируется. Без этого пользователю пришлось бы перезапускать лаунчер,
        /// чтобы снять блокировку, которой на сервере уже нет.
        /// </summary>
        [Fact]
        public async Task ВыходИзРежимаРазблокируетБезПерезапуска() {
            this.UseHandler(n => n == 1
                ? Json("{\"enabled\":true,\"reason\":\"Работы\"}")
                : Json("{\"enabled\":false}"));
            var raised = new List<MaintenanceState>();

            await MaintenanceService.RefreshNowAsync();
            MaintenanceService.Changed += raised.Add;
            await MaintenanceService.RefreshNowAsync();

            Assert.Single(raised);
            Assert.False(raised[0].Enabled);
            Assert.False(MaintenanceService.Current.BlocksInstall);
            Assert.False(MaintenanceService.Current.BlocksUpdate);
        }

        /// <summary>
        /// Адрес собирается из базового без задвоенного слеша: сервер на такой путь
        /// отвечает 404, и режим работ молча перестал бы приходить вовсе.
        /// </summary>
        /// <param name="baseUrl">Базовый адрес из настроек.</param>
        [Theory]
        [InlineData("https://launcher.samoy.love")]
        [InlineData("https://launcher.samoy.love/")]
        [InlineData("https://launcher.samoy.love///")]
        public void АдресСобираетсяБезЗадвоенногоСлеша(string baseUrl) {
            Assert.Equal("https://launcher.samoy.love/api/maintenance", MaintenanceService.BuildUrl(baseUrl));
        }

        /// <summary>Настроек ещё нет — адрес всё равно собирается, а не падает на null.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ПустойБазовыйАдресНеРоняетСборкуАдреса(string? baseUrl) {
            Assert.Equal(MaintenanceService.EndpointPath, MaintenanceService.BuildUrl(baseUrl));
        }

        /// <summary>
        /// Подписчик упал на своём коде — опрос обязан это пережить.
        /// Иначе одна ошибка в разметке страницы навсегда останавливала бы слежение
        /// за режимом работ для всего приложения.
        /// </summary>
        [Fact]
        public async Task ПадениеПодписчикаНеРоняетОпрос() {
            this.UseHandler(_ => Json("{\"enabled\":true,\"reason\":\"Работы\"}"));
            MaintenanceService.Changed += _ => throw new InvalidOperationException("страница уже закрыта");

            var state = await MaintenanceService.RefreshNowAsync();

            Assert.True(state.Enabled);
            Assert.True(MaintenanceService.Current.Enabled);
        }

        /// <summary>Успешный ответ обнуляет серию сбоев — иначе первое падение перестало бы попадать в лог.</summary>
        [Fact]
        public async Task УспешныйОтветОбнуляетСериюСбоев() {
            this.UseHandler(n => n <= 2
                ? throw new HttpRequestException("сеть недоступна")
                : Json("{\"enabled\":false}"));

            await MaintenanceService.RefreshNowAsync();
            await MaintenanceService.RefreshNowAsync();
            Assert.Equal(2, MaintenanceService.ConsecutiveFailures);

            await MaintenanceService.RefreshNowAsync();
            Assert.Equal(0, MaintenanceService.ConsecutiveFailures);
        }

        /// <summary>
        /// Первый запрос цикла сорвался — цикл продолжает работу и подхватывает
        /// состояние со следующей попытки. Остановись он на первой ошибке, клиент
        /// навсегда остался бы с состоянием, известным на момент запуска.
        /// </summary>
        [Fact]
        public async Task ЦиклПродолжаетРаботуПослеСбоя() {
            MaintenanceService.PollInterval = TimeSpan.FromMilliseconds(20);
            this.UseHandler(n => n == 1
                ? throw new HttpRequestException("сеть недоступна")
                : Json("{\"enabled\":true,\"reason\":\"Работы\"}"));

            MaintenanceService.Start();

            Assert.True(await WaitUntilAsync(() => MaintenanceService.Current.Enabled));
        }

        /// <summary>
        /// Stop действительно гасит цикл: после выхода из приложения фоновые запросы
        /// продолжаться не должны — иначе процесс не завершится и будет стучаться в сеть.
        /// </summary>
        [Fact]
        public async Task ОстановкаПрекращаетОпрос() {
            MaintenanceService.PollInterval = TimeSpan.FromMilliseconds(20);
            var handler = this.UseHandler(_ => Json("{\"enabled\":false}"));

            MaintenanceService.Start();
            Assert.True(await WaitUntilAsync(() => handler.Calls >= 2));

            MaintenanceService.Stop();
            Assert.True(await WaitUntilAsync(() => MaintenanceService.RunningLoops == 0));

            var afterStop = handler.Calls;
            await Task.Delay(200);
            Assert.Equal(afterStop, handler.Calls);
        }

        /// <summary>
        /// Start зовут из нескольких мест старта приложения. Второй вызов не должен
        /// заводить второй цикл: сервер получал бы двойную нагрузку, а событие о смене
        /// состояния приходило бы дважды.
        /// </summary>
        [Fact]
        public async Task ПовторныйСтартНеПлодитВторойЦикл() {
            MaintenanceService.PollInterval = TimeSpan.FromMilliseconds(20);
            var handler = this.UseHandler(_ => Json("{\"enabled\":false}"));

            MaintenanceService.Start();
            MaintenanceService.Start();
            MaintenanceService.Start();

            Assert.True(await WaitUntilAsync(() => handler.Calls >= 3));
            Assert.Equal(1, MaintenanceService.RunningLoops);
        }

        public void Dispose() {
            MaintenanceService.Stop();

            // Клиент нельзя освобождать, пока цикл ещё может им пользоваться.
            var sw = Stopwatch.StartNew();
            while (MaintenanceService.RunningLoops > 0 && sw.ElapsedMilliseconds < 5000) {
                Thread.Sleep(10);
            }

            MaintenanceService.ResetForTests();
            foreach (var client in this.clients) {
                client.Dispose();
            }

            this.clients.Clear();
            GC.SuppressFinalize(this);
        }

        private static HttpResponseMessage Json(string body)
            => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        private static async Task<bool> WaitUntilAsync(Func<bool> condition) {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000) {
                if (condition()) {
                    return true;
                }

                await Task.Delay(10);
            }

            return condition();
        }

        private FakeMaintenanceHandler UseHandler(Func<int, HttpResponseMessage> respond) {
            var handler = new FakeMaintenanceHandler(respond);
            var client = new HttpClient(handler, disposeHandler: true);
            this.clients.Add(client);
            MaintenanceService.HttpOverride = client;
            return handler;
        }
    }
}
