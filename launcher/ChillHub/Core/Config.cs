// <copyright file="Config.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Windows;
    using System.Windows.Media;

    public class AppConfig {
        /// <summary>Адрес сервера по умолчанию; он же — запасной вариант для отклонённого значения из конфига.</summary>
        public const string DefaultApiBaseUrl = "https://launcher.samoy.love";

        public string GamesPath { get; set; } = DefaultGamesPath();

        public int DownloadThreads { get; set; } = 8; // 2..16

        public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl; // base URL for server API/content

        public string LastGameId { get; set; } = string.Empty; // last launched game id

        // Автоматическая отправка отчётов об ошибках (необработанные исключения + диагностика).
        // По умолчанию true, чтобы не менять текущее поведение при обновлении.
        // На ручную отправку обратной связи не влияет.
        public bool AutoErrorReports { get; set; } = true;

        // Отправлять обезличенную статистику использования (запуски, установки, ошибки).
        // Персональных данных не содержит: ни имени пользователя, ни имени машины, ни путей —
        // см. Core/Metrics/MetricsService.cs. По умолчанию true, как и у отчётов об ошибках.
        public bool SendUsageMetrics { get; set; } = true;

        // Показывать в Discord статус «сейчас играет …» (Rich Presence).
        // По умолчанию true. Фактически интеграция работает только если владелец лаунчера
        // подставил Application ID в Core/DiscordRichPresence.cs — см. DiscordRichPresence.IsConfigured.
        public bool DiscordRichPresence { get; set; } = true;

        public static string DefaultGamesPath() {
            if (Directory.Exists(@"D:\")) {
                return @"D:\Games\ChillHub";
            }

            return @"C:\Games\ChillHub";
        }
    }

    public static class ConfigService {
        // Пользовательские данные держим в %APPDATA%\ChillHub (роуминг-состояние: очередь фидбэка, счётчики отчётов).
        // %LOCALAPPDATA%\ChillHub — это КАТАЛОГ УСТАНОВКИ лаунчера (там ChillHub.exe, *.dll, runtimes/),
        // поэтому конфиг оттуда попадал в пакет сборки и в манифест обновления -> вечный цикл самообновления.
        private static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");
        private static readonly string ConfigPath = Path.Combine(AppDir, "config.json");

        // Старое (унаследованное) расположение конфига — только для чтения при миграции.
        private static readonly string LegacyAppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChillHub");
        private static readonly string LegacyConfigPath = Path.Combine(LegacyAppDir, "config.json");

        // Конфиг читают и фоновые задачи (сеть, синхронизация), и UI. Без синхронизации
        // два одновременных промаха кеша запускали два Load, каждый со своей записью на
        // диск, а вызывающий мог увидеть недособранный объект.
        private static readonly object cacheLock = new object();

        private static AppConfig? cache;

        /// <summary>
        /// Фактический путь к конфигу. Единственный источник правды: другие компоненты
        /// (например сбор диагностики) должны спрашивать его здесь, а не составлять путь заново.
        /// </summary>
        public static string ConfigFilePath => ConfigPath;

        /// <summary>
        /// Читает конфиг с диска.
        /// Повреждённый JSON и недоступный файл — разные беды, и лечатся они по-разному:
        /// битый JSON чинить нечем (делаем бэкап и разворачиваем дефолты), а занятый или
        /// недоступный файл через секунду прочитается. Раньше оба случая ловил один пустой
        /// catch, который тут же ПЕРЕЗАПИСЫВАЛ конфиг дефолтами: достаточно было антивирусу
        /// подержать config.json открытым, чтобы пользователь потерял GamesPath и увидел
        /// «игры не установлены».
        /// </summary>
        /// <returns>Конфигурация приложения.</returns>
        public static AppConfig Load() {
            lock (cacheLock) {
                return LoadLocked();
            }
        }

        /// <summary>
        /// Сохраняет конфигурацию. Ошибку не глушит: возвращает false, чтобы вызывающий
        /// мог сказать пользователю правду. Раньше страница настроек рапортовала об успехе
        /// даже когда запись не удалась, и настройки молча терялись при перезапуске.
        /// </summary>
        /// <param name="cfg">Сохраняемая конфигурация.</param>
        /// <returns>true, если файл записан.</returns>
        public static bool Save(AppConfig cfg) => TrySave(cfg, out _);

        /// <summary>
        /// То же, что <see cref="Save"/>, но с текстом ошибки для показа пользователю.
        /// </summary>
        /// <param name="cfg">Сохраняемая конфигурация.</param>
        /// <param name="error">Описание сбоя; пустая строка при успехе.</param>
        /// <returns>true, если файл записан.</returns>
        public static bool TrySave(AppConfig cfg, out string error) {
            error = string.Empty;
            lock (cacheLock) {
                try {
                    Clamp(cfg);
                    Directory.CreateDirectory(AppDir);
                    var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(ConfigPath, json);

                    // Кеш обновляем только после удачной записи: иначе в памяти живут настройки,
                    // которых на диске нет, и после перезапуска они «откатываются» сами.
                    cache = cfg;
                    ApplyTheme();
                    return true;
                }
                catch (Exception ex) {
                    error = ex.Message;
                    Logging.Logger.Warn($"Config.Save: настройки не сохранены: {ex.Message}");
                    return false;
                }
            }
        }

        public static void EnsureDir(string path) {
            try {
                Directory.CreateDirectory(path);
            }
            catch {
            }
        }

        /// <summary>
        /// Текущая конфигурация. Обращаются и из UI, и из фоновых задач, поэтому промах
        /// кеша разрешается под замком: иначе два потока одновременно уходили в Load,
        /// и каждый писал на диск свою копию.
        /// </summary>
        public static AppConfig Current {
            get {
                var cached = Volatile.Read(ref cache);
                if (cached != null) {
                    return cached;
                }

                lock (cacheLock) {
                    return cache ?? LoadLocked();
                }
            }
        }

        /// <summary>
        /// Применяет единственную тёмную тему. Выбор темы из конфига убран — тема одна.
        /// </summary>
        public static void ApplyTheme() {
            try {
                var app = Application.Current;
                if (app == null) {
                    return;
                }

                // remove previous theme dictionaries
                for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--) {
                    var md = app.Resources.MergedDictionaries[i];
                    var src = md.Source?.OriginalString ?? string.Empty;
                    if (src.IndexOf("Themes/", StringComparison.OrdinalIgnoreCase) >= 0) {
                        app.Resources.MergedDictionaries.RemoveAt(i);
                    }
                }

                // Always use dark theme
                var uri = new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative);
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
                if (app.MainWindow != null) {
                    app.MainWindow.SetResourceReference(Window.BackgroundProperty, "Brush.Background");
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[Theme] ApplyTheme error: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверяет адрес сервера из config.json.
        /// <para>
        /// Подписи манифестов из формата убраны, поэтому единственное, что связывает
        /// скачанные файлы с их источником, — это TLS до нужного хоста: по этому адресу
        /// лаунчер берёт манифест самообновления и кладёт полученные файлы поверх
        /// ChillHub.exe. config.json лежит в %APPDATA% и правится чем угодно, работающим
        /// от имени пользователя, так что http:// здесь принимать нельзя.
        /// Исключение — петлевые адреса: локальный сервер разработки сетью не ходит,
        /// а без него отладка против localhost стала бы невозможной.
        /// </para>
        /// </summary>
        /// <param name="url">Значение из конфига.</param>
        /// <returns>true, если адрес можно использовать.</returns>
        internal static bool IsAcceptableApiBaseUrl(string? url) {
            if (string.IsNullOrWhiteSpace(url)) {
                return false;
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) {
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttps) {
                return true;
            }

            return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        }

        /// <summary>Тело <see cref="Load"/>; вызывается уже под замком кеша.</summary>
        private static AppConfig LoadLocked() {
            try {
                MigrateLegacyConfig();
            }
            catch (Exception ex) {
                // Logger.Warn, а не Error: Error поднимает ErrorReporter, который сам читает
                // конфиг — получили бы рекурсию ровно в момент, когда конфига ещё нет.
                Logging.Logger.Warn($"Config.Load: миграция не выполнена: {ex.Message}");
            }

            string json;
            try {
                if (!File.Exists(ConfigPath)) {
                    return CreateAndSaveDefaults();
                }

                json = File.ReadAllText(ConfigPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Файл занят или недоступен. Ничего не пишем на диск и ничего не кешируем:
                // следующее обращение попробует снова, а до тех пор отдаём последнее
                // известное состояние.
                Logging.Logger.Warn($"Config.Load: config.json недоступен, настройки не перезаписываем: {ex.Message}");
                return cache ?? new AppConfig();
            }

            try {
                var cfg = JsonSerializer.Deserialize<AppConfig>(json)
                          ?? throw new JsonException("config.json пуст");
                Clamp(cfg);
                cache = cfg;
                ApplyTheme();
                return cfg;
            }
            catch (JsonException ex) {
                // Содержимое испорчено — восстановить из него нечего. Сохраняем копию,
                // чтобы пользователь мог достать оттуда путь к играм, и разворачиваем дефолты.
                Logging.Logger.Warn($"Config.Load: config.json повреждён, откатываемся на значения по умолчанию: {ex.Message}");
                BackupCorruptedConfig();
                return CreateAndSaveDefaults();
            }
        }

        /// <summary>Разворачивает и сохраняет конфигурацию по умолчанию.</summary>
        private static AppConfig CreateAndSaveDefaults() {
            var def = new AppConfig();
            EnsureDir(Path.GetDirectoryName(def.GamesPath)!);
            cache = def;
            Save(def);
            return def;
        }

        /// <summary>Откладывает повреждённый конфиг в сторону: config.corrupted.json.</summary>
        private static void BackupCorruptedConfig() {
            try {
                var backup = Path.Combine(AppDir, "config.corrupted.json");
                File.Copy(ConfigPath, backup, overwrite: true);
                Logging.Logger.Warn($"Config.Load: копия повреждённого конфига сохранена как '{backup}'");
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"Config.Load: копию повреждённого конфига сделать не удалось: {ex.Message}");
            }
        }

        /// <summary>
        /// Одноразовый перенос config.json из %LOCALAPPDATA%\ChillHub в %APPDATA%\ChillHub.
        /// Идемпотентно: если новый файл уже есть — ничего не делает.
        /// Старый файл НЕ удаляем намеренно: его ещё может читать не обновившаяся версия лаунчера,
        /// и апдейтер держит config.json в списке --preserve. Удаление сделает откат/старый билд неработающим.
        /// Все ошибки (нет прав, файл занят) глушим — миграция не должна ломать запуск.
        /// </summary>
        private static void MigrateLegacyConfig() {
            try {
                if (File.Exists(ConfigPath)) {
                    return;
                }

                if (!File.Exists(LegacyConfigPath)) {
                    return;
                }

                var legacyJson = File.ReadAllText(LegacyConfigPath);

                // Проверяем, что старый файл вообще парсится: мусор переносить смысла нет.
                var probe = JsonSerializer.Deserialize<AppConfig>(legacyJson);
                if (probe == null) {
                    return;
                }

                Directory.CreateDirectory(AppDir);

                // Пишем уже нормализованную модель: устаревшие поля (напр. Theme) отбрасываются.
                Clamp(probe);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(probe, new JsonSerializerOptions { WriteIndented = true }));
                Debug.WriteLine("[Config] Мигрировали config.json из LOCALAPPDATA в APPDATA");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[Config] Ошибка миграции конфига: {ex.Message}");
            }
        }

        private static void Clamp(AppConfig cfg) {
            if (cfg.DownloadThreads < 2) {
                cfg.DownloadThreads = 2;
            }

            if (cfg.DownloadThreads > 16) {
                cfg.DownloadThreads = 16;
            }

            if (string.IsNullOrWhiteSpace(cfg.GamesPath)) {
                cfg.GamesPath = AppConfig.DefaultGamesPath();
            }

            if (!IsAcceptableApiBaseUrl(cfg.ApiBaseUrl)) {
                if (!string.IsNullOrWhiteSpace(cfg.ApiBaseUrl)) {
                    Logging.Logger.Warn($"Config: ApiBaseUrl '{cfg.ApiBaseUrl}' отклонён, используем {AppConfig.DefaultApiBaseUrl}");
                }

                cfg.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
            }
        }
    }
}
