// <copyright file="DiscordRichPresence.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System;
    using System.IO;
    using System.IO.Pipes;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Минимальный клиент Discord IPC для Rich Presence («сейчас играет …»).
    /// <para>
    /// Внешних зависимостей нет намеренно: Discord слушает именованный канал
    /// <c>\\.\pipe\discord-ipc-N</c> (N = 0..9), протокол — заголовок из двух
    /// 32-битных чисел (opcode и длина тела, little-endian) и тело в виде JSON.
    /// </para>
    /// <para>
    /// Всё строго опционально. Discord не запущен, канал недоступен, протокол
    /// изменился — молча ничего не делаем и пишем в лог. Пользователь ошибок не видит,
    /// UI не блокируется: любая работа с каналом идёт в фоне и с таймаутами.
    /// </para>
    /// </summary>
    public static class DiscordRichPresence {
        /// <summary>
        /// Application ID приложения Discord.
        /// <para>
        /// ВЛАДЕЛЬЦУ: заведите своё приложение на https://discord.com/developers/applications,
        /// скопируйте «Application ID» и подставьте его сюда строкой из цифр.
        /// Пока значение пустое (или не состоит из цифр), Rich Presence просто не активируется —
        /// ни ошибок, ни задержек в лаунчере это не вызывает.
        /// </para>
        /// <para>
        /// Там же, в разделе Rich Presence → Art Assets, можно загрузить картинку с ключом
        /// <see cref="LargeImageKey"/> — она будет показана рядом со статусом.
        /// </para>
        /// </summary>
        private const string DefaultApplicationId = "";

        /// <summary>Ключ большой картинки из Art Assets приложения Discord. Если такой картинки нет — Discord просто её не покажет.</summary>
        private const string LargeImageKey = "chillhub";

        /// <summary>Сколько ждём подключения к одному каналу discord-ipc-N.</summary>
        private const int ConnectTimeoutMs = 300;

        /// <summary>Общий таймаут на одну операцию (подключение + handshake + отправка).</summary>
        private const int OperationTimeoutMs = 3000;

        /// <summary>Discord держит до десяти каналов: клиент, PTB и Canary могут занимать разные индексы.</summary>
        private const int MaxPipeIndex = 10;

        /// <summary>Опкод рукопожатия.</summary>
        private const int OpHandshake = 0;

        /// <summary>Опкод обычного кадра с командой.</summary>
        private const int OpFrame = 1;

        /// <summary>Опкод корректного закрытия соединения.</summary>
        private const int OpClose = 2;

        /// <summary>Операции сериализуем: одновременных записей в канал быть не должно.</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static NamedPipeClientStream? pipe;

        /// <summary>Повторно не долбимся в Discord, если уже выяснили, что его нет.</summary>
        private static bool unavailable;

        /// <summary>Время старта текущей игровой сессии — Discord показывает по нему счётчик времени.</summary>
        private static DateTimeOffset? sessionStartedAt;

        /// <summary>Включена ли интеграция (настройка владельца) и задан ли валидный Application ID.</summary>
        public static bool IsConfigured => IsValidApplicationId(ApplicationId);

        /// <summary>
        /// Gets or sets действующий Application ID. В приложении это всегда
        /// <see cref="DefaultApplicationId"/>: значение выведено в свойство только затем,
        /// чтобы ветки, доступные лишь настроенной интеграции, можно было проверить,
        /// не подменяя константу сборки.
        /// </summary>
        internal static string ApplicationId { get; set; } = DefaultApplicationId;

        /// <summary>
        /// Gets or sets a value indicating whether Discord признан недоступным.
        /// Флаг выставляется один раз за запуск и глушит дальнейшие попытки достучаться до канала.
        /// </summary>
        internal static bool Unavailable {
            get => unavailable;
            set => unavailable = value;
        }

        /// <summary>
        /// Сообщает Discord, что пользователь запустил игру. Вызов не блокирует UI:
        /// вся работа уходит в фоновую задачу, ошибки гасятся и пишутся в лог.
        /// </summary>
        /// <param name="gameTitle">Название игры, как оно показано в лаунчере.</param>
        /// <param name="version">Установленная версия сборки (может быть пустой).</param>
        public static void SetPlaying(string? gameTitle, string? version = null) {
            if (!IsEnabled()) {
                return;
            }

            var title = string.IsNullOrWhiteSpace(gameTitle) ? "Игра" : gameTitle.Trim();
            var state = string.IsNullOrWhiteSpace(version) ? "Играет через ChillHub" : $"Сборка {version.Trim()}";
            sessionStartedAt = DateTimeOffset.UtcNow;

            _ = Task.Run(async () => {
                try {
                    await SendActivityAsync(BuildActivity(title, state, sessionStartedAt)).ConfigureAwait(false);
                }
                catch (Exception ex) {
                    // Rich Presence — необязательная функция: молчим для пользователя, фиксируем в логе
                    Logging.Logger.Warn($"DiscordRichPresence.SetPlaying: {ex.Message}");
                }
            });
        }

        /// <summary>Убирает статус из Discord (например, при выходе из лаунчера).</summary>
        public static void Clear() {
            if (!IsEnabled() || pipe == null) {
                return;
            }

            sessionStartedAt = null;
            _ = Task.Run(async () => {
                try {
                    await SendActivityAsync(null).ConfigureAwait(false);
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"DiscordRichPresence.Clear: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Синхронно очищает статус и закрывает канал. Рассчитан на выход из приложения:
        /// ждём не дольше <see cref="OperationTimeoutMs"/>, чтобы не задерживать закрытие лаунчера.
        /// </summary>
        public static void Shutdown() {
            try {
                if (pipe == null) {
                    return;
                }

                var task = Task.Run(async () => {
                    await SendActivityAsync(null).ConfigureAwait(false);
                    await CloseAsync().ConfigureAwait(false);
                });
                task.Wait(OperationTimeoutMs);
            }
            catch (Exception ex) {
                // Закрытие лаунчера важнее корректного прощания с Discord
                Logging.Logger.Warn($"DiscordRichPresence.Shutdown: {ex.Message}");
            }
        }

        /// <summary>
        /// Включена ли интеграция. Учитывает Application ID и пользовательскую настройку.
        /// </summary>
        /// <returns>True, если можно пытаться работать с Discord.</returns>
        internal static bool IsEnabled() {
            if (!IsConfigured) {
                return false;
            }

            if (unavailable) {
                return false;
            }

            return IsEnabledByConfig();
        }

        /// <summary>Application ID Discord — это строка из цифр (snowflake).</summary>
        internal static bool IsValidApplicationId(string? id) {
            if (string.IsNullOrWhiteSpace(id)) {
                return false;
            }

            foreach (var ch in id) {
                if (ch < '0' || ch > '9') {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Формирует объект activity протокола Discord. null означает «убрать статус».</summary>
        internal static object? BuildActivity(string details, string state, DateTimeOffset? startedAt) {
            object? timestamps = startedAt.HasValue
                ? new { start = startedAt.Value.ToUnixTimeSeconds() }
                : null;

            return new {
                details,
                state,
                timestamps,
                assets = new { large_image = LargeImageKey, large_text = "ChillHub" },
            };
        }

        /// <summary>
        /// Пишет в канал один кадр протокола: opcode, длина тела и само тело.
        /// Длина считается В БАЙТАХ UTF-8 — на символах Discord сразу теряет синхронизацию.
        /// </summary>
        internal static async Task WriteFrameAsync(Stream stream, int opcode, string json, CancellationToken token) {
            var body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[8 + body.Length];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
            Buffer.BlockCopy(body, 0, frame, 8, body.Length);

            // Протокол little-endian; на big-endian машине порядок надо развернуть.
            if (!BitConverter.IsLittleEndian) {
                Array.Reverse(frame, 0, 4);
                Array.Reverse(frame, 4, 4);
            }

            await stream.WriteAsync(frame.AsMemory(0, frame.Length), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        /// <summary>Вычитывает один ответный кадр, если он есть. Содержимое нам не нужно.</summary>
        internal static async Task TryDrainAsync(Stream stream, CancellationToken token) {
            try {
                var header = new byte[8];
                var read = await stream.ReadAsync(header.AsMemory(0, 8), token).ConfigureAwait(false);
                if (read < 8) {
                    return;
                }

                if (!BitConverter.IsLittleEndian) {
                    Array.Reverse(header, 0, 4);
                    Array.Reverse(header, 4, 4);
                }

                var length = BitConverter.ToInt32(header, 4);

                // Защита от мусора в канале: разумный ответ Discord не бывает больше пары сотен КБ
                if (length <= 0 || length > 256 * 1024) {
                    return;
                }

                var body = new byte[length];
                var offset = 0;
                while (offset < length) {
                    var chunk = await stream.ReadAsync(body.AsMemory(offset, length - offset), token).ConfigureAwait(false);
                    if (chunk <= 0) {
                        break;
                    }

                    offset += chunk;
                }
            }
            catch (Exception ex) {
                // Ответ не обязателен: статус мог уже примениться
                Logging.Logger.Info($"DiscordRichPresence: ответ не прочитан ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// Читает пользовательский флаг «Discord Rich Presence» из настроек
        /// (<see cref="AppConfig.DiscordRichPresence"/>, переключатель — на странице настроек).
        /// Конфиг недоступен — считаем, что интеграция включена: это поведение по умолчанию.
        /// </summary>
        /// <returns>True, если пользователь не отключил интеграцию.</returns>
        private static bool IsEnabledByConfig() {
            try {
                return ConfigService.Current?.DiscordRichPresence ?? true;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"DiscordRichPresence: чтение флага из настроек не удалось: {ex.Message}");
                return true;
            }
        }

        private static async Task SendActivityAsync(object? activity) {
            using var timeout = new CancellationTokenSource(OperationTimeoutMs);
            var token = timeout.Token;

            if (!await Gate.WaitAsync(OperationTimeoutMs, token).ConfigureAwait(false)) {
                Logging.Logger.Warn("DiscordRichPresence: предыдущая операция не завершилась, статус пропущен");
                return;
            }

            try {
                var stream = await EnsureConnectedAsync(token).ConfigureAwait(false);
                if (stream == null) {
                    return;
                }

                var payload = new {
                    cmd = "SET_ACTIVITY",
                    nonce = Guid.NewGuid().ToString("N"),
                    args = new {
                        pid = Environment.ProcessId,
                        activity,
                    },
                };

                await WriteFrameAsync(stream, OpFrame, JsonSerializer.Serialize(payload), token).ConfigureAwait(false);

                // Ответ вычитываем и выбрасываем: держать его незабранным в канале нельзя
                await TryDrainAsync(stream, token).ConfigureAwait(false);
            }
            catch (Exception ex) {
                // Битое соединение переоткроем при следующей попытке
                Logging.Logger.Warn($"DiscordRichPresence.SendActivity: {ex.Message}");
                await CloseAsync().ConfigureAwait(false);
            }
            finally {
                Gate.Release();
            }
        }

        /// <summary>Подключается к каналу и выполняет рукопожатие. null — Discord недоступен.</summary>
        private static async Task<NamedPipeClientStream?> EnsureConnectedAsync(CancellationToken token) {
            var existing = pipe;
            if (existing is { IsConnected: true }) {
                return existing;
            }

            for (var i = 0; i < MaxPipeIndex; i++) {
                var name = $"discord-ipc-{i}";
                NamedPipeClientStream? candidate = null;
                try {
                    candidate = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await candidate.ConnectAsync(ConnectTimeoutMs, token).ConfigureAwait(false);

                    var handshake = JsonSerializer.Serialize(new { v = 1, client_id = ApplicationId });
                    await WriteFrameAsync(candidate, OpHandshake, handshake, token).ConfigureAwait(false);
                    await TryDrainAsync(candidate, token).ConfigureAwait(false);

                    pipe = candidate;
                    Logging.Logger.Info($"DiscordRichPresence: подключились к каналу '{name}'");
                    return candidate;
                }
                catch (Exception ex) {
                    try {
                        candidate?.Dispose();
                    }
                    catch (Exception disposeEx) {
                        Logging.Logger.Warn($"DiscordRichPresence: не удалось освободить канал '{name}': {disposeEx.Message}");
                    }

                    // Это штатная ситуация: канала с таким индексом просто нет
                    Logging.Logger.Info($"DiscordRichPresence: канал '{name}' недоступен ({ex.GetType().Name})");
                }

                if (token.IsCancellationRequested) {
                    break;
                }
            }

            // Discord не запущен — больше не пробуем до следующего старта лаунчера
            unavailable = true;
            Logging.Logger.Info("DiscordRichPresence: Discord не найден, интеграция не активируется");
            return null;
        }

        private static async Task CloseAsync() {
            var current = pipe;
            pipe = null;
            if (current == null) {
                return;
            }

            try {
                if (current.IsConnected) {
                    using var cts = new CancellationTokenSource(ConnectTimeoutMs);
                    await WriteFrameAsync(current, OpClose, "{}", cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) {
                Logging.Logger.Info($"DiscordRichPresence: закрытие канала без прощания ({ex.GetType().Name})");
            }

            try {
                current.Dispose();
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"DiscordRichPresence: Dispose канала: {ex.Message}");
            }
        }
    }
}
