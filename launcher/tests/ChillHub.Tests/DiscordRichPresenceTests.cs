// <copyright file="DiscordRichPresenceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Коллекция для тестов, которые правят общие настройки в памяти.
    /// xunit гоняет классы параллельно, а <see cref="ConfigService.Current"/> один на процесс:
    /// без коллекции такие тесты видели бы чужие значения.
    /// </summary>
    [CollectionDefinition("НастройкиВПамяти")]
    public class InMemoryConfigCollection {
    }

    /// <summary>
    /// Статус «сейчас играет» в Discord.
    /// <para>
    /// Интеграция необязательная, поэтому проверяется в первую очередь то, что она
    /// НЕ делает: без Application ID, при выключенном тумблере и при ненайденном Discord
    /// она обязана молчать и ничего не ронять. Второе — формат кадра IPC: ошибка в длине
    /// или кодировке рассинхронизирует канал, и Discord перестаёт понимать клиента.
    /// </para>
    /// <para>
    /// К настоящему Discord тесты не ходят: методы, открывающие канал, здесь не вызываются.
    /// </para>
    /// </summary>
    [Collection("НастройкиВПамяти")]
    public class DiscordRichPresenceTests : IDisposable {
        private readonly string savedApplicationId = DiscordRichPresence.ApplicationId;
        private readonly bool savedUnavailable = DiscordRichPresence.Unavailable;
        private readonly bool savedToggle = ConfigService.Current.DiscordRichPresence;

        /// <summary>
        /// Application ID в исходниках не подставлен, поэтому интеграция считается ненастроенной.
        /// Если бы <see cref="DiscordRichPresence.IsConfigured"/> при пустом значении отвечал «да»,
        /// лаунчер на каждом запуске игры ходил бы по десяти каналам по 300 мс.
        /// </summary>
        [Fact]
        public void БезApplicationIdИнтеграцияНеНастроена() {
            Assert.False(DiscordRichPresence.IsConfigured);
            Assert.False(DiscordRichPresence.IsEnabled());
        }

        /// <summary>
        /// Ненастроенная интеграция обязана быть безвредной: запуск игры и выход из лаунчера
        /// не должны ни падать, ни ждать. Это состояние по умолчанию у всех пользователей.
        /// </summary>
        [Fact]
        public void НенастроеннаяИнтеграцияНичегоНеДелает() {
            DiscordRichPresence.SetPlaying("Lethal Company", "1.2.3");
            DiscordRichPresence.Clear();
            DiscordRichPresence.Shutdown();

            Assert.False(DiscordRichPresence.IsConfigured);
        }

        /// <summary>
        /// Application ID — snowflake, то есть строка из одних цифр. Отправить в handshake
        /// что угодно другое значит гарантированно получить разрыв соединения от Discord.
        /// </summary>
        /// <param name="id">Проверяемое значение.</param>
        /// <param name="valid">Ожидаемый вердикт.</param>
        [Theory]
        [InlineData("1234567890123456789", true)]
        [InlineData("0", true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("ВАШ_ID", false)]
        [InlineData("123456abc", false)]
        [InlineData("123 456", false)]
        [InlineData("-12345", false)]
        public void ApplicationIdПринимаетТолькоЦифры(string? id, bool valid) {
            Assert.Equal(valid, DiscordRichPresence.IsValidApplicationId(id));
        }

        /// <summary>
        /// Discord не найден — интеграция гасится до следующего старта лаунчера.
        /// Без этого флага каждый запуск игры снова перебирал бы десять каналов
        /// с таймаутом, то есть добавлял бы к запуску три секунды впустую.
        /// </summary>
        [Fact]
        public void НенайденныйDiscordБольшеНеОпрашивается() {
            DiscordRichPresence.ApplicationId = "1234567890123456789";
            DiscordRichPresence.Unavailable = false;
            Assert.True(DiscordRichPresence.IsEnabled());

            DiscordRichPresence.Unavailable = true;

            Assert.True(DiscordRichPresence.IsConfigured);
            Assert.False(DiscordRichPresence.IsEnabled());
        }

        /// <summary>
        /// Тумблер на странице настроек обязан отключать интеграцию полностью:
        /// пользователь, снявший галку, не должен видеть свою игру в Discord.
        /// </summary>
        [Fact]
        public void ВыключенныйТумблерОтключаетИнтеграцию() {
            DiscordRichPresence.ApplicationId = "1234567890123456789";
            DiscordRichPresence.Unavailable = false;

            ConfigService.Current.DiscordRichPresence = false;
            Assert.False(DiscordRichPresence.IsEnabled());

            ConfigService.Current.DiscordRichPresence = true;
            Assert.True(DiscordRichPresence.IsEnabled());
        }

        /// <summary>
        /// Кадр IPC — это опкод, длина тела и тело. Перепутанные местами числа
        /// или лишний байт в заголовке ломают протокол на первом же сообщении.
        /// </summary>
        [Fact]
        public async Task КадрНесётОпкодДлинуИТело() {
            const string json = "{\"v\":1}";
            using var stream = new MemoryStream();

            await DiscordRichPresence.WriteFrameAsync(stream, 1, json, CancellationToken.None);

            var bytes = stream.ToArray();
            Assert.Equal(8 + json.Length, bytes.Length);
            Assert.Equal(1, BitConverter.ToInt32(bytes, 0));
            Assert.Equal(json.Length, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(json, Encoding.UTF8.GetString(bytes, 8, bytes.Length - 8));
        }

        /// <summary>
        /// Опкод передаётся как есть: рукопожатие (0), команда (1) и закрытие (2)
        /// различаются только этим числом.
        /// </summary>
        /// <param name="opcode">Опкод кадра.</param>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task ОпкодУходитВЗаголовокБезИзменений(int opcode) {
            using var stream = new MemoryStream();

            await DiscordRichPresence.WriteFrameAsync(stream, opcode, "{}", CancellationToken.None);

            Assert.Equal(opcode, BitConverter.ToInt32(stream.ToArray(), 0));
        }

        /// <summary>
        /// Длина тела считается в БАЙТАХ UTF-8, а не в символах. Русское название игры
        /// или эмодзи в статусе дают длину больше числа символов, и стоит посчитать
        /// символы — Discord прочитает кадр не до конца, а остаток примет за начало
        /// следующего: канал рассинхронизируется до перезапуска.
        /// </summary>
        /// <param name="json">Тело кадра с многобайтными символами.</param>
        [Theory]
        [InlineData("{\"details\":\"Лютая Компания\"}")]
        [InlineData("{\"details\":\"Игра 🎮 онлайн\"}")]
        [InlineData("{\"details\":\"日本語\"}")]
        public async Task ДлинаКадраСчитаетсяВБайтахАНеВСимволах(string json) {
            using var stream = new MemoryStream();

            await DiscordRichPresence.WriteFrameAsync(stream, 1, json, CancellationToken.None);

            var bytes = stream.ToArray();
            var expected = Encoding.UTF8.GetByteCount(json);
            Assert.True(expected > json.Length, "тест должен проверять именно многобайтный текст");
            Assert.Equal(expected, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(8 + expected, bytes.Length);
            Assert.Equal(json, Encoding.UTF8.GetString(bytes, 8, bytes.Length - 8));
        }

        /// <summary>Ответ Discord вычитывается целиком: остаток в канале сдвинул бы следующий кадр.</summary>
        [Fact]
        public async Task ОтветВычитываетсяЦеликом() {
            var body = Encoding.UTF8.GetBytes("{\"evt\":\"READY\"}");
            using var stream = new MemoryStream(BuildFrame(1, body));

            await DiscordRichPresence.TryDrainAsync(stream, CancellationToken.None);

            Assert.Equal(stream.Length, stream.Position);
        }

        /// <summary>
        /// Заявленная длина ответа бессмысленна (ноль, отрицательная, гигантская) —
        /// читаем только заголовок. Иначе мусор в канале превращался бы в попытку
        /// выделить два гигабайта под тело.
        /// </summary>
        /// <param name="length">Длина тела из заголовка.</param>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData((256 * 1024) + 1)]
        public async Task НелепаяДлинаОтветаНеЧитаетТело(int length) {
            var header = new byte[8];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), 1);
            BitConverter.TryWriteBytes(header.AsSpan(4, 4), length);
            using var stream = new MemoryStream(header);

            await DiscordRichPresence.TryDrainAsync(stream, CancellationToken.None);

            Assert.Equal(8, stream.Position);
        }

        /// <summary>
        /// Канал закрылся посреди ответа. Читать нечего, но и виснуть на цикле чтения
        /// нельзя: выход из лаунчера ждёт эту операцию.
        /// </summary>
        [Fact]
        public async Task ОборванныйОтветНеВешаетЧтение() {
            var full = BuildFrame(1, Encoding.UTF8.GetBytes("{\"evt\":\"READY\"}"));
            using var stream = new MemoryStream(full, 0, full.Length - 5);

            await DiscordRichPresence.TryDrainAsync(stream, CancellationToken.None);

            Assert.Equal(stream.Length, stream.Position);
        }

        /// <summary>Ответа нет вовсе или он короче заголовка — это штатно, падать нельзя.</summary>
        /// <param name="available">Сколько байт успело прийти.</param>
        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(7)]
        public async Task КороткийОтветНеСчитаетсяОшибкой(int available) {
            using var stream = new MemoryStream(new byte[available]);

            await DiscordRichPresence.TryDrainAsync(stream, CancellationToken.None);

            Assert.True(stream.Position <= available);
        }

        /// <summary>
        /// В статус уходят название игры и версия сборки — ровно то, что видит
        /// пользователь в лаунчере, плюс ключ картинки из Art Assets.
        /// </summary>
        [Fact]
        public void АктивностьНесётНазваниеИСборку() {
            var json = JsonSerializer.Serialize(DiscordRichPresence.BuildActivity("Лютая Компания", "Сборка 1.2.3", null));

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Лютая Компания", doc.RootElement.GetProperty("details").GetString());
            Assert.Equal("Сборка 1.2.3", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("chillhub", doc.RootElement.GetProperty("assets").GetProperty("large_image").GetString());
        }

        /// <summary>
        /// Времени старта нет — счётчик не отправляется. Discord показал бы иначе
        /// отсчёт от начала эпохи, то есть «играет 56 лет».
        /// </summary>
        [Fact]
        public void БезВремениСтартаСчётчикНеОтправляется() {
            var json = JsonSerializer.Serialize(DiscordRichPresence.BuildActivity("Игра", "Играет через ChillHub", null));

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("timestamps").ValueKind);
        }

        /// <summary>Время старта уходит в unix-секундах — Discord других единиц не понимает.</summary>
        [Fact]
        public void ВремяСтартаУходитВUnixСекундах() {
            var started = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

            var json = JsonSerializer.Serialize(DiscordRichPresence.BuildActivity("Игра", "Сборка 1.0", started));

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(started.ToUnixTimeSeconds(), doc.RootElement.GetProperty("timestamps").GetProperty("start").GetInt64());
        }

        public void Dispose() {
            DiscordRichPresence.ApplicationId = this.savedApplicationId;
            DiscordRichPresence.Unavailable = this.savedUnavailable;

            // Только в памяти: ConfigService.Save записал бы это в настоящий config.json.
            ConfigService.Current.DiscordRichPresence = this.savedToggle;
            GC.SuppressFinalize(this);
        }

        /// <summary>Собирает кадр в том виде, в каком его присылает Discord.</summary>
        private static byte[] BuildFrame(int opcode, byte[] body) {
            var frame = new byte[8 + body.Length];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
            Buffer.BlockCopy(body, 0, frame, 8, body.Length);
            return frame;
        }
    }
}
