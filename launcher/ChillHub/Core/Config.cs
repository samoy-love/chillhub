// <copyright file="Config.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Windows;
    using System.Windows.Media;

    public class AppConfig {
        public string GamesPath { get; set; } = DefaultGamesPath();

        public int DownloadThreads { get; set; } = 8; // 2..16

        public string ApiBaseUrl { get; set; } = "https://launcher.samoy.love"; // base URL for server API/content

        public string LastGameId { get; set; } = string.Empty; // last launched game id

        // Автоматическая отправка отчётов об ошибках (необработанные исключения + диагностика).
        // По умолчанию true, чтобы не менять текущее поведение при обновлении.
        // На ручную отправку обратной связи не влияет.
        public bool AutoErrorReports { get; set; } = true;

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

        private static AppConfig cache = null!;

        /// <summary>
        /// Фактический путь к конфигу. Единственный источник правды: другие компоненты
        /// (например сбор диагностики) должны спрашивать его здесь, а не составлять путь заново.
        /// </summary>
        public static string ConfigFilePath => ConfigPath;

        public static AppConfig Load() {
            try {
                MigrateLegacyConfig();

                if (File.Exists(ConfigPath)) {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                    Clamp(cfg);
                    cache = cfg;
                    ApplyTheme();
                    return cfg;
                }
            }
            catch {
            }
            var def = new AppConfig();
            EnsureDir(Path.GetDirectoryName(def.GamesPath)!);
            cache = def;
            Save(def);
            return def;
        }

        public static void Save(AppConfig cfg) {
            try {
                Clamp(cfg);
                Directory.CreateDirectory(AppDir);
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                ApplyTheme();
            }
            catch {
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

        public static void EnsureDir(string path) {
            try {
                Directory.CreateDirectory(path);
            }
            catch {
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

            if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl)) {
                cfg.ApiBaseUrl = "https://launcher.samoy.love";
            }
        }

        public static AppConfig Current => cache ?? Load();

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
    }
}
