// <copyright file="SteamLocator.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Microsoft.Win32;

    /// <summary>
    /// Почему поиск не удался. Каждая ветка — своя ступень, потому что пользователь
    /// пришлёт «не находит игру», и по логу должно быть видно, где именно оборвалось.
    /// </summary>
    internal enum SteamLookup {
        /// <summary>Папка игры найдена.</summary>
        Found,

        /// <summary>Steam не установлен: в реестре нет пути.</summary>
        SteamNotInstalled,

        /// <summary>Не нашли ни одной библиотеки Steam.</summary>
        NoLibraries,

        /// <summary>Ни в одной библиотеке нет манифеста этой игры — она не установлена.</summary>
        GameNotInstalled,

        /// <summary>Манифест есть, но папки, которую он называет, на диске нет.</summary>
        FolderMissing,

        /// <summary>У игры не задан Steam AppID — сервер не прислал его в настройках модов.</summary>
        NoAppId,
    }

    /// <summary>
    /// Результат поиска Steam-копии игры.
    /// </summary>
    /// <param name="Outcome">Чем закончился поиск.</param>
    /// <param name="GameDir">Папка игры, если нашли.</param>
    /// <param name="SteamExe">Путь к Steam.exe, если Steam установлен.</param>
    /// <param name="Trace">Пошаговый след для журнала.</param>
    internal sealed record SteamGame(
        SteamLookup Outcome,
        string GameDir,
        string SteamExe,
        IReadOnlyList<string> Trace) {
        /// <summary>Нашлась ли папка игры.</summary>
        internal bool Ok => this.Outcome == SteamLookup.Found && !string.IsNullOrEmpty(this.GameDir);
    }

    /// <summary>
    /// Поиск установленной через Steam копии игры и самого Steam.
    /// <para>
    /// Официального API для «где лежит игра N» у Steam нет, поэтому путь такой же,
    /// каким его проходят все менеджеры модов: реестр → <c>libraryfolders.vdf</c> →
    /// <c>appmanifest_&lt;appid&gt;.acf</c> → <c>steamapps/common/&lt;installdir&gt;</c>.
    /// </para>
    /// </summary>
    internal static class SteamLocator {
        /// <summary>
        /// Ветки реестра с путём установки Steam. 64-битная идёт первой, но проверяются
        /// обе: на 32-битной системе (и в некоторых сборках) значение лежит только во второй.
        /// </summary>
        private static readonly string[] RegistryKeys = {
            @"SOFTWARE\WOW6432Node\Valve\Steam",
            @"SOFTWARE\Valve\Steam",
        };

        /// <summary>
        /// Подмена чтения реестра на время теста; null — работает настоящий реестр.
        /// Прогон тестов не должен зависеть от того, установлен ли Steam на машине сборки.
        /// </summary>
        internal static Func<string?>? SteamPathOverride { get; set; }

        /// <summary>Возвращает поиск к настоящему реестру.</summary>
        internal static void ResetForTests() => SteamPathOverride = null;

        /// <summary>
        /// Ищет папку игры по её Steam AppID.
        /// </summary>
        /// <param name="appId">Идентификатор приложения в Steam.</param>
        /// <param name="steamFolderName">
        /// Имя папки из схемы Thunderstore. Иногда оно вложенное: у How to Fish
        /// <c>installdir</c> равен «How to Fish», а игра лежит в
        /// <c>common/How to Fish/How to Fish</c>. Пустая строка — берём <c>installdir</c>.
        /// </param>
        /// <returns>Что нашли и как искали.</returns>
        internal static SteamGame Locate(string? appId, string? steamFolderName) {
            var trace = new List<string>();

            if (string.IsNullOrWhiteSpace(appId)) {
                trace.Add("Steam AppID не задан в настройках игры");
                return new SteamGame(SteamLookup.NoAppId, string.Empty, string.Empty, trace);
            }

            var steamDir = FindSteamDirectory(trace);
            if (string.IsNullOrEmpty(steamDir)) {
                return new SteamGame(SteamLookup.SteamNotInstalled, string.Empty, string.Empty, trace);
            }

            var steamExe = Path.Combine(steamDir, "steam.exe");
            if (!File.Exists(steamExe)) {
                // Steam переставили или снесли, оставив ключ реестра. Папку игры искать
                // всё ещё имеет смысл (файлы на месте), но запускать будет нечем.
                trace.Add($"steam.exe не найден в '{steamDir}'");
                steamExe = string.Empty;
            }

            var libraries = FindLibraries(steamDir, trace);
            if (libraries.Count == 0) {
                return new SteamGame(SteamLookup.NoLibraries, string.Empty, steamExe, trace);
            }

            foreach (var library in libraries) {
                var manifest = Path.Combine(library, $"appmanifest_{appId}.acf");
                if (!File.Exists(manifest)) {
                    continue;
                }

                trace.Add($"манифест приложения найден: '{manifest}'");
                var installDir = ReadInstallDir(manifest, trace);
                if (string.IsNullOrEmpty(installDir)) {
                    continue;
                }

                var dir = ResolveGameDir(library, installDir, steamFolderName, trace);
                if (dir == null) {
                    return new SteamGame(SteamLookup.FolderMissing, string.Empty, steamExe, trace);
                }

                trace.Add($"папка игры: '{dir}'");
                return new SteamGame(SteamLookup.Found, dir, steamExe, trace);
            }

            trace.Add($"appmanifest_{appId}.acf не найден ни в одной библиотеке — игра не установлена в Steam");
            return new SteamGame(SteamLookup.GameNotInstalled, string.Empty, steamExe, trace);
        }

        /// <summary>Читает путь установки Steam из реестра.</summary>
        /// <param name="trace">Куда писать след поиска.</param>
        /// <returns>Путь или пустая строка.</returns>
        private static string FindSteamDirectory(List<string> trace) {
            if (SteamPathOverride != null) {
                var overridden = SteamPathOverride();
                trace.Add($"путь Steam подменён для теста: '{overridden}'");
                return Normalize(overridden);
            }

            foreach (var key in RegistryKeys) {
                try {
                    using var reg = Registry.LocalMachine.OpenSubKey(key);
                    var value = reg?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(value)) {
                        trace.Add($@"реестр HKLM\{key}\InstallPath = '{value}'");
                        return Normalize(value);
                    }
                }
                catch (Exception ex) {
                    trace.Add($@"реестр HKLM\{key}: {ex.Message}");
                }
            }

            trace.Add("путь установки Steam в реестре не найден");
            return string.Empty;
        }

        /// <summary>
        /// Собирает каталоги <c>steamapps</c> всех библиотек: своя папка Steam плюс
        /// всё, что перечислено в <c>libraryfolders.vdf</c>.
        /// </summary>
        /// <param name="steamDir">Папка установки Steam.</param>
        /// <param name="trace">Куда писать след поиска.</param>
        /// <returns>Пути каталогов steamapps.</returns>
        private static List<string> FindLibraries(string steamDir, List<string> trace) {
            var result = new List<string>();
            var own = Path.Combine(steamDir, "steamapps");
            if (Directory.Exists(own)) {
                result.Add(own);
            }

            var vdfPath = Path.Combine(own, "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) {
                trace.Add($"libraryfolders.vdf не найден в '{own}'");
                trace.Add($"библиотек найдено: {result.Count}");
                return result;
            }

            try {
                var root = VdfParser.Parse(File.ReadAllText(vdfPath));

                // Современный Steam пишет "libraryfolders", старый — "LibraryFolders";
                // разбор регистронезависим, поэтому достаточно взять первый непустой узел.
                var folders = root.Child("libraryfolders") ?? root.Child("LibraryFolders") ?? root;
                foreach (var entry in folders.Children) {
                    // Ключи библиотек — это индексы "0", "1", ...; остальное (contentstatsid
                    // и прочая служебка) пропускаем.
                    if (!int.TryParse(entry.Key, out _)) {
                        continue;
                    }

                    // Современный формат: вложенный блок с "path". Старый: сразу строка.
                    var path = entry.Value.Value ?? entry.Value.String("path");
                    if (string.IsNullOrWhiteSpace(path)) {
                        continue;
                    }

                    var apps = Path.Combine(Normalize(path), "steamapps");
                    if (Directory.Exists(apps) && !result.Contains(apps, StringComparer.OrdinalIgnoreCase)) {
                        result.Add(apps);
                    }
                }
            }
            catch (Exception ex) {
                trace.Add($"разбор libraryfolders.vdf не удался: {ex.Message}");
            }

            trace.Add($"библиотек найдено: {result.Count}");
            return result;
        }

        /// <summary>Читает <c>AppState.installdir</c> из манифеста приложения.</summary>
        /// <param name="manifestPath">Путь к appmanifest_*.acf.</param>
        /// <param name="trace">Куда писать след поиска.</param>
        /// <returns>Имя папки или пустая строка.</returns>
        private static string ReadInstallDir(string manifestPath, List<string> trace) {
            try {
                var root = VdfParser.Parse(File.ReadAllText(manifestPath));
                var state = root.Child("AppState");
                if (state == null) {
                    trace.Add("в манифесте нет блока AppState");
                    return string.Empty;
                }

                var installDir = state.String("installdir");
                if (string.IsNullOrWhiteSpace(installDir)) {
                    trace.Add("в манифесте нет installdir");
                    return string.Empty;
                }

                trace.Add($"installdir = '{installDir}', StateFlags = '{state.String("StateFlags")}'");
                return installDir;
            }
            catch (Exception ex) {
                trace.Add($"чтение манифеста не удалось: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Строит путь к папке игры, учитывая вложенный вариант.
        /// </summary>
        /// <param name="library">Каталог steamapps библиотеки.</param>
        /// <param name="installDir">Значение installdir из манифеста.</param>
        /// <param name="steamFolderName">Имя папки из схемы Thunderstore.</param>
        /// <param name="trace">Куда писать след поиска.</param>
        /// <returns>Существующая папка или null.</returns>
        private static string? ResolveGameDir(string library, string installDir, string? steamFolderName, List<string> trace) {
            var common = Path.Combine(library, "common");
            var direct = Path.Combine(common, installDir);

            // Вложенный случай: схема называет папку «How to Fish/How to Fish» при
            // installdir «How to Fish». Наивный путь ведёт в каталог без exe, и игра
            // «не находится» при том, что она установлена.
            var folder = (steamFolderName ?? string.Empty).Replace('\\', '/').Trim('/');
            if (folder.Length > 0 &&
                folder.StartsWith(installDir + "/", StringComparison.OrdinalIgnoreCase)) {
                var nested = Path.Combine(common, folder.Replace('/', Path.DirectorySeparatorChar));
                trace.Add($"вложенная папка из схемы: '{folder}'");
                if (Directory.Exists(nested)) {
                    return nested;
                }

                trace.Add($"вложенной папки нет на диске: '{nested}', пробуем обычную");
            }

            if (Directory.Exists(direct)) {
                return direct;
            }

            trace.Add($"папки игры нет на диске: '{direct}'");
            return null;
        }

        /// <summary>
        /// Приводит путь из реестра или VDF к виду Windows: Steam пишет прямые слеши
        /// и нижний регистр, и такой путь ломает сравнения и Path.Combine по-разному
        /// в разных местах.
        /// </summary>
        /// <param name="path">Исходный путь.</param>
        /// <returns>Нормализованный путь.</returns>
        private static string Normalize(string? path) {
            if (string.IsNullOrWhiteSpace(path)) {
                return string.Empty;
            }

            return path.Trim().Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}
