// <copyright file="MetricsService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Metrics {
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Net;

    /// <summary>
    /// Отправка обезличенной статистики использования на <c>/metrics/report</c>.
    /// <para>
    /// Что уходит: тип события, версия лаунчера, версия ОС, идентификатор игры и
    /// сборки, результат, длительность, объём загрузки, код ошибки. Ни имени
    /// пользователя, ни имени машины, ни путей — сервер и так отбрасывает все
    /// поля, которых нет в его списке.
    /// </para>
    /// <para>
    /// Отправка всегда «выстрелил и забыл»: метрика не должна ни задерживать
    /// интерфейс, ни тем более ломать сценарий, ради которого её собирают.
    /// Любая ошибка сети глушится — при недоступном сервере пользователь просто
    /// не должен ничего заметить.
    /// </para>
    /// </summary>
    public static class MetricsService {
        /// <summary>Путь эндпоинта относительно <c>ApiBaseUrl</c>.</summary>
        public const string EndpointPath = "/metrics/report";

        /// <summary>
        /// Переменная окружения для отладочных и автоматических запусков, чтобы
        /// они не пачкали статистику. Значение "0" глушит отправку целиком,
        /// значение "test" оставляет её включённой, но помечает события
        /// служебными: сервер принимает их как обычно и не считает игроками.
        /// </summary>
        public const string EnvVar = "CHILLHUB_METRICS";

        /// <summary>
        /// Префикс идентификатора служебной установки. Сервер узнаёт по нему
        /// свой же прогон и не учитывает его ни в одной цифре админки
        /// (server/internal/metrics/synthetic.go).
        /// <para>
        /// Признак живёт в самом событии, а не только в решении «слать или не
        /// слать»: прогон, которому отправку заглушили, перестаёт проверять и
        /// приём событий, а уже отправленные события задним числом не
        /// перекрасишь.
        /// </para>
        /// </summary>
        public const string TestInstallIdPrefix = "test-";

        // Метрика ценна в объёме, а не поштучно: ждать её дольше пары секунд
        // бессмысленно — событие уже устарело, а сокет держать незачем.
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

        // Идентификатор установки живёт в %APPDATA%, а НЕ в каталоге установки.
        // Файл в каталоге установки попал бы в пакет сборки и в манифест
        // обновления — ровно так лаунчер и получил вечный цикл самообновления
        // на config.json и launcher.version.
        private static readonly string StateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");

        private static readonly string InstallIdPath = Path.Combine(StateDir, "install-id");

        private static readonly Lazy<string> InstallIdLazy = new Lazy<string>(LoadOrCreateInstallId);

        /// <summary>
        /// Gets анонимный идентификатор установки: случайный GUID, созданный один
        /// раз. Нужен, чтобы отличать «сто запусков у одного» от «по одному у ста»;
        /// с личностью пользователя не связан никак.
        /// </summary>
        public static string InstallId => InstallIdLazy.Value;

        /// <summary>
        /// Gets a value indicating whether прогон объявил себя служебным
        /// (<c>CHILLHUB_METRICS=test</c>): автотест, отладочный или ручной
        /// запуск, который не должен попадать в статистику как игрок.
        /// </summary>
        public static bool Synthetic
            => Environment.GetEnvironmentVariable(EnvVar)?.Trim() == "test";

        /// <summary>
        /// Gets идентификатор, который действительно уходит на сервер: у
        /// служебного прогона — с префиксом <see cref="TestInstallIdPrefix"/>,
        /// у обычного запуска — <see cref="InstallId"/> как есть.
        /// </summary>
        public static string ReportedInstallId
            => Synthetic ? TestInstallIdPrefix + InstallId : InstallId;

        /// <summary>Gets a value indicating whether отправка разрешена.</summary>
        public static bool Enabled {
            get {
                if (Environment.GetEnvironmentVariable(EnvVar)?.Trim() == "0") {
                    return false;
                }

                return true;
            }
        }

        /// <summary>Запуск лаунчера.</summary>
        public static void LauncherStart() => Report("launcher_start");

        /// <summary>
        /// Установка игры с нуля.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия сборки.</param>
        /// <param name="result">ok, fail или cancel.</param>
        /// <param name="durationMs">Длительность в миллисекундах.</param>
        /// <param name="bytes">Скачано байт.</param>
        /// <param name="filesDownloaded">Скачано файлов.</param>
        /// <param name="filesTotal">Файлов в сборке целиком.</param>
        /// <param name="fullBytes">Вес сборки целиком.</param>
        public static void GameInstall(
            string? gameId, string? version, string result, long durationMs, long bytes,
            long filesDownloaded = 0, long filesTotal = 0, long fullBytes = 0)
            => Report(
                "game_install", gameId, version, result, durationMs, bytes,
                filesDownloaded: filesDownloaded, filesTotal: filesTotal, fullBytes: fullBytes);

        /// <summary>
        /// Обновление уже установленной игры.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия сборки.</param>
        /// <param name="result">ok, fail или cancel.</param>
        /// <param name="durationMs">Длительность в миллисекундах.</param>
        /// <param name="bytes">Скачано байт.</param>
        /// <param name="filesDownloaded">Скачано файлов.</param>
        /// <param name="filesTotal">Файлов в сборке целиком.</param>
        /// <param name="fullBytes">Вес сборки целиком.</param>
        /// <remarks>
        /// Пары «скачано / всего» отправляются именно здесь, а не считаются на
        /// сервере: сервер видит только запросы за файлами и не знает, сколько
        /// файлов у пользователя УЖЕ совпало с манифестом — а вся экономия
        /// лаунчера состоит ровно из них.
        /// </remarks>
        public static void GameUpdate(
            string? gameId, string? version, string result, long durationMs, long bytes,
            long filesDownloaded = 0, long filesTotal = 0, long fullBytes = 0)
            => Report(
                "game_update", gameId, version, result, durationMs, bytes,
                filesDownloaded: filesDownloaded, filesTotal: filesTotal, fullBytes: fullBytes);

        /// <summary>
        /// Проверка целостности установленной игры.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Проверяемая версия.</param>
        /// <param name="ok">Все файлы на месте и совпали.</param>
        /// <param name="filesTotal">Файлов в сборке.</param>
        /// <param name="hashMismatches">Файлов с разошедшимся хешем.</param>
        public static void IntegrityCheck(
            string? gameId, string? version, bool ok, long filesTotal, long hashMismatches)
            => Report(
                "integrity_check", gameId, version, ok ? "ok" : "fail",
                filesTotal: filesTotal, hashMismatches: hashMismatches);

        /// <summary>
        /// Запуск игры.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия сборки.</param>
        public static void GameLaunch(string? gameId, string? version)
            => Report("game_launch", gameId, version, "ok");

        /// <summary>
        /// Завершение игровой сессии: игра была запущена и её процесс закрылся.
        /// Уходит одним событием с итоговой длительностью, а не тиками по ходу
        /// сессии — так же, как <see cref="Game.PlaytimeStore"/> считает наигранное
        /// время локально.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="durationMs">Длительность сессии в миллисекундах.</param>
        public static void GameSession(string? gameId, long durationMs)
            => Report("game_session", gameId, null, "ok", durationMs);

        /// <summary>
        /// Ошибка. Код классифицирует проблему и не должен содержать текста
        /// исключения: там встречаются пути и имена файлов пользователя.
        /// </summary>
        /// <param name="errorCode">Короткий код вида <c>sync_hash_mismatch</c>.</param>
        /// <param name="gameId">Идентификатор игры, если применимо.</param>
        public static void Error(string errorCode, string? gameId = null)
            => Report("error", gameId, null, "fail", 0, 0, errorCode);

        /// <summary>
        /// Отправляет одно событие. Никогда не бросает исключений и не ждёт результата.
        /// </summary>
        /// <param name="kind">Тип события из списка сервера.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия сборки.</param>
        /// <param name="result">ok, fail или cancel.</param>
        /// <param name="durationMs">Длительность в миллисекундах.</param>
        /// <param name="bytes">Скачано байт.</param>
        /// <param name="errorCode">Код ошибки.</param>
        /// <param name="filesDownloaded">Скачано файлов.</param>
        /// <param name="filesTotal">Файлов в сборке целиком.</param>
        /// <param name="fullBytes">Вес сборки целиком.</param>
        /// <param name="hashMismatches">Файлов с разошедшимся хешем.</param>
        public static void Report(
            string kind,
            string? gameId = null,
            string? version = null,
            string? result = null,
            long durationMs = 0,
            long bytes = 0,
            string? errorCode = null,
            long filesDownloaded = 0,
            long filesTotal = 0,
            long fullBytes = 0,
            long hashMismatches = 0) {
            if (!Enabled) {
                return;
            }

            // Тело собираем синхронно: значения конфига могут измениться, пока
            // задача ждёт своей очереди в пуле.
            string json;
            string url;
            try {
                var baseUrl = (ConfigService.Current.ApiBaseUrl ?? string.Empty).TrimEnd('/');
                if (baseUrl.Length == 0) {
                    return;
                }

                url = baseUrl + EndpointPath;
                json = JsonSerializer.Serialize(new {
                    installId = ReportedInstallId,
                    @event = kind,
                    appVersion = AppVersion(),
                    os = Environment.OSVersion.VersionString,
                    gameId = gameId ?? string.Empty,
                    version = version ?? string.Empty,
                    result = result ?? string.Empty,
                    durationMs,
                    bytes,
                    errorCode = errorCode ?? string.Empty,
                    filesDownloaded,
                    filesTotal,
                    fullBytes,
                    hashMismatches,
                });
            }
            catch {
                return;
            }

            _ = Task.Run(async () => {
                try {
                    using var cts = new CancellationTokenSource(SendTimeout);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await HttpClientProvider.Shared
                        .PostAsync(url, content, cts.Token).ConfigureAwait(false);

                    // Ответ не разбираем: единственное, что клиент мог бы с ним
                    // сделать, — повторить отправку, а повтор метрики хуже её потери.
                }
                catch {
                    // Метрика не стоит ни одной строки в логе пользователя.
                }
            });
        }

        private static string AppVersion() {
            try {
                return typeof(MetricsService).Assembly.GetName().Version?.ToString() ?? string.Empty;
            }
            catch {
                return string.Empty;
            }
        }

        private static string LoadOrCreateInstallId() {
            try {
                if (File.Exists(InstallIdPath)) {
                    var existing = File.ReadAllText(InstallIdPath).Trim();
                    if (Guid.TryParse(existing, out var parsed)) {
                        return parsed.ToString("N");
                    }
                }

                var id = Guid.NewGuid();
                Directory.CreateDirectory(StateDir);
                File.WriteAllText(InstallIdPath, id.ToString());
                return id.ToString("N");
            }
            catch {
                // Не смогли сохранить — работаем без идентификатора. Пустая строка
                // лучше, чем новый GUID на каждый запуск: тот раздул бы счётчик
                // уникальных установок до числа запусков.
                return string.Empty;
            }
        }
    }
}
