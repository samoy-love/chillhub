// <copyright file="Config.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Windows;
    using System.Windows.Media;

    public class AppConfig {
        /// <summary>Адрес сервера по умолчанию; он же — запасной вариант для отклонённого значения из конфига.</summary>
        public const string DefaultApiBaseUrl = "https://launcher.samoy.love";

        public string GamesPath { get; set; } = DefaultGamesPath();

        public int DownloadThreads { get; set; } = 8; // 2..16

        // Ограничение скорости скачивания, МБ/с. 0 — без лимита.
        public int SpeedLimitMbps { get; set; } = 0; // 0..10

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

        // Сворачивать окно в трей вместо закрытия по крестику/Alt+F4. По умолчанию true —
        // полностью выйти можно через пункт «Выход» в меню значка в трее.
        public bool MinimizeToTray { get; set; } = true;

        // Подсказка «лаунчер продолжает работать в трее» уже показана. Показываем её один
        // раз за всё время, а не за сессию: тот, кто раз прочитал, второй раз не хочет.
        public bool TrayHintShown { get; set; }

        // Размер окна, который пользователь выставил сам. 0 — не трогал: тогда окно
        // открывается минимального размера (см. MainWindow.MinWidth/MinHeight), а не
        // произвольными 1180×760, которые на ноутбуке уходили за край экрана.
        // Развёрнутое окно запоминается как WindowMaximized, а не размерами.
        public double WindowWidth { get; set; }

        public double WindowHeight { get; set; }

        public bool WindowMaximized { get; set; }

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
        // Второй каталог — старое (унаследованное) расположение конфига, только для чтения при миграции.
        private static readonly ConfigStore DefaultStore = new ConfigStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChillHub"));

        // Подмена каталогов на время теста. AsyncLocal, а не обычное статическое поле:
        // классы xUnit по умолчанию идут параллельно, и глобальная подмена увела бы в
        // подставной каталог чужой тест, который в этот момент читает Current или
        // ConfigFilePath. Значение видно только внутри того потока выполнения, где его
        // выставили; в приложении оно всегда null, и работает DefaultStore.
        private static readonly AsyncLocal<ConfigStore?> ScopedStore = new AsyncLocal<ConfigStore?>();

        // Конфиг читают и фоновые задачи (сеть, синхронизация), и UI. Без синхронизации
        // два одновременных промаха кеша запускали два Load, каждый со своей записью на
        // диск, а вызывающий мог увидеть недособранный объект.
        private static readonly object cacheLock = new object();

        /// <summary>Действующее хранилище: подставленное тестом либо настоящее.</summary>
        private static ConfigStore Store => ScopedStore.Value ?? DefaultStore;

        private static string AppDir => Store.AppDir;

        private static string ConfigPath => Path.Combine(Store.AppDir, "config.json");

        private static string LegacyConfigPath => Path.Combine(Store.LegacyAppDir, "config.json");

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
                    Store.Cache = cfg;
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
                var store = Store;
                var cached = store.Cache;
                if (cached != null) {
                    return cached;
                }

                lock (cacheLock) {
                    return store.Cache ?? LoadLocked();
                }
            }
        }

        /// <summary>Путь словаря темы внутри сборки; тема одна — тёмная.</summary>
        public const string ThemePath = "Themes/Theme.Dark.xaml";

        /// <summary>
        /// Решает, что сделать со списком подключённых словарей ресурсов: какие убрать
        /// (чужие темы, индексы по убыванию — чтобы удалять с конца) и нужно ли добавлять
        /// нашу.
        /// <para>
        /// Тема одна и та же, а <see cref="ApplyTheme"/> вызывается на каждом сохранении
        /// конфига — в том числе на каждом шаге ползунка в настройках. Пересборка словаря
        /// ресурсов перестраивает шаблоны всех элементов окна: у ползунка это создаёт новый
        /// бегунок посреди перетаскивания, и мышь его теряет. Поэтому уже подключённый
        /// словарь не трогаем.
        /// </para>
        /// </summary>
        /// <param name="sources">Адреса подключённых словарей в их порядке.</param>
        /// <returns>Индексы словарей на удаление и признак, что тему нужно добавить.</returns>
        public static (IReadOnlyList<int> Remove, bool Add) PlanThemeMerge(IReadOnlyList<string> sources) {
            var remove = new List<int>();
            bool applied = false;
            for (int i = sources.Count - 1; i >= 0; i--) {
                var src = sources[i] ?? string.Empty;
                if (src.IndexOf(ThemePath, StringComparison.OrdinalIgnoreCase) >= 0) {
                    applied = true;
                }
                else if (src.IndexOf("Themes/", StringComparison.OrdinalIgnoreCase) >= 0) {
                    remove.Add(i);
                }
            }

            return (remove, !applied);
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

                var merged = app.Resources.MergedDictionaries;
                var (remove, add) = PlanThemeMerge(merged.Select(md => md.Source?.OriginalString ?? string.Empty).ToList());
                foreach (var i in remove) {
                    merged.RemoveAt(i);
                }

                if (add) {
                    merged.Add(new ResourceDictionary { Source = new Uri("/ChillHub;component/" + ThemePath, UriKind.Relative) });
                }

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

        /// <summary>
        /// Уводит чтение и запись конфига во временные каталоги на время теста.
        /// <para>
        /// Без этого шва проверить <see cref="Load"/> и <see cref="TrySave"/> нечем: они ходят
        /// в настоящий %APPDATA%\ChillHub\config.json, и тест затирал бы рабочие настройки
        /// разработчика — ровно то, от чего эти методы и защищают пользователя.
        /// </para>
        /// <para>
        /// Подставленное хранилище живёт со своим кешем и видно только тому потоку
        /// выполнения, который его выставил: параллельные классы тестов продолжают
        /// работать с настоящим конфигом.
        /// </para>
        /// </summary>
        /// <param name="appDir">Каталог, играющий роль %APPDATA%\ChillHub.</param>
        /// <param name="legacyAppDir">Каталог, играющий роль %LOCALAPPDATA%\ChillHub.</param>
        /// <returns>Объект, возвращающий конфиг к настоящим каталогам.</returns>
        internal static IDisposable OverrideForTests(string appDir, string legacyAppDir)
            => new TestOverride(appDir, legacyAppDir);

        /// <summary>
        /// Забывает прочитанное, чтобы следующее обращение сходило на диск.
        /// Нужно тесту, который подменил содержимое файла у работающего сервиса.
        /// </summary>
        internal static void InvalidateCache() {
            lock (cacheLock) {
                Store.Cache = null;
            }
        }

        /// <summary>Тело <see cref="Load"/>; вызывается уже под замком кеша.</summary>
        private static AppConfig LoadLocked() {
            try {
                MigrateLegacyConfig();
            }
            catch (Exception ex) {
                // Все ошибки миграции (нет прав, файл занят, диск полон) глушим здесь:
                // миграция не должна ломать запуск, чтение конфига продолжается ниже.
                // Но глушим ГРОМКО: потерянные настройки пользователя обязаны оставить
                // след, иначе разбирать жалобу «настройки сбросились» не по чему.
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
                return Store.Cache ?? new AppConfig();
            }

            try {
                var cfg = JsonSerializer.Deserialize<AppConfig>(json)
                          ?? throw new JsonException("config.json пуст");
                Clamp(cfg);
                Store.Cache = cfg;
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
            Store.Cache = def;
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
        /// Ошибки не глушит: их ловит и записывает в журнал вызывающий (<see cref="LoadLocked"/>).
        /// Пока перенос гасил их сам, сорвавшаяся миграция не оставляла в client.log ни строки,
        /// и потерянные настройки выглядели как «сбросилось само».
        /// </summary>
        private static void MigrateLegacyConfig() {
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

        /// <summary>
        /// Приводит прочитанный конфиг к пригодному виду: чинит число потоков, пустой путь
        /// к играм и отклоняет неприемлемый адрес сервера. Чистая функция над переданным
        /// объектом — диска не касается, поэтому проверяется напрямую.
        /// </summary>
        /// <param name="cfg">Конфигурация, которую нужно нормализовать на месте.</param>
        internal static void Clamp(AppConfig cfg) {
            if (cfg.DownloadThreads < 2) {
                cfg.DownloadThreads = 2;
            }

            if (cfg.DownloadThreads > 16) {
                cfg.DownloadThreads = 16;
            }

            if (cfg.SpeedLimitMbps < 0) {
                cfg.SpeedLimitMbps = 0;
            }

            if (cfg.SpeedLimitMbps > 10) {
                cfg.SpeedLimitMbps = 10;
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

        /// <summary>
        /// Каталоги, откуда конфиг читается и куда пишется, вместе с кешем прочитанного.
        /// Кеш лежит здесь, а не рядом с путями: подменённое хранилище не должно делить
        /// закешированную конфигурацию с настоящим %APPDATA%.
        /// </summary>
        private sealed class ConfigStore {
            private AppConfig? cached;

            internal ConfigStore(string appDir, string legacyAppDir) {
                this.AppDir = appDir;
                this.LegacyAppDir = legacyAppDir;
            }

            /// <summary>Каталог с config.json и копией повреждённого конфига.</summary>
            internal string AppDir { get; }

            /// <summary>Каталог, из которого конфиг переносится при миграции.</summary>
            internal string LegacyAppDir { get; }

            /// <summary>Последняя удачно прочитанная или записанная конфигурация; null — ещё не читали.</summary>
            internal AppConfig? Cache {
                get => Volatile.Read(ref this.cached);
                set => Volatile.Write(ref this.cached, value);
            }
        }

        /// <summary>Возвращает конфиг к настоящим каталогам после <see cref="OverrideForTests"/>.</summary>
        private sealed class TestOverride : IDisposable {
            private readonly ConfigStore? previous;

            internal TestOverride(string appDir, string legacyAppDir) {
                this.previous = ScopedStore.Value;
                ScopedStore.Value = new ConfigStore(appDir, legacyAppDir);
            }

            public void Dispose() => ScopedStore.Value = this.previous;
        }
    }
}
