// <copyright file="SelfUpdateCleanup.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Update;

    /// <summary>
    /// Уборка следов прошлых обновлений: каталогов сессий в %TEMP% и служебных
    /// файлов апдейтера, которые старые версии «зеркалили» в папку установки.
    /// Всё best-effort: уборка мусора не должна мешать запуску лаунчера.
    /// </summary>
    internal static class SelfUpdateCleanup {
        /// <summary>
        /// Сколько каталогов версий в %TEMP%\ChillHub\SelfUpdate оставляем при уборке (19b):
        /// самый свежий (его мог только что использовать апдейтер) и один предыдущий.
        /// </summary>
        internal const int KeepTempSessionDirs = 2;

        /// <summary>
        /// A14. Возраст, после которого каталог сессии удаляется независимо от того,
        /// входит ли он в число самых свежих: иначе у пользователя, который давно не
        /// обновлялся, пара каталогов-ветеранов лежала бы в %TEMP% вечно.
        /// </summary>
        internal const int StaleSessionDays = 7;

        /// <summary>Суффикс имени каталога, отложенного до следующей уборки (A14).</summary>
        internal const string TrashSuffix = ".trash-";

        /// <summary>
        /// 19b. Уборка %TEMP%\ChillHub\SelfUpdate от каталогов старых версий.
        ///
        /// Раньше чистилась только подпапка updater, а сами каталоги версий оставались навсегда —
        /// по копии пакета обновления на каждую версию. Теперь каталоги старых версий удаляются
        /// целиком; оставляем самые свежие: в новейшем может ещё дописывать лог апдейтер,
        /// который только что перезапустил лаунчер.
        ///
        /// Всё best-effort: залоченные файлы просто доживают до следующего запуска.
        /// </summary>
        /// <param name="root">Корень временных сессий обновления.</param>
        internal static void TryCleanupTempSelfUpdateDirs(string root) {
            try {
                if (!Directory.Exists(root)) {
                    return;
                }

                var dirs = new List<DirectoryInfo>();
                foreach (var p in Directory.EnumerateDirectories(root)) {
                    try {
                        // A14. Хвосты прошлых уборок: каталог, который не удалялся из-за
                        // залоченного файла, отправлялся в *.trash-* и добивается здесь.
                        if (Path.GetFileName(p).Contains(TrashSuffix, StringComparison.OrdinalIgnoreCase)) {
                            TryDeleteDirectoryBestEffort(p);
                            continue;
                        }

                        dirs.Add(new DirectoryInfo(p));
                    }
                    catch {
                    }
                }

                // Свежие — в начало списка.
                dirs.Sort((a, b) => DirStamp(b).CompareTo(DirStamp(a)));

                for (var i = 0; i < dirs.Count; i++) {
                    var dir = dirs[i].FullName;

                    // A14. «Свежесть» по позиции в списке недостаточна: если обновлений
                    // давно не было, два каталога-ветерана хранились бы вечно.
                    var stale = DirStamp(dirs[i]) < DateTime.UtcNow.AddDays(-StaleSessionDays);
                    if (i < KeepTempSessionDirs && !stale) {
                        // Свежие сессии целиком не сносим, но копию апдейтера из них выносим:
                        // старая раскладка (updater прямо в папке версии) и новая (work\updater).
                        TryDeleteDirectoryBestEffort(Path.Combine(dir, PreserveMatcher.UpdaterArtifactDir));
                        TryDeleteDirectoryBestEffort(Path.Combine(dir, "work", PreserveMatcher.UpdaterArtifactDir));
                        continue;
                    }

                    TryDeleteDirectoryBestEffort(dir);
                }
            }
            catch {
            }
        }

        /// <summary>Время последнего изменения каталога; при ошибке — «очень давно».</summary>
        internal static DateTime DirStamp(DirectoryInfo d) {
            try {
                var w = d.LastWriteTimeUtc;
                var c = d.CreationTimeUtc;
                return w > c ? w : c;
            }
            catch {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Удаляет каталог, переживая залоченные файлы и атрибут «только чтение».
        /// Ничего не бросает: уборка мусора не должна мешать запуску лаунчера.
        /// </summary>
        /// <param name="path">Каталог для удаления.</param>
        internal static void TryDeleteDirectoryBestEffort(string path) {
            try {
                if (!Directory.Exists(path)) {
                    return;
                }

                try {
                    Directory.Delete(path, true);
                    return;
                }
                catch {
                }

                // Не вышло с первого раза: снимаем read-only и выносим файлы поштучно,
                // чтобы освободить место даже если один файл кем-то занят.
                var locked = new List<string>();
                try {
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) {
                        try {
                            var attrs = File.GetAttributes(f);
                            if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                                File.SetAttributes(f, attrs & ~(FileAttributes.ReadOnly | FileAttributes.System));
                            }

                            File.Delete(f);
                        }
                        catch {
                            locked.Add(f);
                        }
                    }
                }
                catch {
                }

                try {
                    Directory.Delete(path, true);
                    return;
                }
                catch {
                }

                // Каталог всё ещё занят. Раньше на этом уборка заканчивалась, и залоченный
                // каталог оставался в %TEMP% НАВСЕГДА: имя занято, при следующем обновлении
                // той же версии сессия создавалась поверх чужих остатков. Пробуем увести его
                // в сторону, чтобы освободить имя.
                //
                // ПОЛУЧАЕТСЯ НЕ ВСЕГДА, и прежний комментарий здесь обещал лишнего.
                // Переименовать каталог с открытым внутри файлом Windows разрешает ровно
                // тогда, когда держатель открыл файл с FILE_SHARE_DELETE. Так поступает
                // загрузчик образов, поэтому каталог с ЗАПУЩЕННЫМ ChillHub.Updater.exe
                // переименуется. А файл, открытый обычным FileStream без общего доступа на
                // удаление, переименование каталога заблокирует — и мы останемся под старым
                // именем.
                //
                // Тогда каталог доживает до следующего запуска, но не обязательно до
                // ближайшей уборки: TryCleanupTempSelfUpdateDirs пропускает
                // KeepTempSessionDirs самых свежих сессий, пока им меньше StaleSessionDays.
                // То есть занятый каталог может пролежать под своим именем до недели.
                try {
                    var trash = path + TrashSuffix + Guid.NewGuid().ToString("N").Substring(0, 8);
                    Directory.Move(path, trash);
                    path = trash;
                    try {
                        Directory.Delete(path, true);
                        return;
                    }
                    catch {
                    }
                }
                catch {
                }

                // Последний рубеж: просим систему удалить остатки при перезагрузке.
                // Работает не всегда (нужны права на HKLM), поэтому именно последний.
                foreach (var f in locked) {
                    try {
                        NativeMethods.MoveFileEx(f, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                    catch {
                    }
                }

                try {
                    Logging.Logger.Warn($"SelfUpdate temp cleanup: каталог занят и оставлен до следующего запуска: {path}");
                }
                catch {
                }
            }
            catch {
            }
        }

        /// <summary>
        /// A6. Разовая очистка папки установки от служебных файлов апдейтера,
        /// которые прошлые версии «зеркалили» из TEMP (filelist.txt, apply-update.log, updater\ и т.п.).
        /// </summary>
        /// <param name="baseDir">Папка установки лаунчера.</param>
        internal static void TryCleanupInstalledUpdaterArtifacts(string baseDir) {
            try {
                foreach (var name in PreserveMatcher.UpdaterArtifactFiles) {
                    try {
                        var p = Path.Combine(baseDir, name);
                        if (File.Exists(p)) {
                            File.Delete(p);
                        }
                    }
                    catch {
                    }
                }

                try {
                    var dir = Path.Combine(baseDir, PreserveMatcher.UpdaterArtifactDir);
                    if (Directory.Exists(dir)) {
                        Directory.Delete(dir, true);
                    }
                }
                catch {
                }
            }
            catch {
            }
        }

        private static class NativeMethods {
            internal const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            internal static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
        }
    }
}
