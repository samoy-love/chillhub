// <copyright file="NewsWebViewStorage.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.News {
    using System;
    using System.IO;

    /// <summary>
    /// Каталог данных WebView2 и уборка каталога, оставшегося от прежних версий лаунчера.
    /// Только файловая система — ни окна, ни браузера здесь нет.
    /// </summary>
    internal static class NewsWebViewStorage {
        private static bool legacyFolderCleaned;

        /// <summary>
        /// Каталог данных WebView2 (кеш, куки, localStorage).
        /// Обязательно вне папки установки: по умолчанию WebView2 кладёт
        /// "ChillHub.exe.WebView2" рядом с exe, а самообновление сносит всё,
        /// чего нет в манифесте, — вместе с этим каталогом.
        /// </summary>
        /// <returns>Полный путь к каталогу данных WebView2.</returns>
        internal static string GetUserDataFolder() {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "ChillHub", "WebView2");
        }

        /// <summary>
        /// Имена каталогов, которые WebView2 мог оставить в папке установки: по имени
        /// текущего exe и по историческому «ChillHub.exe».
        /// </summary>
        /// <param name="exeName">Имя файла текущего процесса.</param>
        /// <returns>Кандидаты на удаление.</returns>
        internal static string[] LegacyFolderNames(string? exeName) {
            var name = string.IsNullOrEmpty(exeName) ? "ChillHub.exe" : exeName;
            return new[] { name + ".WebView2", "ChillHub.exe.WebView2" };
        }

        /// <summary>
        /// Разовая уборка каталога данных WebView2, оставшегося в папке установки
        /// от версий лаунчера без явного UserDataFolder.
        /// </summary>
        internal static void CleanupLegacyUserDataFolder() {
            if (legacyFolderCleaned) {
                return;
            }

            legacyFolderCleaned = true;
            try {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var exeName = Path.GetFileName(Environment.ProcessPath) ?? "ChillHub.exe";
                foreach (var candidate in LegacyFolderNames(exeName)) {
                    var legacy = Path.Combine(baseDir, candidate);
                    if (!Directory.Exists(legacy)) {
                        continue;
                    }

                    try {
                        Directory.Delete(legacy, recursive: true);
                        Logging.Logger.Info($"WebView2: удалён старый каталог данных '{legacy}'");
                    }
                    catch (Exception ex) {
                        // Каталог мог остаться залоченным — не критично, попробуем в следующий раз
                        Logging.Logger.Warn($"WebView2: не удалось удалить '{legacy}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, "NewsDetailPage.CleanupLegacyUserDataFolder");
            }
        }
    }
}
