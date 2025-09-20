using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace ChillHub.Core
{
    public class AppConfig
    {
        public string GamesPath { get; set; } = DefaultGamesPath();
        public int DownloadThreads { get; set; } = 8; // 2..16
        public string Theme { get; set; } = "dark"; // light | dark (default: dark)
        public string ApiBaseUrl { get; set; } = "http://localhost:55700"; // base URL for server API/content
        public string LastGameId { get; set; } = string.Empty; // last launched game id

        public static string DefaultGamesPath()
        {
            var dDrive = Path.GetPathRoot(@"D:\")?.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(@"D:\"))
            {
                return @"D:\\Games\\ChillHub";
            }
            return @"C:\\Games\\ChillHub";
        }
    }

    public static class ConfigService
    {
        private static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChillHub");
        private static readonly string ConfigPath = Path.Combine(AppDir, "config.json");
        private static AppConfig _cache = null!;

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                    Clamp(cfg);
                    _cache = cfg;
                    ApplyTheme(cfg.Theme);
                    return cfg;
                }
            }
            catch { }
            var def = new AppConfig();
            EnsureDir(Path.GetDirectoryName(def.GamesPath)!);
            _cache = def;
            Save(def);
            return def;
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                Clamp(cfg);
                Directory.CreateDirectory(AppDir);
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                ApplyTheme(cfg.Theme);
            }
            catch { }
        }

        public static void EnsureDir(string path)
        {
            try { Directory.CreateDirectory(path); } catch { }
        }

        private static void Clamp(AppConfig cfg)
        {
            if (cfg.DownloadThreads < 2) cfg.DownloadThreads = 2;
            if (cfg.DownloadThreads > 16) cfg.DownloadThreads = 16;
            if (string.IsNullOrWhiteSpace(cfg.GamesPath)) cfg.GamesPath = AppConfig.DefaultGamesPath();
            if (string.IsNullOrWhiteSpace(cfg.Theme)) cfg.Theme = "dark";
            cfg.Theme = (cfg.Theme.Equals("dark", StringComparison.OrdinalIgnoreCase)) ? "dark" : "light";
            if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl)) cfg.ApiBaseUrl = "http://localhost:55700";
        }

        public static AppConfig Current => _cache ?? Load();

        public static void ApplyTheme(string theme)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;
                // remove previous theme dictionaries
                for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
                {
                    var md = app.Resources.MergedDictionaries[i];
                    var src = md.Source?.OriginalString ?? string.Empty;
                    if (src.IndexOf("Themes/", StringComparison.OrdinalIgnoreCase) >= 0)
                        app.Resources.MergedDictionaries.RemoveAt(i);
                }
                // Always use dark theme
                var uri = new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative);
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
                if (app.MainWindow != null)
                {
                    app.MainWindow.SetResourceReference(Window.BackgroundProperty, "Brush.Background");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Theme] ApplyTheme error: {ex.Message}");
            }
        }
    }
}
