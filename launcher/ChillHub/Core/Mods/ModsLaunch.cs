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
    /// Что произойдёт по нажатию на пункт меню.
    /// <para>
    /// ПУНКТ, КОТОРЫЙ НИЧЕГО НЕ ДЕЛАЕТ, — ХУДШИЙ ИЗ ВОЗМОЖНЫХ. Раньше «Steam · с
    /// модами» просто выключался с припиской «моды не установлены — нажмите
    /// „Обновить“», причём «Обновить» на карточке обновляет СБОРКУ С СЕРВЕРА и к
    /// копии из Steam отношения не имеет. Настоящее действие пряталось в
    /// контекстном меню списка игр, куда никто не заглядывает.
    /// </para>
    /// <para>
    /// Теперь каждый пункт либо запускает, либо доводит до запуска сам, и только
    /// по-настоящему невозможное остаётся выключенным с причиной.
    /// </para>
    /// </summary>
    internal enum LaunchAction {
        /// <summary>Всё на месте — запустить.</summary>
        Play,

        /// <summary>Поставить или обновить модпак в этой папке, потом запустить.</summary>
        InstallMods,

        /// <summary>Скачать сборку с сервера; модпак приедет вместе с ней.</summary>
        InstallGame,

        /// <summary>Сборка на диске устарела — докатить.</summary>
        Update,

        /// <summary>Сделать нельзя; причина в <see cref="LaunchOption.Note"/>.</summary>
        Unavailable,
    }

    /// <summary>
    /// Один пункт меню кнопки «Играть».
    /// </summary>
    /// <param name="Target">Что именно запускается.</param>
    /// <param name="Title">Подпись для пользователя.</param>
    /// <param name="GameDir">Папка игры.</param>
    /// <param name="Modded">Включать ли моды.</param>
    /// <param name="Action">Что произойдёт по нажатию.</param>
    /// <param name="Note">Что именно сделает пункт или почему не может; пусто, если просто запустит.</param>
    internal sealed record LaunchOption(
        LaunchTarget Target,
        string Title,
        string GameDir,
        bool Modded,
        LaunchAction Action,
        string Note) {
        /// <summary>Запускается ли эта копия через Steam.</summary>
        internal bool ViaSteam => this.Target is LaunchTarget.SteamModded or LaunchTarget.SteamVanilla;

        /// <summary>Можно ли вообще нажать на пункт.</summary>
        internal bool Available => this.Action != LaunchAction.Unavailable;

        /// <summary>Запустится ли игра прямо сейчас, без установки.</summary>
        internal bool ReadyToPlay => this.Action == LaunchAction.Play;

        /// <summary>Строка пункта меню целиком.</summary>
        internal string MenuText => string.IsNullOrEmpty(this.Note) ? this.Title : $"{this.Title} — {this.Note}";
    }

    /// <summary>
    /// Всё, что нужно знать, чтобы решить, чем сейчас является каждый из вариантов.
    /// <para>
    /// Отдельной записью, а не семью аргументами подряд: половина из них — булевы,
    /// и перепутанные местами «установлена» и «нужно обновление» дали бы не ошибку
    /// сборки, а предложение обновить то, чего на диске нет.
    /// </para>
    /// </summary>
    /// <param name="Mods">Настройки модов игры с сервера.</param>
    /// <param name="LocalRoot">Папка сборки с сервера.</param>
    /// <param name="LocalInstalled">Установлена ли сборка с сервера.</param>
    /// <param name="LocalNeedsUpdate">Отличается ли сборка на диске от эталона.</param>
    /// <param name="HasServerBuild">Есть ли у игры сборка на сервере вообще.</param>
    /// <param name="Steam">Результат поиска копии в Steam.</param>
    /// <param name="SteamModsVersion">Версия модпака, стоящего в копии из Steam; пусто — не стоит.</param>
    internal sealed record LaunchContext(
        ModsInfo? Mods,
        string LocalRoot,
        bool LocalInstalled,
        bool LocalNeedsUpdate,
        bool HasServerBuild,
        SteamGame Steam,
        string SteamModsVersion);

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
        /// Короткая подпись варианта — для подсказки на кнопке «Играть».
        /// <para>
        /// Отдельно от <see cref="Options"/>: там подпись строится вместе с
        /// доступностью и требует поиска копии в Steam, а подсказке нужно только имя.
        /// </para>
        /// </summary>
        /// <param name="target">Вариант запуска.</param>
        /// <param name="mods">Настройки модов игры; null допустим.</param>
        /// <returns>Подпись варианта.</returns>
        internal static string TitleOf(LaunchTarget target, ModsInfo? mods) {
            var packName = mods?.Describe() ?? string.Empty;
            var modded = string.IsNullOrEmpty(packName) ? "с модами" : $"с модами ({packName})";
            return target switch {
                LaunchTarget.SteamModded => $"Steam · {modded}",
                LaunchTarget.SteamVanilla => "Steam · без модов",
                LaunchTarget.LocalModded => $"Сборка Chill Hub · {modded}",
                _ => "Сборка Chill Hub · без модов",
            };
        }

        /// <summary>
        /// Составляет варианты запуска игры.
        /// <para>
        /// Каждый пункт доводит дело до конца сам: не установлены моды в копию Steam —
        /// пункт их поставит, не скачана сборка с сервера — скачает, устарела —
        /// обновит. Выключенным остаётся только по-настоящему невозможное, и тогда в
        /// <see cref="LaunchOption.Note"/> лежит причина: исчезнувший пункт не
        /// объясняет игроку ничего, а «Steam не установлен» — объясняет.
        /// </para>
        /// <para>
        /// Пунктов четыре, но если у игры нет сборки на сервере — их два. Такая игра
        /// живёт только копией из Steam, и предлагать «Сборку Chill Hub», которой
        /// негде взяться, значит обещать несуществующее.
        /// </para>
        /// </summary>
        /// <param name="ctx">Что известно об игре и её копиях прямо сейчас.</param>
        /// <returns>Варианты в порядке показа.</returns>
        internal static IReadOnlyList<LaunchOption> Options(LaunchContext ctx) {
            var options = new List<LaunchOption>();
            var mods = ctx.Mods;
            var hasPack = mods is { HasLatest: true };
            var steamOk = ctx.Steam.Ok;
            var steamReason = DescribeSteam(ctx.Steam);

            LaunchOption Make(LaunchTarget target, string dir, bool modded, LaunchAction action, string note)
                => new(target, TitleOf(target, mods), dir, modded, action, note);

            // --- копия из Steam, с модами -------------------------------------
            if (!steamOk) {
                options.Add(Make(LaunchTarget.SteamModded, ctx.Steam.GameDir, true, LaunchAction.Unavailable, steamReason));
            }
            else if (!hasPack) {
                options.Add(Make(LaunchTarget.SteamModded, ctx.Steam.GameDir, true, LaunchAction.Unavailable, "модпак ещё не опубликован"));
            }
            else if (string.IsNullOrWhiteSpace(ctx.SteamModsVersion)) {
                options.Add(Make(LaunchTarget.SteamModded, ctx.Steam.GameDir, true, LaunchAction.InstallMods, "установить моды"));
            }
            else if (!string.Equals(ctx.SteamModsVersion.Trim(), (mods!.Version ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) {
                options.Add(Make(LaunchTarget.SteamModded, ctx.Steam.GameDir, true, LaunchAction.InstallMods, "обновить моды"));
            }
            else if (!DoorstopConfig.IsInstalled(ctx.Steam.GameDir)) {
                // Версия записана, а загрузчика нет: обычное дело после того, как Steam
                // восстановил свои файлы поверх модов.
                options.Add(Make(LaunchTarget.SteamModded, ctx.Steam.GameDir, true, LaunchAction.InstallMods, "восстановить моды"));
            }
            else {
                options.Add(Make(LaunchTarget.SteamModded, ctx.Steam.GameDir, true, LaunchAction.Play, string.Empty));
            }

            // --- копия из Steam, без модов ------------------------------------
            options.Add(Make(
                LaunchTarget.SteamVanilla, ctx.Steam.GameDir, false,
                steamOk ? LaunchAction.Play : LaunchAction.Unavailable,
                steamOk ? string.Empty : steamReason));

            if (!ctx.HasServerBuild) {
                return options;
            }

            // --- сборка с сервера, с модами -----------------------------------
            if (!ctx.LocalInstalled) {
                options.Add(Make(LaunchTarget.LocalModded, ctx.LocalRoot, true, LaunchAction.InstallGame, "установить игру с модами"));
            }
            else if (ctx.LocalNeedsUpdate) {
                options.Add(Make(LaunchTarget.LocalModded, ctx.LocalRoot, true, LaunchAction.Update, "обновить"));
            }
            else if (!hasPack) {
                options.Add(Make(LaunchTarget.LocalModded, ctx.LocalRoot, true, LaunchAction.Unavailable, "модпак ещё не опубликован"));
            }
            else if (!DoorstopConfig.IsInstalled(ctx.LocalRoot)) {
                options.Add(Make(LaunchTarget.LocalModded, ctx.LocalRoot, true, LaunchAction.Update, "восстановить моды"));
            }
            else {
                options.Add(Make(LaunchTarget.LocalModded, ctx.LocalRoot, true, LaunchAction.Play, string.Empty));
            }

            // --- сборка с сервера, без модов ----------------------------------
            if (!ctx.LocalInstalled) {
                options.Add(Make(LaunchTarget.LocalVanilla, ctx.LocalRoot, false, LaunchAction.InstallGame, "установить игру"));
            }
            else if (ctx.LocalNeedsUpdate) {
                options.Add(Make(LaunchTarget.LocalVanilla, ctx.LocalRoot, false, LaunchAction.Update, "обновить"));
            }
            else {
                options.Add(Make(LaunchTarget.LocalVanilla, ctx.LocalRoot, false, LaunchAction.Play, string.Empty));
            }

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
            if (!option.ReadyToPlay) {
                // Пункт, которому ещё нужна установка, сюда попадать не должен: его
                // обрабатывает экран, а не этот метод. Проверка — на случай, если
                // однажды попадёт, и тогда лучше отказ в журнале, чем запуск игры,
                // у которой модов нет.
                Logging.Logger.Warn($"[mods] запуск отклонён: {option.Target} — {option.Note}");
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
