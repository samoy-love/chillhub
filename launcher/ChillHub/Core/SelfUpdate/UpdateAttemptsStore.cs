// <copyright file="UpdateAttemptsStore.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;
    using System.IO;

    // ---------------------------------------------------------------------
    // Защита от зацикливания: счётчик применений обновления на одну версию.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Счётчик подряд идущих попыток применить обновление на ОДНУ И ТУ ЖЕ версию.
    /// Больше <see cref="SelfUpdateChecker.MaxSameVersionAttempts"/> — значит апдейтер
    /// не доводит дело до конца, и лаунчер крутится в петле.
    /// </summary>
    internal sealed class UpdateAttemptsStore {
        private readonly string path;

        internal UpdateAttemptsStore(string? path = null) {
            this.path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
        }

        /// <summary>Настоящее расположение счётчика: рядом с остальным роуминг-состоянием.</summary>
        internal static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChillHub",
            "selfupdate-attempts.txt");

        /// <summary>Путь к файлу счётчика — его показывают пользователю в тупиковом состоянии.</summary>
        internal string FilePath => this.path;

        /// <summary>Сколько раз подряд обновление на эту версию уже применялось.</summary>
        /// <param name="version">Версия, на которую обновляемся.</param>
        /// <returns>Число попыток; 0, если счётчик пуст, испорчен или про другую версию.</returns>
        internal int Get(string version) {
            try {
                if (!File.Exists(this.path)) {
                    return 0;
                }

                var parts = (File.ReadAllText(this.path) ?? string.Empty).Split('|');
                if (parts.Length < 2) {
                    return 0;
                }

                if (!string.Equals(parts[0].Trim(), version, StringComparison.OrdinalIgnoreCase)) {
                    return 0;
                }

                return int.TryParse(parts[1].Trim(), out var n) ? n : 0;
            }
            catch {
                return 0;
            }
        }

        /// <summary>Засчитывает попытку. Ошибка записи ничего не должна ломать.</summary>
        /// <param name="version">Версия, на которую обновляемся.</param>
        internal void Register(string version) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
                var n = this.Get(version) + 1;
                File.WriteAllText(this.path, $"{version}|{n}|{DateTime.Now:O}", SelfUpdateRules.Utf8NoBom);
            }
            catch {
            }
        }

        /// <summary>Обнуляет счётчик: обновление дошло до конца либо оказалось не нужным.</summary>
        internal void Reset() {
            try {
                if (File.Exists(this.path)) {
                    File.Delete(this.path);
                }
            }
            catch {
            }
        }
    }
}
