// <copyright file="SelfUpdateApplier.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;

    using ChillHub.Update;

    /// <summary>Чем закончилась попытка применить обновление.</summary>
    internal enum SelfUpdateApplyResult {
        /// <summary>Скачанного пакета на месте нет.</summary>
        PackageMissing,

        /// <summary>A10. Комплект апдейтера подготовлен не полностью — гасить лаунчер нельзя.</summary>
        UpdaterIncomplete,

        /// <summary>A3. Каталог установки уже занят другим апдейтером.</summary>
        LockBusy,

        /// <summary>A8. Апдейтер не стартовал — приложение не закрываем.</summary>
        StartFailed,

        /// <summary>Апдейтер работает: пора завершать приложение.</summary>
        Started,

        /// <summary>Непредвиденный отказ на этапе применения.</summary>
        Error,
    }

    /// <summary>
    /// Запуск внешнего апдейтера: подготовка его копии в %TEMP%, сборка аргументов,
    /// замок на каталог установки и собственно старт процесса.
    /// <para>
    /// Самая опасная часть самообновления: после успешного старта лаунчер завершается
    /// и файлы работающей установки заменяет чужой процесс. Любой отказ ДО этого
    /// момента обязан оставить лаунчер живым — иначе пользователь остаётся без
    /// приложения, которое уже не может обновиться само.
    /// </para>
    /// </summary>
    internal sealed class SelfUpdateApplier {
        private readonly SelfUpdatePaths paths;
        private readonly UpdateAttemptsStore attempts;
        private readonly Action<SelfUpdateUiState> apply;
        private readonly Func<ProcessStartInfo, Process?> startProcess;

        internal SelfUpdateApplier(
            SelfUpdatePaths paths,
            UpdateAttemptsStore attempts,
            Action<SelfUpdateUiState> apply,
            Func<ProcessStartInfo, Process?>? startProcess = null) {
            this.paths = paths;
            this.attempts = attempts;
            this.apply = apply;
            this.startProcess = startProcess ?? (psi => Process.Start(psi));
        }

        /// <summary>Применение (подготовка апдейтера, аргументы и перезапуск).</summary>
        /// <param name="pendingTempRoot">Каталог с полезной нагрузкой.</param>
        /// <param name="pendingWorkDir">Служебный каталог сессии.</param>
        /// <param name="remoteVersion">Версия, на которую обновляемся.</param>
        /// <param name="stripPrefix">Общая корневая папка пакета.</param>
        /// <returns>Результат: <see cref="SelfUpdateApplyResult.Started"/> — можно завершать приложение.</returns>
        internal SelfUpdateApplyResult Apply(string? pendingTempRoot, string? pendingWorkDir, string? remoteVersion, string stripPrefix) {
            try {
                if (string.IsNullOrWhiteSpace(pendingTempRoot) || !Directory.Exists(pendingTempRoot)) {
                    this.apply(new SelfUpdateUiState {
                        StatusText = "Не найден пакет обновления.",
                        ButtonEnabled = true,
                    });
                    return SelfUpdateApplyResult.PackageMissing;
                }

                var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetExecutingAssembly().Location;

                // Надёжнее берем корень через AppDomain (папка запуска)
                var targetDir = this.paths.TargetDir;
                var selfUpdateDir = pendingWorkDir ?? this.paths.WorkDir(remoteVersion ?? "pending");
                var logPath = Path.Combine(selfUpdateDir, "apply-update.log");
                Directory.CreateDirectory(selfUpdateDir);

                var pid = Process.GetCurrentProcess().Id;

                // Pre-create log with header for the native updater
                try {
                    // Use a single interpolated string to avoid writing literal placeholders
                    var header = $"[{DateTime.Now:o}] Apply started. SRC={pendingTempRoot} DST={targetDir} EXE={currentExe} PID={pid}\r\n";
                    File.WriteAllText(logPath, header, SelfUpdateRules.Utf8NoBom);
                }
                catch {
                }

                // Prepare native updater in TEMP so DST copies can be freely replaced
                var tempUpdaterDir = Path.Combine(selfUpdateDir, PreserveMatcher.UpdaterArtifactDir);
                try {
                    Directory.CreateDirectory(tempUpdaterDir);
                }
                catch {
                }

                var updaterPath = Path.Combine(tempUpdaterDir, "YourLauncher.Updater.exe");
                var missing = PrepareUpdaterPayload(targetDir, tempUpdaterDir);

                if (missing.Count > 0) {
                    // A8. Без полного комплекта апдейтера гасить приложение нельзя —
                    // пользователь просто потеряет лаунчер.
                    this.apply(new SelfUpdateUiState {
                        StatusText =
                            "Модуль обновления подготовлен не полностью, обновление не применено:\n" +
                            string.Join("\n", missing.Take(5)) + "\n" +
                            $"Каталог: {tempUpdaterDir}\n" +
                            "Попробуйте ещё раз или переустановите лаунчер вручную.",
                        ButtonEnabled = true,
                    });
                    try {
                        Logging.Logger.Error(
                            new FileNotFoundException("Updater payload incomplete: " + string.Join("; ", missing), updaterPath),
                            "UpdateWindow.ApplyUpdate");
                    }
                    catch {
                    }

                    return SelfUpdateApplyResult.UpdaterIncomplete;
                }

                var exeArgsPath = WriteExeArgs(selfUpdateDir);

                var psi = BuildStartInfo(
                    updaterPath,
                    tempUpdaterDir,
                    pendingTempRoot!,
                    targetDir,
                    currentExe,
                    pid,
                    logPath,
                    selfUpdateDir,
                    exeArgsPath,
                    remoteVersion,
                    stripPrefix);

                // A3. Замок на каталог установки держит работающий апдейтер. Если он
                // занят — обновление уже применяется (второй экземпляр лаунчера, двойной
                // клик, зависший прошлый прогон). Запускать второй апдейтер в ту же
                // папку нельзя: два процесса перемешают файлы и бэкапы, и откат любого
                // из них оставит смесь версий.
                if (UpdateLock.IsBusy(targetDir)) {
                    this.apply(new SelfUpdateUiState {
                        StatusText =
                            "Обновление уже применяется другим процессом.\n" +
                            "Дождитесь его завершения и запустите лаунчер снова.",
                        ButtonEnabled = true,
                    });
                    try {
                        Logging.Logger.Warn($"Self-update skipped: install lock is busy ({targetDir})");
                    }
                    catch {
                    }

                    return SelfUpdateApplyResult.LockBusy;
                }

                Process? started = null;
                Exception? startError = null;
                try {
                    started = this.startProcess(psi);
                }
                catch (Exception ex) {
                    startError = ex;
                }

                if (started == null) {
                    // A8. Апдейтер не стартовал — НЕ закрываем приложение.
                    this.apply(new SelfUpdateUiState {
                        StatusText = $"Не удалось запустить модуль обновления:\n{updaterPath}\n{startError?.Message ?? "процесс не создан"}",
                        ButtonEnabled = true,
                    });
                    try {
                        Logging.Logger.Error(startError ?? new InvalidOperationException("Process.Start returned null"), "UpdateWindow.StartUpdater");
                    }
                    catch {
                    }

                    return SelfUpdateApplyResult.StartFailed;
                }

                // Фиксируем попытку только когда апдейтер реально запущен (A1: защита от петли).
                this.attempts.Register(remoteVersion ?? string.Empty);

                // Завершаем приложение: освобождаем файлы и даём скрипту применить обновление
                this.apply(new SelfUpdateUiState {
                    StatusText = $"Применение обновления...\nUpdater: {updaterPath}\nLog: {logPath}",
                });
                return SelfUpdateApplyResult.Started;
            }
            catch (Exception ex) {
                this.apply(new SelfUpdateUiState {
                    StatusText = $"Ошибка применения обновления: {ex.Message}",
                    ButtonEnabled = true,
                });
                try {
                    Logging.Logger.Error(ex, "UpdateWindow.ApplyUpdate");
                }
                catch {
                }

                return SelfUpdateApplyResult.Error;
            }
        }

        /// <summary>
        /// A10. Кладёт ВЕСЬ комплект апдейтера в %TEMP% и проверяет его целиком, а не только .exe.
        /// <para>
        /// Апдейтер — обычное framework-dependent приложение: без .dll и
        /// .runtimeconfig.json его apphost падает мгновенно. Раньше проверялось
        /// наличие одного YourLauncher.Updater.exe — если остальное не скопировалось
        /// (антивирус, нет места, залоченный файл), лаунчер всё равно делал Shutdown,
        /// апдейтер тут же умирал, и пользователь оставался вообще без приложения.
        /// </para>
        /// </summary>
        /// <param name="targetDir">Каталог установки — источник файлов апдейтера.</param>
        /// <param name="tempUpdaterDir">Куда кладём копию.</param>
        /// <returns>Список того, чего не хватает; пустой список — комплект полон.</returns>
        internal static List<string> PrepareUpdaterPayload(string targetDir, string tempUpdaterDir) {
            var updaterPath = Path.Combine(tempUpdaterDir, "YourLauncher.Updater.exe");
            var missing = new List<string>();
            try {
                var sources = Directory.EnumerateFiles(targetDir, "YourLauncher.Updater*", SearchOption.TopDirectoryOnly).ToList();
                if (sources.Count == 0) {
                    missing.Add("YourLauncher.Updater.* (в папке установки нет ни одного файла модуля обновления)");
                }

                foreach (var f in sources) {
                    var name = Path.GetFileName(f);
                    var dstF = Path.Combine(tempUpdaterDir, name);
                    try {
                        File.Copy(f, dstF, true);

                        // Копия обязана совпадать по размеру: усечённая копия — это
                        // тот же мгновенный крах, только без внятного сообщения.
                        var srcLen = new FileInfo(f).Length;
                        var dstLen = new FileInfo(dstF).Length;
                        if (srcLen != dstLen) {
                            missing.Add($"{name} (скопировано {dstLen} из {srcLen} байт)");
                        }
                    }
                    catch (Exception ex) {
                        missing.Add($"{name} ({ex.Message})");
                    }
                }
            }
            catch (Exception ex) {
                missing.Add($"перечисление файлов модуля обновления: {ex.Message}");
            }

            if (!File.Exists(updaterPath)) {
                missing.Add("YourLauncher.Updater.exe");
            }

            return missing;
        }

        /// <summary>
        /// A9. Исходные аргументы командной строки лаунчера — в файл (по строке
        /// на аргумент). Раньше они просто терялись: апдейтер поднимал лаунчер
        /// «голым», и запуск с параметром (например, автозапуск игры) молча
        /// превращался в обычный старт. Файл вместо строки — чтобы ничего не
        /// экранировать и не разбирать заново.
        /// </summary>
        /// <param name="selfUpdateDir">Служебный каталог сессии.</param>
        /// <returns>Путь к файлу либо пустая строка, если записать не вышло.</returns>
        internal static string WriteExeArgs(string selfUpdateDir) {
            var exeArgsPath = Path.Combine(selfUpdateDir, "exeargs.txt");
            try {
                var original = Environment.GetCommandLineArgs();
                var carry = new List<string>();
                for (var i = 1; i < original.Length; i++) {
                    var a = original[i] ?? string.Empty;

                    // Перевод строки в аргументе разрушил бы построчный формат.
                    if (a.Contains('\n') || a.Contains('\r')) {
                        continue;
                    }

                    carry.Add(a);
                }

                File.WriteAllLines(exeArgsPath, carry, SelfUpdateRules.Utf8NoBom);
            }
            catch {
                exeArgsPath = string.Empty;
            }

            return exeArgsPath;
        }

        /// <summary>
        /// Собирает команду запуска апдейтера.
        /// <para>
        /// A6. ArgumentList вместо ручной сборки строки. Прежний Q() экранировал
        /// только кавычку и не удваивал бэкслеши, поэтому путь, заканчивающийся
        /// на '\' (а каталог установки — ровно такой случай), съедал закрывающую
        /// кавычку и склеивал соседние аргументы. ArgumentList делает это по
        /// правилам Windows и не требует от нас ничего угадывать.
        /// </para>
        /// </summary>
        /// <returns>Готовый к запуску ProcessStartInfo.</returns>
        internal static ProcessStartInfo BuildStartInfo(
            string updaterPath,
            string tempUpdaterDir,
            string srcRoot,
            string targetDir,
            string currentExe,
            int pid,
            string logPath,
            string selfUpdateDir,
            string exeArgsPath,
            string? remoteVersion,
            string stripPrefix) {
            var psi = new ProcessStartInfo {
                FileName = updaterPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempUpdaterDir,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            void A(string key, string value) {
                psi.ArgumentList.Add(key);
                psi.ArgumentList.Add(value);
            }

            A("--src", srcRoot);
            A("--dst", targetDir);
            A("--exe", currentExe);
            A("--parent", pid.ToString(CultureInfo.InvariantCulture));
            A("--log", logPath);
            A("--files", Path.Combine(selfUpdateDir, "filelist.txt"));
            A("--dirs", Path.Combine(selfUpdateDir, "emptydirs.txt"));
            A("--del", Path.Combine(selfUpdateDir, "deletelist.txt"));
            if (!string.IsNullOrWhiteSpace(exeArgsPath)) {
                A("--exe-args-file", exeArgsPath);
            }

            if (!string.IsNullOrWhiteSpace(remoteVersion)) {
                A("--version", remoteVersion!);
            }

            // A10. Strip-prefix считаем на стороне лаунчера (по манифесту) и запрещаем автодетект,
            // чтобы обе стороны одинаково понимали пути.
            A("--auto-strip", "false");
            if (stripPrefix.Length > 0) {
                A("--strip-prefix", stripPrefix);
            }

            // A2. Preserve-правила берём из общего PreserveMatcher, а не из строкового литерала.
            A("--preserve", PreserveMatcher.DefaultRulesArg);

            return psi;
        }
    }
}
