// <copyright file="FeedbackQueueStorageTests.cs" company="PlaceholderCompany">
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
    using System.Windows.Threading;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Оффлайн-очередь обратной связи на диске: сохранение, подъём после перезапуска
    /// и поведение при испорченном файле.
    /// <para>
    /// Очередь — единственное место, где лаунчер хранит НАПИСАННЫЙ ПОЛЬЗОВАТЕЛЕМ текст.
    /// Потерять сообщение здесь — значит потерять его насовсем: формы уже нет, а человек
    /// уверен, что отправил. Поэтому проверяется, что сообщение ложится на диск сразу,
    /// переживает перезапуск и исчезает с диска только после того, как сервер его принял.
    /// </para>
    /// <para>
    /// Файл очереди подменяется на временный: тест, пишущий в настоящий
    /// %APPDATA%\ChillHub\feedback_queue.json, затёр бы неотправленные сообщения разработчика.
    /// </para>
    /// </summary>
    [Collection(FeedbackQueueCollection.Name)]
    public class FeedbackQueueStorageTests {
        /// <summary>
        /// Сообщение ложится на диск сразу и поднимается оттуда следующим запуском.
        /// Ради этого очередь и существует: человек пишет в обратную связь именно тогда,
        /// когда у него что-то не работает, — в том числе сеть.
        /// </summary>
        [Fact]
        public async Task СообщениеПереживаетПерезапускЛаунчера() {
            using var queue = new QueueFileScope();

            var offline = NewService(Unreachable());
            offline.Enqueue(Draft("сервер лежит"));

            Assert.Equal(1, offline.PendingCount);
            Assert.Equal("сервер лежит", Assert.Single(queue.ReadFromDisk()).Comment);

            var accepting = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var restarted = NewService(accepting);
            restarted.Start();

            Assert.Equal(1, restarted.PendingCount);

            await restarted.FlushNowAsync();

            using var sent = JsonDocument.Parse(Assert.Single(accepting.Bodies));
            Assert.Equal("сервер лежит", sent.RootElement.GetProperty("comment").GetString());
        }

        /// <summary>
        /// Доставленное убирается и с диска тоже. Иначе очередь никогда не пустеет и
        /// при каждом запуске лаунчер шлёт на сервер уже разобранные обращения по второму разу.
        /// </summary>
        [Fact]
        public async Task ПослеОтправкиОчередьНаДискеСокращается() {
            using var queue = new QueueFileScope();

            var offline = NewService(Unreachable());
            offline.Enqueue(Draft("первое"));
            offline.Enqueue(Draft("второе"));
            Assert.Equal(2, queue.ReadFromDisk().Count);

            var accepting = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var restarted = NewService(accepting);
            restarted.Start();
            await restarted.FlushNowAsync();

            Assert.Equal(0, restarted.PendingCount);
            Assert.Empty(queue.ReadFromDisk());
        }

        /// <summary>
        /// Испорченный файл очереди не запирает обратную связь: восстанавливать из мусора
        /// нечего, но форма обязана продолжать работать — иначе человек не сможет
        /// пожаловаться даже на то, что у него сломалась очередь.
        /// </summary>
        [Theory]
        [InlineData("[ это не json")]
        [InlineData("{}")]
        [InlineData("")]
        public void БитаяОчередьНеЛомаетФормуОбратнойСвязи(string garbage) {
            using var queue = new QueueFileScope();
            queue.WriteRaw(garbage);

            var svc = NewService(Unreachable());
            svc.Start();

            Assert.Equal(0, svc.PendingCount);

            svc.Enqueue(Draft("новое сообщение"));

            Assert.Equal(1, svc.PendingCount);
            Assert.Equal("новое сообщение", Assert.Single(queue.ReadFromDisk()).Comment);
        }

        /// <summary>
        /// Занятый файл (антивирус, второй экземпляр лаунчера) — то же самое: пустая очередь
        /// в памяти и никакого исключения на пути открытия экрана обратной связи.
        /// </summary>
        [Fact]
        public void НедоступныйФайлОчередиДаётПустуюОчередь() {
            using var queue = new QueueFileScope();
            queue.WriteRaw(JsonSerializer.Serialize(new[] { Draft("уже лежало") }));

            using var hold = new FileStream(queue.Path, FileMode.Open, FileAccess.Read, FileShare.None);
            var svc = NewService(Unreachable());
            svc.Start();

            Assert.Equal(0, svc.PendingCount);
        }

        /// <summary>Первый запуск: файла очереди ещё нет — это норма, а не сбой.</summary>
        [Fact]
        public void ОтсутствующийФайлОчередиДаётПустуюОчередь() {
            using var queue = new QueueFileScope();
            Assert.False(File.Exists(queue.Path));

            var svc = NewService(Unreachable());
            svc.Start();

            Assert.Equal(0, svc.PendingCount);
        }

        /// <summary>
        /// Через диск проходит всё сообщение целиком. Имя, контакт и сведения о системе
        /// нужны, чтобы на обращение можно было ответить: потерянный по дороге контакт
        /// превращает жалобу в анонимную записку.
        /// </summary>
        [Fact]
        public void ЧерновикПереживаетКругЧерезДиск() {
            using var queue = new QueueFileScope();
            var draft = new FeedbackService.FeedbackDraft(
                "Алексей",
                "alex@example.test",
                "bug",
                "не запускается игра",
                false,
                new Dictionary<string, string> { ["os"] = "Windows 11", ["appVersion"] = "1.3.0" });

            NewService(Unreachable()).Enqueue(draft);

            var back = Assert.Single(queue.ReadFromDisk());
            Assert.Equal(draft.Name, back.Name);
            Assert.Equal(draft.Contact, back.Contact);
            Assert.Equal(draft.Type, back.Type);
            Assert.Equal(draft.Comment, back.Comment);
            Assert.Equal(draft.AttachLogs, back.AttachLogs);
            Assert.Equal("Windows 11", back.System!["os"]);
            Assert.Equal("1.3.0", back.System!["appVersion"]);
        }

        /// <summary>
        /// Имена полей на диске менять нельзя: файл очереди уже лежит у пользователей,
        /// и переименование поля превратит их неотправленные сообщения в пустые.
        /// </summary>
        [Fact]
        public void ИменаПолейЧерновикаНеМеняются() {
            var draft = new FeedbackService.FeedbackDraft(
                "Алексей", "alex@example.test", "idea", "добавьте тёмную тему", true, null);

            var json = JsonSerializer.Serialize(new[] { draft });

            foreach (var field in new[] { "Name", "Contact", "Type", "Comment", "AttachLogs", "System" }) {
                Assert.Contains("\"" + field + "\"", json, StringComparison.Ordinal);
            }

            var back = JsonSerializer.Deserialize<List<FeedbackService.FeedbackDraft>>(json)!.Single();
            Assert.Equal(draft, back);
        }

        /// <summary>
        /// Stop действительно останавливает фоновый ретрай. Пока таймер жив, он держит
        /// ссылку на ушедший с экрана сервис и переписывает feedback_queue.json своей
        /// устаревшей копией очереди — сообщения, добавленные после ухода с экрана,
        /// пропадали бы.
        /// </summary>
        [Fact]
        public void ОстановкаГаситФоновыйРетрай() {
            using var queue = new QueueFileScope();
            var svc = NewService(Unreachable());
            svc.Start();

            var timer = RetryTimer(svc);
            Assert.NotNull(timer);
            Assert.True(timer!.IsEnabled, "фоновый ретрай не запустился");

            svc.Stop();

            Assert.False(timer.IsEnabled, "таймер продолжает тикать после Stop");
            Assert.Null(RetryTimer(svc));
        }

        /// <summary>
        /// Остановленный сервис на диск больше не ходит: очередь, пополненную после Stop,
        /// он не затирает своей копией. Это та самая потеря сообщения, ради которой Stop
        /// и вызывается при уходе с экрана.
        /// </summary>
        [Fact]
        public void ПослеОстановкиОчередьНаДискеНеЗатирается() {
            using var queue = new QueueFileScope();
            var svc = NewService(Unreachable());
            svc.Start();
            svc.Enqueue(Draft("первое"));
            svc.Stop();

            // Экран обратной связи открыли заново и написали ещё одно сообщение.
            queue.WriteRaw(JsonSerializer.Serialize(new[] { Draft("первое"), Draft("второе") }));

            svc.Stop();

            Assert.Equal(2, queue.ReadFromDisk().Count);
        }

        /// <summary>
        /// Возврат на экран поднимает ретрай обратно, не перечитывая очередь: иначе
        /// сообщение, написанное и не отправленное минуту назад, разбиралось бы только
        /// после перезапуска лаунчера.
        /// </summary>
        [Fact]
        public void ВозвратНаЭкранВозобновляетРетрай() {
            using var queue = new QueueFileScope();
            var svc = NewService(Unreachable());
            svc.Start();
            svc.Stop();

            svc.Resume();

            var timer = RetryTimer(svc);
            Assert.NotNull(timer);
            Assert.True(timer!.IsEnabled, "ретрай не возобновился");
            svc.Stop();
        }

        /// <summary>
        /// Сам шов не должен переживать тест: после него очередь обязана снова лежать
        /// в настоящем %APPDATA%, иначе сообщения пользователя уедут во временный файл.
        /// </summary>
        [Fact]
        public void ПодменаФайлаОчередиЖивётТолькоВнутриТеста() {
            var real = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChillHub",
                "feedback_queue.json");

            using (var queue = new QueueFileScope()) {
                Assert.Equal(queue.Path, FeedbackService.QueuePath);
            }

            Assert.Equal(real, FeedbackService.QueuePath);
        }

        private static FeedbackService NewService(HttpMessageHandler handler)
            => new FeedbackService(new HttpClient(handler), () => "https://example.test", _ => { }, _ => { });

        /// <summary>
        /// Сервер недоступен: сообщение остаётся в очереди, а фоновый разбор ничего
        /// не пишет на диск — тест не зависит от того, когда он закончится.
        /// </summary>
        private static RecordingHandler Unreachable()
            => new RecordingHandler(_ => throw new HttpRequestException("сеть недоступна"));

        private static FeedbackService.FeedbackDraft Draft(string comment)
            => new FeedbackService.FeedbackDraft("Аноним", string.Empty, "bug", comment, false, null);

        /// <summary>Таймер фонового ретрая: наружу не выведен, а проверить Stop иначе нечем.</summary>
        private static DispatcherTimer? RetryTimer(FeedbackService svc)
            => (DispatcherTimer?)typeof(FeedbackService)
                .GetField("retryTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(svc);

        /// <summary>Уводит файл очереди во временный каталог на время теста.</summary>
        private sealed class QueueFileScope : IDisposable {
            private readonly TempDir dir = new TempDir();
            private readonly IDisposable seam;

            internal QueueFileScope() {
                this.Path = this.dir.PathTo("feedback_queue.json");
                this.seam = FeedbackService.OverrideQueuePathForTests(this.Path);
            }

            /// <summary>Файл, играющий роль feedback_queue.json.</summary>
            internal string Path { get; }

            internal void WriteRaw(string content)
                => File.WriteAllText(this.Path, content, new UTF8Encoding(false));

            /// <summary>Читает очередь так, как её прочитал бы следующий запуск лаунчера.</summary>
            internal List<FeedbackService.FeedbackDraft> ReadFromDisk()
                => JsonSerializer.Deserialize<List<FeedbackService.FeedbackDraft>>(
                    File.ReadAllText(this.Path, Encoding.UTF8)) ?? new List<FeedbackService.FeedbackDraft>();

            public void Dispose() {
                this.seam.Dispose();
                this.dir.Dispose();
            }
        }

        /// <summary>Подставной транспорт: отвечает по заданному правилу и запоминает тела запросов.</summary>
        private sealed class RecordingHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> reply;
            private readonly ConcurrentQueue<string> bodies = new();

            internal RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => this.reply = reply;

            /// <summary>Тела отправленных запросов в порядке отправки.</summary>
            internal IReadOnlyList<string> Bodies => this.bodies.ToArray();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
                if (request.Content != null) {
                    this.bodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                }

                return this.reply(request);
            }
        }
    }

    /// <summary>
    /// Тесты, подменяющие файл очереди, идут в одной коллекции: путь — состояние процесса,
    /// а классы xUnit по умолчанию выполняются параллельно.
    /// </summary>
    [CollectionDefinition(Name)]
    public class FeedbackQueueCollection {
        internal const string Name = "feedback-queue";
    }
}
