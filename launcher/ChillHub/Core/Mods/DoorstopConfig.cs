// <copyright file="DoorstopConfig.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Переключение модов в папке игры через <c>doorstop_config.ini</c>.
    /// <para>
    /// BepInEx запускается так: рядом с exe лежит <c>winhttp.dll</c> (UnityDoorstop),
    /// он читает <c>doorstop_config.ini</c>, находит по ОТНОСИТЕЛЬНОМУ пути
    /// <c>BepInEx\core\BepInEx.Preloader.dll</c> и грузит загрузчик. Всё внутри одной
    /// папки, никаких аргументов запуска не нужно — игра подхватывает моды и от
    /// двойного клика по exe, и из библиотеки Steam.
    /// </para>
    /// <para>
    /// Отсюда и способ выключить моды: <c>enabled = false</c> в этом файле. Аргументы
    /// командной строки для этого не годятся. r2modman шлёт для ванили
    /// <c>--doorstop-enable false</c> — флаг Doorstop 3, а BepInEx 5.4.23 и новее
    /// приносит Doorstop 4, где ключ называется <c>--doorstop-enabled</c> и старый
    /// просто не разбирается. Правка ini работает в обеих версиях, потому что имя
    /// ключа <c>enabled</c> у них общее.
    /// </para>
    /// </summary>
    internal static class DoorstopConfig {
        /// <summary>Имя файла настроек Doorstop в папке игры.</summary>
        internal const string FileName = "doorstop_config.ini";

        /// <summary>Библиотека-перехватчик, которую Doorstop подкладывает рядом с exe.</summary>
        internal const string ProxyDllName = "winhttp.dll";

        /// <summary>Файл с версией Doorstop, который кладёт пакет BepInEx.</summary>
        internal const string VersionFileName = ".doorstop_version";

        /// <summary>Ключ, значение которого мы меняем. Одинаков в Doorstop 3 и 4.</summary>
        private const string EnabledKey = "enabled";

        /// <summary>Потолок размера файла, который мы вообще беремся разбирать.</summary>
        private const long MaxIniBytes = 256 * 1024;

        /// <summary>
        /// Читает текущее состояние: включены ли моды в этой папке игры.
        /// </summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <returns>true — Doorstop включён; null — файла нет или значение не прочиталось.</returns>
        internal static bool? ReadEnabled(string gameDir) {
            var path = Path.Combine(gameDir, FileName);
            try {
                if (!File.Exists(path) || new FileInfo(path).Length > MaxIniBytes) {
                    return null;
                }

                foreach (var line in File.ReadAllLines(path)) {
                    if (TryParseEnabled(line, out var value)) {
                        return value;
                    }
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"[mods] DoorstopConfig.ReadEnabled '{path}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Выставляет <c>enabled</c> в файле настроек папки игры.
        /// <para>
        /// Правится ТОЛЬКО значение — оформление строки сохраняется. Реальные сборки
        /// пишут ключ по-разному (<c>enabled = true</c> у Lethal Company,
        /// <c>enabled=true</c> у Risk of Rain 2), и переписывание файла «как правильно»
        /// означало бы, что при каждом запуске лаунчер считает файл изменившимся.
        /// </para>
        /// </summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <param name="enabled">Нужное состояние.</param>
        /// <returns>true, если файл после вызова содержит нужное значение.</returns>
        internal static bool SetEnabled(string gameDir, bool enabled) {
            var path = Path.Combine(gameDir, FileName);
            try {
                if (!File.Exists(path)) {
                    Logging.Logger.Warn($"[mods] DoorstopConfig.SetEnabled: файла '{path}' нет");
                    return false;
                }

                if (new FileInfo(path).Length > MaxIniBytes) {
                    Logging.Logger.Warn($"[mods] DoorstopConfig.SetEnabled: '{path}' подозрительно велик, не трогаем");
                    return false;
                }

                var lines = File.ReadAllLines(path);
                var changed = false;
                var found = false;

                for (var i = 0; i < lines.Length; i++) {
                    if (!TryParseEnabled(lines[i], out var current)) {
                        continue;
                    }

                    found = true;
                    if (current == enabled) {
                        break;
                    }

                    lines[i] = ReplaceValue(lines[i], enabled ? "true" : "false");
                    changed = true;
                    break;
                }

                if (!found) {
                    Logging.Logger.Warn($"[mods] DoorstopConfig.SetEnabled: ключ '{EnabledKey}' в '{path}' не найден");
                    return false;
                }

                if (changed) {
                    // Кодировку не меняем: файл кладёт пакет BepInEx, и переписывать его
                    // в UTF-8 с BOM — лишний повод для расхождения по хешу.
                    File.WriteAllLines(path, lines, new UTF8Encoding(false));
                    Logging.Logger.Info($"[mods] doorstop: enabled={enabled} в '{path}'");
                } else {
                    Logging.Logger.Info($"[mods] doorstop: enabled уже {enabled} в '{path}'");
                }

                return true;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, $"[mods] DoorstopConfig.SetEnabled '{path}'");
                Metrics.MetricsService.Error("mods_doorstop_write_failed");
                return false;
            }
        }

        /// <summary>
        /// Читает мажорную версию Doorstop из файла <c>.doorstop_version</c>.
        /// В журнал её стоит писать: поведение аргументов у 3 и 4 разное, и при разборе
        /// чужой проблемы это первое, что хочется знать.
        /// </summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <returns>Мажорная версия или 0, если файла нет.</returns>
        internal static int ReadMajorVersion(string gameDir) {
            try {
                var path = Path.Combine(gameDir, VersionFileName);
                if (!File.Exists(path)) {
                    return 0;
                }

                var text = File.ReadAllText(path).Trim();
                var head = text.Split('.', 2)[0];
                return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"[mods] DoorstopConfig.ReadMajorVersion: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Есть ли в папке игры всё, что нужно для загрузки модов.
        /// </summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <returns>true, если лежат и перехватчик, и файл настроек.</returns>
        internal static bool IsInstalled(string gameDir) {
            try {
                return File.Exists(Path.Combine(gameDir, ProxyDllName))
                    && File.Exists(Path.Combine(gameDir, FileName));
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"[mods] DoorstopConfig.IsInstalled: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Разбирает строку ini вида <c>enabled = true</c>. Секцию не проверяем:
        /// ключ с таким именем в файле Doorstop ровно один, а имя секции у третьей и
        /// четвёртой версии разное.
        /// </summary>
        /// <param name="line">Строка файла.</param>
        /// <param name="value">Прочитанное значение.</param>
        /// <returns>true, если строка задаёт этот ключ.</returns>
        private static bool TryParseEnabled(string line, out bool value) {
            value = false;
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';' || trimmed[0] == '[') {
                return false;
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0) {
                return false;
            }

            if (!trimmed[..eq].Trim().Equals(EnabledKey, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            var raw = trimmed[(eq + 1)..].Trim();

            // Doorstop понимает true/false; всё прочее считаем «выключено», как и он сам.
            value = raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
            return true;
        }

        /// <summary>Меняет только правую часть строки, сохраняя отступы и пробелы вокруг «=».</summary>
        /// <param name="line">Исходная строка.</param>
        /// <param name="newValue">Новое значение.</param>
        /// <returns>Строка с заменённым значением.</returns>
        private static string ReplaceValue(string line, string newValue) {
            var eq = line.IndexOf('=');
            if (eq < 0) {
                return line;
            }

            var head = line[..(eq + 1)];
            var tail = line[(eq + 1)..];

            // Сохраняем ведущие пробелы значения: «enabled = true» остаётся с пробелом,
            // «enabled=true» — без.
            var padding = 0;
            while (padding < tail.Length && (tail[padding] == ' ' || tail[padding] == '\t')) {
                padding++;
            }

            return head + tail[..padding] + newValue;
        }
    }
}
