// <copyright file="ModProfileStore.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>Один профиль модов: имя, необязательная папка модов и доп. аргументы запуска.</summary>
    public sealed class ModProfile {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Путь к папке модов относительно каталога игры. Null — без модов (vanilla).</summary>
        [JsonPropertyName("modFolder")]
        public string? ModFolder { get; set; }

        /// <summary>Дополнительные аргументы командной строки при запуске с этим профилем.</summary>
        [JsonPropertyName("extraArgs")]
        public string? ExtraArgs { get; set; }
    }

    /// <summary>Содержимое файла профилей одной игры: выбранный профиль + список профилей.</summary>
    public sealed class ModProfileFile {
        [JsonPropertyName("selected")]
        public string? Selected { get; set; }

        [JsonPropertyName("profiles")]
        public List<ModProfile> Profiles { get; set; } = new();
    }

    /// <summary>
    /// Первая итерация модпак-профилей (трек F): без реальной установки модов — только
    /// имя, необязательный путь к папке модов и дополнительные аргументы командной строки.
    /// Хранилище — <c>%APPDATA%/ChillHub/profiles/&lt;gameId&gt;.json</c>, не <c>%LOCALAPPDATA%</c>
    /// (как первоначально указывал PLAN.md): та же причина, что увела playtime.json из
    /// %LOCALAPPDATA% в трек E — это папка установки лаунчера, и пользовательские файлы
    /// там подхватываются self-update пакетом, что приводит к циклу обновлений.
    /// </summary>
    public static class ModProfileStore {
        private static string ProfilesDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "profiles");

        private static string ProfilePath(string gameId) => Path.Combine(ProfilesDir, $"{gameId}.json");

        /// <summary>
        /// Читает профили игры с диска. Файла нет или он битый — молча возвращает
        /// одиночный профиль «Vanilla» (не считать это ошибкой: у большинства игр
        /// профилей никогда не будет).
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Файл профилей (никогда не null, минимум один профиль внутри).</returns>
        public static ModProfileFile Load(string gameId) {
            try {
                var path = ProfilePath(gameId);
                if (!File.Exists(path)) {
                    return DefaultFile();
                }

                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<ModProfileFile>(json);
                if (parsed == null || parsed.Profiles.Count == 0) {
                    return DefaultFile();
                }

                return parsed;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ModProfileStore.Load({gameId}): {ex.Message}");
                return DefaultFile();
            }
        }

        /// <summary>Сохраняет профили игры на диск.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="file">Содержимое файла профилей.</param>
        /// <returns>true, если запись удалась.</returns>
        public static bool Save(string gameId, ModProfileFile file) {
            try {
                Directory.CreateDirectory(ProfilesDir);
                var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProfilePath(gameId), json);
                return true;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ModProfileStore.Save({gameId}): {ex.Message}");
                return false;
            }
        }

        /// <summary>Профиль, отмеченный как выбранный в файле, либо первый в списке.</summary>
        /// <param name="file">Файл профилей.</param>
        /// <returns>Выбранный профиль или null, если список пуст.</returns>
        public static ModProfile? SelectedProfile(ModProfileFile file) {
            if (file.Profiles.Count == 0) {
                return null;
            }

            return file.Profiles.FirstOrDefault(p => p.Id == file.Selected) ?? file.Profiles[0];
        }

        private static ModProfileFile DefaultFile() {
            var vanilla = new ModProfile { Id = "vanilla", Name = "Vanilla (без модов)", ModFolder = null };
            return new ModProfileFile { Selected = vanilla.Id, Profiles = new List<ModProfile> { vanilla } };
        }
    }
}
