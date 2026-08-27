// <copyright file="ModsLaunch.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;

    /// <summary>Какую копию игры и как запускать.</summary>
    internal enum LaunchTarget {
        /// <summary>Копия из Steam, с модами.</summary>
        SteamModded,

        /// <summary>Копия из Steam, без модов.</summary>
        SteamVanilla,

        /// <summary>Сборка с сервера Chill Hub, с модами.</summary>
        LocalModded,

        /// <summary>Сборка с сервера Chill Hub, без модов.</summary>
        LocalVanilla,
    }

    /// <summary>
    /// Один пункт меню кнопки «Играть».
    /// </summary>
    /// <param name="Target">Что именно запускается.</param>
    /// <param name="Title">Подпись для пользователя.</param>
    /// <param name="GameDir">Папка игры.</param>
    /// <param name="Modded">Включать ли моды.</param>
    /// <param name="Available">Доступен ли пункт.</param>
    /// <param name="Reason">Почему пункт недоступен; пусто, если доступен.</param>
    internal sealed record LaunchOption(
        LaunchTarget Target,
        string Title,
        string GameDir,
        bool Modded,
        bool Available,
        string Reason) {
        /// <summary>Запускается ли эта копия через Steam.</summary>
        internal bool ViaSteam => this.Target is LaunchTarget.SteamModded or LaunchTarget.SteamVanilla;
    }

    /// <summary>
    /// Четыре способа запустить игру с модпаком: копия из Steam или сборка с сервера,
    /// каждая — с модами или без.
    /// <para>
    /// Разница между «с модами» и «без» — одно значение в <c>doorstop_config.ini</c>
    /// той папки, которую запускаем. Файлы модов при этом остаются на диске: их
    /// удаление и возврат заняли бы минуты на паке в полтора гигабайта, а Doorstop и
    /// так их не тронет, пока выключен.
    /// </para>
    /// </summary>
    internal static class ModsLaunch {
        /// <summary>
        /// Шов для тестов: как именно стартует процесс. Настоящая реализация запускает
        /// программу, поэтому в прогоне тестов её подменяют.
        /// </summary>
        internal static Func<ProcessStartInfo, Process?> StartProcess { get; set; } = psi => Process.Start(psi);

        /// <summary>Возвращает запуск к настоящему процессу.</summary>
        internal static void ResetForTests() => StartProcess = psi => Process.Start(psi);

        /// <summary>
        /// Составляет список вариантов запуска для игры.
        /// <para>
        /// Недоступные пункты не выбрасываются, а возвращаются с причиной: «Steam-копия
        /// не найдена» на кнопке объясняет пользователю ситуацию, а исчезнувший пункт —
        /// нет.
        /// </para>
        /// </summary>
        /// <param name="mods">Настройки модов игры с сервера.</param>
        /// <param name="localRoot">Папка сборки с сервера.</param>
        /// <param name="localInstalled">Установлена ли сборка с сервера.</param>
        /// <param name="steam">Результат поиска копии в Steam.</param>
        /// <returns>Четыре варианта в порядке показа.</returns>
        internal static IReadOnlyList<LaunchOption> Options(
            ModsInfo? mods, string localRoot, bool localInstalled, SteamGame steam) {
            var options = new List<LaunchOption>();
            var packName = mods?.Describe() ?? string.Empty;
            var moddedTitle = string.IsNullOrEmpty(packName) ? "с модами" : $"с модами ({packName})";
            var hasPack = mods is { HasLatest: true };

            var steamReason = DescribeSteam(steam);
            var steamOk = steam.Ok;

            // Причины «почему нельзя с модами» считаются заранее, отдельными строками:
            // тернарник на три ветки прямо в аргументе читается хуже и спорит со
            // StyleCop (SA1118).
            var steamModdedReason = !steamOk ? steamReason
                : !hasPack ? "модпак ещё не опубликован"
                : "моды не установлены в копию Steam — нажмите «Обновить»";
            var localModdedReason = !localInstalled ? "сборка не установлена"
                : !hasPack ? "модпак ещё не опубликован"
                : "моды не установлены — нажмите «Обновить»";

            options.Add(new LaunchOption(
                LaunchTarget.SteamModded,
                $"Steam · {moddedTitle}",
                steam.GameDir,
                true,
                steamOk && hasPack && DoorstopConfig.IsInstalled(steam.GameDir),
                steamModdedReason));

            options.Add(new LaunchOption(
                LaunchTarget.SteamVanilla,
                "Steam · без модов",
                steam.GameDir,
                false,
                steamOk,
                steamReason));

            options.Add(new LaunchOption(
                LaunchTarget.LocalModded,
                $"Сборка Chill Hub · {moddedTitle}",
                localRoot,
                true,
                localInstalled && hasPack && DoorstopConfig.IsInstalled(localRoot),
                localModdedReason));

            options.Add(new LaunchOption(
                LaunchTarget.LocalVanilla,
                "Сборка Chill Hub · без модов",
                localRoot,
                false,
                localInstalled,
                "сборка не установлена"));

            return options;
        }

        /// <summary>
        /// Готовит папку и запускает игру.
        /// <para>
        /// Порядок важен: сначала правится <c>doorstop_config.ini</c>, потом стартует
        /// процесс. Наоборот — и игра успеет прочитать старое значение.
        /// </para>
        /// </summary>
        /// <param name="option">Выбранный вариант.</param>
        /// <param name="mods">Настройки модов игры.</param>
        /// <param name="exeRelativePath">Путь к exe сборки с сервера, из реестра игр.</param>
        /// <param name="steam">Результат поиска копии в Steam.</param>
        /// <returns>Запущенный процесс или null, если запустить не удалось.</returns>
        internal static Process? Start(LaunchOption option, ModsInfo? mods, string exeRelativePath, SteamGame steam) {
            if (!option.Available) {
                Logging.Logger.Warn($"[mods] запуск отклонён: {option.Target} — {option.Reason}");
                return null;
            }

            ApplyDoorstop(option);

            return option.ViaSteam
                ? StartViaSteam(option, mods, steam)
                : StartDirect(option, exeRelativePath);
        }

        /// <summary>
        /// Приводит папку игры в нужное состояние: моды включены или выключены.
        /// <para>
        /// Неудача не отменяет запуск. Если файла настроек нет, значит модов в этой
        /// папке и не было — ванильный запуск в такой папке и так ванильный, а
        /// «с модами» уже отсечён проверкой доступности пункта.
        /// </para>
        /// </summary>
        /// <param name="option">Выбранный вариант.</param>
        private static void ApplyDoorstop(LaunchOption option) {
            var before = DoorstopConfig.ReadEnabled(option.GameDir);
            var version = DoorstopConfig.ReadMajorVersion(option.GameDir);
            var ok = DoorstopConfig.SetEnabled(option.GameDir, option.Modded);

            Logging.Logger.Info(
                $"[mods] doorstop: цель={option.Target} папка='{option.GameDir}' " +
                $"версия={version} было={Describe(before)} стало={option.Modded} применено={ok}");
        }

        /// <summary>Запускает игру через Steam.</summary>
        /// <param name="option">Выбранный вариант.</param>
        /// <param name="mods">Настройки модов игры.</param>
        /// <param name="steam">Результат поиска копии в Steam.</param>
        /// <returns>Процесс Steam или null.</returns>
        private static Process? StartViaSteam(LaunchOption option, ModsInfo? mods, SteamGame steam) {
            if (string.IsNullOrEmpty(steam.SteamExe)) {
                Logging.Logger.Error("[mods] запуск через Steam невозможен: steam.exe не найден");
                Metrics.MetricsService.Error("mods_steam_not_found");
                return null;
            }

            var appId = mods?.SteamAppId ?? string.Empty;
            var psi = new ProcessStartInfo {
                FileName = steam.SteamExe,
                WorkingDirectory = Path.GetDirectoryName(steam.SteamExe) ?? string.Empty,
                UseShellExecute = true,
            };
            psi.ArgumentList.Add("-applaunch");
            psi.ArgumentList.Add(appId);

            Logging.Logger.Info($"[mods] запуск: '{psi.FileName}' -applaunch {appId} (моды: {option.Modded})");
            return Run(psi);
        }

        /// <summary>Запускает exe напрямую из папки игры.</summary>
        /// <param name="option">Выбранный вариант.</param>
        /// <param name="exeRelativePath">Относительный путь к exe из реестра игр.</param>
        /// <returns>Процесс игры или null.</returns>
        private static Process? StartDirect(LaunchOption option, string exeRelativePath) {
            var exe = ResolveExe(option.GameDir, exeRelativePath);
            if (exe == null) {
                Logging.Logger.Error($"[mods] исполняемый файл не найден в '{option.GameDir}' (ожидался '{exeRelativePath}')");
                Metrics.MetricsService.Error("mods_exe_missing");
                return null;
            }

            var psi = new ProcessStartInfo {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? option.GameDir,
                UseShellExecute = true,
            };

            Logging.Logger.Info($"[mods] запуск: '{exe}' (моды: {option.Modded})");
            return Run(psi);
        }

        /// <summary>Стартует процесс, переводя исключение в запись журнала.</summary>
        /// <param name="psi">Описание запуска.</param>
        /// <returns>Процесс или null.</returns>
        private static Process? Run(ProcessStartInfo psi) {
            try {
                var proc = StartProcess(psi);
                Logging.Logger.Info($"[mods] процесс запущен, PID={(proc == null ? "нет" : proc.Id.ToString(CultureInfo.InvariantCulture))}");
                return proc;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, "[mods] ModsLaunch.Run");
                Metrics.MetricsService.Error("mods_launch_failed");
                return null;
            }
        }

        /// <summary>
        /// Ищет исполняемый файл в папке игры: сначала указанный в реестре путь, затем
        /// имена из схемы Thunderstore.
        /// </summary>
        /// <param name="gameDir">Папка игры.</param>
        /// <param name="exeRelativePath">Относительный путь из реестра игр.</param>
        /// <returns>Полный путь или null.</returns>
        internal static string? ResolveExe(string gameDir, string? exeRelativePath) {
            try {
                if (!string.IsNullOrWhiteSpace(exeRelativePath)) {
                    var rel = exeRelativePath.Replace('/', Path.DirectorySeparatorChar)
                                             .Replace('\\', Path.DirectorySeparatorChar);
                    var full = Path.Combine(gameDir, rel);
                    if (File.Exists(full)) {
                        return full;
                    }
                }

                if (!Directory.Exists(gameDir)) {
                    return null;
                }

                // Запасной путь: единственный exe в корне. Игры на Unity кладут рядом с
                // ним UnityCrashHandler64.exe, поэтому он отсеивается по имени.
                var candidates = Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(p => !Path.GetFileName(p).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return candidates.Count == 1 ? candidates[0] : null;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"[mods] ResolveExe '{gameDir}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Превращает результат поиска Steam в объяснение для пользователя.</summary>
        /// <param name="steam">Результат поиска.</param>
        /// <returns>Причина недоступности или пустая строка.</returns>
        private static string DescribeSteam(SteamGame steam) => steam.Outcome switch {
            SteamLookup.Found => string.Empty,
            SteamLookup.SteamNotInstalled => "Steam не установлен",
            SteamLookup.NoLibraries => "не найдены библиотеки Steam",
            SteamLookup.GameNotInstalled => "игра не установлена в Steam",
            SteamLookup.FolderMissing => "папка игры Steam не найдена на диске",
            SteamLookup.NoAppId => "для игры не задан Steam AppID",
            _ => "Steam-копия недоступна",
        };

        /// <summary>Читаемое значение трёхзначного «включены ли моды».</summary>
        /// <param name="value">Прочитанное состояние.</param>
        /// <returns>Текст для журнала.</returns>
        private static string Describe(bool? value) => value switch {
            true => "true",
            false => "false",
            _ => "нет файла",
        };
    }
}
