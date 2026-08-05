// <copyright file="ErrorReporterAutoReportTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Автоматическая отправка отчётов об ошибках — единственное, что лаунчер отправляет
    /// на сервер без прямой просьбы человека. Всё остальное (обратная связь, жалобы) человек
    /// нажимает сам.
    /// <para>
    /// Отсюда два обещания, которые здесь и закрепляются. Первое: выключенный тумблер
    /// «Автоотчёты об ошибках» означает НОЛЬ запросов в сеть, а не «отправим, но поменьше».
    /// Второе: в отчёт не уезжает имя пользователя — текст исключения почти всегда содержит
    /// путь вида C:\Users\&lt;имя&gt;\..., и без редактирования каждое падение сообщало бы серверу,
    /// как зовут человека за компьютером.
    /// </para>
    /// <para>
    /// Остальное — про живучесть: отчёт об ошибке, роняющий приложение, хуже отсутствия
    /// отчёта, поэтому отправка обязана молча переживать отказ сети и отказ сервера.
    /// А дедупликация и квота нужны, чтобы цикл «падение → отчёт → падение» не превратил
    /// лаунчер в источник трафика на собственный сервер.
    /// </para>
    /// <para>
    /// Транспорт подменяется на подставной (<see cref="ReportScope"/>): в сеть тесты не ходят.
    /// Тумблеры конфига правятся только в памяти, файлы квот в %APPDATA% сохраняются
    /// и возвращаются на место.
    /// </para>
    /// </summary>
    public class ErrorReporterAutoReportTests {
        /// <summary>
        /// Выключенный тумблер запрещает отправку целиком: ни одного запроса, ни одного
        /// события, и даже квота не тратится. Это обещание приватности, данное человеку
        /// в настройках, — самый дорогой отказ во всём файле.
        /// </summary>
        [Fact]
        public async Task ВыключенныйТумблерЗапрещаетОтправку() {
            using var scope = new ReportScope(_ => Ok());
            scope.AutoErrorReports = false;

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            Assert.Empty(scope.Requests);
            Assert.Empty(scope.Reported);
            Assert.Empty(scope.Suppressed);
            Assert.Equal(0, scope.GlobalQuotaCount);
        }

        /// <summary>
        /// Тумблер спрашивают на каждой отправке, а не один раз при запуске: человек может
        /// выключить автоотчёты прямо посреди сеанса — с этого момента не должно уйти ничего.
        /// </summary>
        [Fact]
        public async Task ТумблерПеречитываетсяПередКаждойОтправкой() {
            using var scope = new ReportScope(_ => Ok());

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("первое"), "Тест");
            Assert.Single(scope.Requests);

            scope.AutoErrorReports = false;
            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("второе"), "Тест");

            Assert.Single(scope.Requests);
        }

        /// <summary>
        /// Без адреса сервера отправлять некуда. Проверяется отдельно, потому что пустой
        /// адрес — это не ошибка, а нормальное состояние конфига, испорченного вручную:
        /// собранный из пустоты URL ушёл бы неизвестно куда.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task ПустойАдресСервераОстанавливаетОтправку(string? api) {
            using var scope = new ReportScope(_ => Ok());
            scope.ApiBaseUrl = api;

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            Assert.Empty(scope.Requests);
            Assert.Empty(scope.Reported);
        }

        /// <summary>
        /// Принятый сервером отчёт поднимает событие <c>AutoReported</c>: на нём висит
        /// уведомление, по которому человек вообще узнаёт, что о падении сообщили.
        /// Заодно проверяется адрес — хвостовой слеш базы не должен удваиваться.
        /// </summary>
        [Fact]
        public async Task УспешнаяОтправкаПоднимаетСобытие() {
            using var scope = new ReportScope(_ => Ok(), apiBaseUrl: "https://example.test/");

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Загрузка игры");

            Assert.Equal("https://example.test/feedback/submit", Assert.Single(scope.Requests).ToString());
            Assert.Equal("Загрузка игры", Assert.Single(scope.Reported));
            Assert.Empty(scope.Suppressed);
        }

        /// <summary>
        /// Автоотчёт помечается признаком <c>auto=1</c> и типом <c>bug</c>: на сервере он лежит
        /// в одной таблице с письмами живых людей, и без метки поток машинных отчётов
        /// хоронит настоящую обратную связь.
        /// </summary>
        [Fact]
        public async Task ОтчётПомеченКакАвтоматический() {
            using var scope = new ReportScope(_ => Ok());

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            var body = JsonDocument.Parse(Assert.Single(scope.Bodies)).RootElement;
            Assert.Equal("auto", body.GetProperty("name").GetString());
            Assert.Equal("bug", body.GetProperty("type").GetString());
            Assert.Equal("1", body.GetProperty("system").GetProperty("auto").GetString());
            Assert.False(body.GetProperty("attachLogs").GetBoolean());
        }

        /// <summary>
        /// Исчерпанная глобальная квота закрывает отправку, называет срок повтора и НЕ ходит
        /// в сеть. Квота — последняя защита от лавины: падение в цикле перезапусков иначе
        /// шлёт отчёт за отчётом, пока сервер не ляжет.
        /// </summary>
        [Fact]
        public async Task ИсчерпаннаяКвотаЗакрываетОтправку() {
            using var scope = new ReportScope(_ => Ok());
            scope.WriteGlobalQuota(count: 3, windowStart: DateTime.UtcNow);

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            Assert.Empty(scope.Requests);
            Assert.Empty(scope.Reported);
            var retryAfter = Assert.Single(scope.Suppressed);
            Assert.True(retryAfter > TimeSpan.Zero, "срок повтора не назван");
            Assert.True(retryAfter <= TimeSpan.FromMinutes(3), $"срок повтора {retryAfter} больше окна квоты");
        }

        /// <summary>
        /// Одна и та же ошибка из одного и того же места уходит не больше трёх раз за окно.
        /// Без этого ошибка в цикле отрисовки (сотни повторов в секунду) выжигает и канал,
        /// и квоту за доли секунды, а на сервере лежит триста одинаковых отчётов.
        /// </summary>
        [Fact]
        public async Task ОдинаковаяОшибкаУходитНеБольшеТрёхРаз() {
            using var scope = new ReportScope(_ => Ok());

            for (var i = 0; i < 7; i++) {
                await ErrorReporter.ReportForTestsAsync(Repeated(), "Отрисовка");
            }

            Assert.Equal(3, scope.Requests.Count);
            Assert.Equal(3, scope.Reported.Count);

            // Лишние повторы отсекает именно дедупликация, а не квота: квота о своём отказе
            // сообщает событием, и здесь его быть не должно.
            Assert.Empty(scope.Suppressed);
        }

        /// <summary>
        /// Дедупликация не должна склеивать разные ошибки: иначе первое же частое падение
        /// затыкает отчёты обо всех остальных, и настоящая причина до разработчика не доходит.
        /// </summary>
        [Fact]
        public async Task РазныеОшибкиНеСклеиваютсяДедупликацией() {
            using var scope = new ReportScope(_ => Ok());

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("первая"), "Тест");
            await ErrorReporter.ReportForTestsAsync(new IOException("вторая"), "Тест");

            Assert.Equal(2, scope.Requests.Count);
        }

        /// <summary>
        /// Одна и та же ошибка из разных мест — это разные отчёты: контекст входит в подпись,
        /// иначе падение при запуске игры глушило бы такое же падение при обновлении.
        /// </summary>
        [Fact]
        public void КонтекстВходитВПодпись() {
            var ex = new InvalidOperationException("одно и то же");

            Assert.NotEqual(ErrorReporter.BuildSignature(ex, "Запуск"), ErrorReporter.BuildSignature(ex, "Обновление"));
            Assert.Equal(ErrorReporter.BuildSignature(ex, "Запуск"), ErrorReporter.BuildSignature(ex, "Запуск"));
        }

        /// <summary>
        /// Подпись считается по исключению, которое уже сломало приложение, — рассчитывать
        /// на осмысленные поля нельзя. Брошенное исключение стека не имеет, а Message
        /// у самодельного типа может вернуть null: упасть здесь — значит потерять отчёт
        /// именно о самом тяжёлом падении.
        /// </summary>
        [Fact]
        public void ПодписьСчитаетсяДажеУИсключенияБезСтекаИСоЗначениямиNull() {
            Assert.False(string.IsNullOrEmpty(ErrorReporter.BuildSignature(new InvalidOperationException("не брошено"), "Тест")));
            Assert.False(string.IsNullOrEmpty(ErrorReporter.BuildSignature(new BrokenException(), "Тест")));
            Assert.False(string.IsNullOrEmpty(ErrorReporter.BuildSignature(new BrokenException(), null!)));
        }

        /// <summary>
        /// Счётчик повторов начинает окно заново, когда старое истекло: иначе одна ошибка,
        /// случившаяся трижды за всё время работы, замолкала бы навсегда.
        /// </summary>
        [Fact]
        public void ОкноПовторовОткрываетсяЗаново() {
            ErrorReporter.ResetThrottleForTests();
            try {
                var sig = ErrorReporter.BuildSignature(new InvalidOperationException("окно"), "Тест");

                Assert.False(ErrorReporter.ShouldThrottle(sig));
                Assert.False(ErrorReporter.ShouldThrottle(sig));
                Assert.False(ErrorReporter.ShouldThrottle(sig));
                Assert.True(ErrorReporter.ShouldThrottle(sig));

                ErrorReporter.ResetThrottleForTests();
                Assert.False(ErrorReporter.ShouldThrottle(sig));
            }
            finally {
                ErrorReporter.ResetThrottleForTests();
            }
        }

        /// <summary>
        /// Имя пользователя не должно уезжать на сервер. В тексте исключения путь вида
        /// C:\Users\&lt;имя&gt;\... появляется почти всегда — «файл не найден», «доступ запрещён»,
        /// любая работа с файлами игры. Автоотчёт уходит БЕЗ участия человека, поэтому
        /// проверить, что именно уехало, ему негде: это делает тест.
        /// </summary>
        [Fact]
        public async Task ИмяПользователяНеУтекаетВОтчёт() {
            using var scope = new ReportScope(_ => Ok());
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(profile, "ChillHub", "games", "save.dat");

            await ErrorReporter.ReportForTestsAsync(new IOException($"Не удалось открыть файл {path}"), "Сохранение");

            var body = Assert.Single(scope.Bodies);
            Assert.False(
                body.Contains(profile, StringComparison.OrdinalIgnoreCase),
                "путь к профилю пользователя ушёл на сервер как есть");
            Assert.False(
                body.Contains(profile.Replace(@"\", @"\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase),
                "путь к профилю ушёл на сервер в экранированном json-виде");

            // Redact заменяет имя только начиная с трёх символов: на более коротких заменять
            // опасно — они встречаются внутри посторонних слов.
            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3) {
                Assert.False(
                    body.Contains(user, StringComparison.OrdinalIgnoreCase),
                    "имя пользователя ушло на сервер");
            }

            var comment = JsonDocument.Parse(body).RootElement.GetProperty("comment").GetString() ?? string.Empty;
            Assert.Contains("%USERPROFILE%", comment, StringComparison.Ordinal);
            Assert.Contains("Сохранение", comment, StringComparison.Ordinal);
        }

        /// <summary>
        /// Настоящий автоотчёт уходит ВМЕСТЕ с диагностикой: конфиг, дерево папки игр, хвосты
        /// логов. Это самая объёмная выгрузка, которую лаунчер делает по своей инициативе,
        /// и она состоит почти целиком из абсолютных путей.
        /// <para>
        /// ErrorReporter редактирует только текст исключения и полагается на то, что
        /// <c>Diagnostics.Build</c> уже вычистил бандл (об этом сказано в комментарии рядом).
        /// Договор живёт в двух файлах сразу, поэтому проверяется на результате: имени
        /// пользователя не должно быть НИГДЕ в отправленном теле.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПриложеннаяДиагностикаТожеБезИмениПользователя() {
            using var logs = new TempDir();
            using var games = new TempDir();
            using var logSeam = ChillHub.Core.Logging.Logger.OverrideForTests(logs.Root);
            using var scope = new ReportScope(_ => Ok());
            var savedGames = ConfigService.Current.GamesPath;
            ConfigService.Current.GamesPath = games.Root;
            try {
                await ErrorReporter.ReportForTestsAsync(
                    new IOException("падение"), "Тест", includeDiagnostics: true);
            }
            finally {
                ConfigService.Current.GamesPath = savedGames;
            }

            var body = Assert.Single(scope.Bodies);
            var root = JsonDocument.Parse(body).RootElement;
            Assert.True(root.GetProperty("attachLogs").GetBoolean(), "диагностика не помечена приложенной");
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("logs").GetString()), "бандл диагностики пуст");

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(
                body.Contains(profile, StringComparison.OrdinalIgnoreCase),
                "путь к профилю пользователя уехал вместе с диагностикой");
            Assert.False(
                body.Contains(profile.Replace(@"\", @"\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase),
                "путь к профилю уехал вместе с диагностикой в экранированном json-виде");

            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3) {
                Assert.False(body.Contains(user, StringComparison.OrdinalIgnoreCase), "имя пользователя уехало вместе с диагностикой");
            }
        }

        /// <summary>
        /// Сведения о системе не содержат имени машины: оно убрано намеренно (см. комментарий
        /// в коде рядом). Имя компьютера в домашних сборках Windows — это чаще всего имя
        /// владельца, и в отчёте, который уходит сам, ему делать нечего.
        /// </summary>
        [Fact]
        public void СведенияОСистемеНеСодержатИмениМашины() {
            var info = ErrorReporter.CollectSystemInfo();

            Assert.DoesNotContain(info.Keys, k => k.Contains("machine", StringComparison.OrdinalIgnoreCase));
            var machine = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(machine)) {
                Assert.DoesNotContain(info.Values, v => v.Contains(machine, StringComparison.OrdinalIgnoreCase));
            }

            // То, ради чего сведения вообще собираются: без версии ОС и сборки разбор отчёта
            // сводится к гаданию.
            Assert.True(info.ContainsKey("os"), "версия ОС не собрана");
            Assert.True(info.ContainsKey("arch"), "разрядность не собрана");
            Assert.True(info.ContainsKey("dotnet"), "версия среды не собрана");
            Assert.True(info.ContainsKey("appVersion"), "версия лаунчера не собрана");
        }

        /// <summary>
        /// Запасной путь на порт админки существует только для петлевых адресов: это
        /// удобство отладки. Для прода такого пути быть не должно — иначе отчёт уходил бы
        /// на посторонний порт чужого хоста.
        /// </summary>
        [Theory]
        [InlineData("http://localhost:5000", true)]
        [InlineData("http://127.0.0.1:8080", true)]
        [InlineData("https://LOCALHOST", true)]
        [InlineData("https://launcher.samoy.love", false)]
        [InlineData("https://example.test:55777", false)]
        [InlineData("не адрес вовсе", false)]
        [InlineData("", false)]
        public void ЗапаснойПортАдминкиТолькоДляЛокальногоАдреса(string baseApi, bool expected) {
            Assert.Equal(expected, ErrorReporter.TryBuildLocalAdminUrl(baseApi, out var adminUrl));

            if (expected) {
                Assert.Equal("/feedback/submit", new Uri(adminUrl).AbsolutePath);
                Assert.Equal(55777, new Uri(adminUrl).Port);
            }
            else {
                Assert.Equal(string.Empty, adminUrl);
            }
        }

        /// <summary>
        /// Локальная отладка: если сервер на основном порту не отвечает, отчёт уходит
        /// на порт админки. Без этого разработчик не видит собственных автоотчётов
        /// и правит их вслепую.
        /// </summary>
        [Fact]
        public async Task ОбрывСетиУводитОтчётНаПортАдминки() {
            using var scope = new ReportScope(
                req => req.RequestUri!.Port == 55777 ? Ok() : throw new HttpRequestException("сеть недоступна"),
                apiBaseUrl: "http://localhost:5000");

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            Assert.Equal(new[] { 5000, 55777 }, scope.Requests.Select(u => u.Port).ToArray());
            Assert.Single(scope.Reported);
        }

        /// <summary>Отказ основного порта тоже переводит отчёт на админку: 404 здесь означает не тот порт.</summary>
        [Fact]
        public async Task ОтказСервераУводитОтчётНаПортАдминки() {
            using var scope = new ReportScope(
                req => req.RequestUri!.Port == 55777 ? Ok() : new HttpResponseMessage(HttpStatusCode.NotFound),
                apiBaseUrl: "http://127.0.0.1:5000");

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            Assert.Equal(new[] { 5000, 55777 }, scope.Requests.Select(u => u.Port).ToArray());
            Assert.Single(scope.Reported);
        }

        /// <summary>
        /// Обрыв сети не роняет вызывающего и не выдаёт неотправленный отчёт за успех.
        /// Report зовут из Logger.Error — то есть уже посреди обработки другой ошибки:
        /// исключение отсюда убило бы приложение вместо того, чтобы сообщить о поломке.
        /// </summary>
        [Fact]
        public async Task ОбрывСетиНеРоняетВызывающего() {
            using var scope = new ReportScope(_ => throw new HttpRequestException("сеть недоступна"));

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            Assert.Single(scope.Requests);
            Assert.Empty(scope.Reported);
        }

        /// <summary>Отказ сервера — тоже не успех: события об отправке быть не должно.</summary>
        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.InternalServerError)]
        public async Task ОтказСервераНеСчитаетсяОтправкой(HttpStatusCode code) {
            using var scope = new ReportScope(_ => new HttpResponseMessage(code));

            await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");

            Assert.Single(scope.Requests);
            Assert.Empty(scope.Reported);
        }

        /// <summary>
        /// Падение внутри подписчика на <c>AutoReported</c> не должно возвращаться в отправку:
        /// на этом событии висит UI, а он способен бросить что угодно.
        /// </summary>
        [Fact]
        public async Task ПадениеПодписчикаНеВозвращаетсяВОтправку() {
            using var scope = new ReportScope(_ => Ok());
            void Broken(string _) => throw new InvalidOperationException("подписчик сломан");
            ErrorReporter.AutoReported += Broken;
            try {
                await ErrorReporter.ReportForTestsAsync(new InvalidOperationException("падение"), "Тест");
            }
            finally {
                ErrorReporter.AutoReported -= Broken;
            }

            Assert.Single(scope.Requests);
        }

        /// <summary>
        /// Повторный вызов <c>InitGlobalHandlers</c> не плодит подписки. Метод зовут при старте
        /// и при восстановлении после сбоя; вторая подписка означала бы два отчёта на каждое
        /// необработанное исключение — и вдвое быстрее выжженную квоту.
        /// </summary>
        [Fact]
        public void ПовторнаяУстановкаОбработчиковНеПлодитПодписки() {
            ErrorReporter.InitGlobalHandlers();
            var afterFirst = CountReporterHandlers();
            Assert.True(afterFirst > 0, "подписки на глобальные исключения не найдены");

            ErrorReporter.InitGlobalHandlers();
            ErrorReporter.InitGlobalHandlers();

            Assert.Equal(afterFirst, CountReporterHandlers());
        }

        /// <summary>
        /// Обработчики необработанных исключений сами обязаны быть непробиваемыми: их зовёт
        /// среда исполнения в момент, когда приложение уже падает. Сюда приходит и «объект,
        /// который не исключение» (UnhandledException умеет такое), и пустой аргумент.
        /// </summary>
        [Fact]
        public void ОбработчикиПереживаютНеожиданныйАргумент() {
            using var scope = new ReportScope(_ => Ok());
            scope.AutoErrorReports = false;

            Invoke("CurrentDomain_UnhandledException", null, new UnhandledExceptionEventArgs("не исключение", false));
            Invoke("CurrentDomain_UnhandledException", null, new UnhandledExceptionEventArgs(new InvalidOperationException("падение"), true));
            Invoke("TaskScheduler_UnobservedTaskException", null, new UnobservedTaskExceptionEventArgs(null!));
            Invoke(
                "TaskScheduler_UnobservedTaskException",
                null,
                new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("падение"))));

            Assert.Empty(scope.Requests);
        }

        private static HttpResponseMessage Ok() => new HttpResponseMessage(HttpStatusCode.OK);

        /// <summary>Одно и то же исключение с точки зрения подписи: тип, текст и место совпадают.</summary>
        private static InvalidOperationException Repeated() => new InvalidOperationException("одна и та же ошибка");

        /// <summary>Зовёт закрытый обработчик так же, как это сделала бы среда исполнения.</summary>
        private static void Invoke(string method, object? sender, EventArgs args) {
            var mi = typeof(ErrorReporter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(mi != null, $"обработчик {method} не найден");
            mi!.Invoke(null, new[] { sender, args });
        }

        /// <summary>
        /// Считает подписки, принадлежащие <see cref="ErrorReporter"/>, среди обработчиков
        /// глобальных исключений. Другого способа увидеть двойную подписку нет: события
        /// AppDomain и TaskScheduler наружу свой список не отдают.
        /// </summary>
        private static int CountReporterHandlers()
            => CountIn(typeof(AppDomain), AppDomain.CurrentDomain) + CountIn(typeof(TaskScheduler), null);

        private static int CountIn(Type type, object? instance) {
            var flags = BindingFlags.NonPublic | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
            var total = 0;
            foreach (var field in type.GetFields(flags)) {
                if (!typeof(Delegate).IsAssignableFrom(field.FieldType)) {
                    continue;
                }

                if (field.GetValue(instance) is not Delegate handler) {
                    continue;
                }

                total += handler.GetInvocationList().Count(h => h.Method.DeclaringType == typeof(ErrorReporter));
            }

            return total;
        }

        /// <summary>Исключение, у которого нет ни стека, ни текста: подпись обязана пережить и такое.</summary>
        private sealed class BrokenException : Exception {
            public override string Message => null!;

            public override string? StackTrace => null;
        }

        /// <summary>
        /// Обстановка для одного теста: подставной транспорт, тумблеры конфига только в памяти
        /// и сохранённые файлы квот.
        /// <para>
        /// Конфиг НИКОГДА не сохраняется на диск: <c>ConfigService.Save</c> писал бы в настоящий
        /// %APPDATA%\ChillHub\config.json и менял настройки разработчика. Файлы квот подменить
        /// нечем — путь задан внутри продакшн-кода, — поэтому их содержимое сохраняется
        /// и возвращается на место.
        /// </para>
        /// </summary>
        private sealed class ReportScope : IDisposable {
            private readonly IDisposable httpSeam;
            private readonly HttpClient client;
            private readonly FakeHandler handler;
            private readonly ConcurrentQueue<string> reported = new();
            private readonly ConcurrentQueue<TimeSpan> suppressed = new();
            private readonly bool savedAutoErrorReports;
            private readonly string? savedApiBaseUrl;
            private readonly string? savedGlobalQuota;
            private readonly string? savedManualQuota;

            internal ReportScope(Func<HttpRequestMessage, HttpResponseMessage> reply, string? apiBaseUrl = "https://example.test") {
                this.savedGlobalQuota = ReadFile(GlobalQuotaPath);
                this.savedManualQuota = ReadFile(ManualQuotaPath);
                this.WriteGlobalQuota(count: 0, windowStart: DateTime.UtcNow);

                this.savedAutoErrorReports = ConfigService.Current.AutoErrorReports;
                this.savedApiBaseUrl = ConfigService.Current.ApiBaseUrl;
                ConfigService.Current.AutoErrorReports = true;
                ConfigService.Current.ApiBaseUrl = apiBaseUrl!;

                ErrorReporter.ResetThrottleForTests();

                this.handler = new FakeHandler(reply);
                this.client = new HttpClient(this.handler);
                this.httpSeam = ErrorReporter.OverrideHttpForTests(this.client);

                ErrorReporter.AutoReported += this.OnReported;
                ErrorReporter.AutoReportSuppressed += this.OnSuppressed;
            }

            /// <summary>Адреса, на которые ушли запросы, в порядке отправки.</summary>
            internal IReadOnlyList<Uri> Requests => this.handler.Requests;

            /// <summary>Тела отправленных запросов.</summary>
            internal IReadOnlyList<string> Bodies => this.handler.Bodies;

            /// <summary>Контексты, о которых поднялось событие успешной отправки.</summary>
            internal IReadOnlyList<string> Reported => this.reported.ToArray();

            /// <summary>Сроки повтора, названные при исчерпанной квоте.</summary>
            internal IReadOnlyList<TimeSpan> Suppressed => this.suppressed.ToArray();

            /// <summary>Тумблер автоотчётов; правится только в памяти.</summary>
            internal bool AutoErrorReports {
                get => ConfigService.Current.AutoErrorReports;
                set => ConfigService.Current.AutoErrorReports = value;
            }

            /// <summary>Адрес сервера; правится только в памяти.</summary>
            internal string? ApiBaseUrl {
                get => ConfigService.Current.ApiBaseUrl;
                set => ConfigService.Current.ApiBaseUrl = value!;
            }

            /// <summary>Сколько отчётов уже списано с глобальной квоты.</summary>
            internal int GlobalQuotaCount {
                get {
                    var json = ReadFile(GlobalQuotaPath);
                    if (string.IsNullOrWhiteSpace(json)) {
                        return -1;
                    }

                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("Count").GetInt32();
                }
            }

            private static string QuotaDir => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");

            private static string GlobalQuotaPath => Path.Combine(QuotaDir, "report_rl.json");

            private static string ManualQuotaPath => Path.Combine(QuotaDir, "report_manual_rl.json");

            internal void WriteGlobalQuota(int count, DateTime windowStart) {
                Directory.CreateDirectory(QuotaDir);
                var json = JsonSerializer.Serialize(new { Count = count, WindowStartUtc = windowStart });
                File.WriteAllText(GlobalQuotaPath, json, Encoding.UTF8);
            }

            public void Dispose() {
                ErrorReporter.AutoReported -= this.OnReported;
                ErrorReporter.AutoReportSuppressed -= this.OnSuppressed;
                this.httpSeam.Dispose();
                this.client.Dispose();
                ErrorReporter.ResetThrottleForTests();

                ConfigService.Current.AutoErrorReports = this.savedAutoErrorReports;
                ConfigService.Current.ApiBaseUrl = this.savedApiBaseUrl!;

                RestoreFile(GlobalQuotaPath, this.savedGlobalQuota);
                RestoreFile(ManualQuotaPath, this.savedManualQuota);
            }

            private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

            private static void RestoreFile(string path, string? content) {
                try {
                    if (content == null) {
                        if (File.Exists(path)) {
                            File.Delete(path);
                        }

                        return;
                    }

                    File.WriteAllText(path, content, Encoding.UTF8);
                }
                catch {
                    // Вернуть файл не удалось — прогон из-за этого валить не нужно.
                }
            }

            private void OnReported(string context) => this.reported.Enqueue(context);

            private void OnSuppressed(TimeSpan retryAfter) => this.suppressed.Enqueue(retryAfter);
        }

        /// <summary>Подставной транспорт: отвечает по заданному правилу и запоминает адреса и тела.</summary>
        private sealed class FakeHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;
            private readonly ConcurrentQueue<Uri> seen = new();
            private readonly ConcurrentQueue<string> bodies = new();

            internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => this.reply = reply;

            internal IReadOnlyList<Uri> Requests => this.seen.ToArray();

            internal IReadOnlyList<string> Bodies => this.bodies.ToArray();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
                this.seen.Enqueue(request.RequestUri!);

                // Тело читается прямо здесь: запрос живёт до конца using в продакшн-коде,
                // а тесту оно нужно после возврата.
                this.bodies.Enqueue(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty);
                return Task.FromResult(this.reply(request));
            }
        }
    }
}
