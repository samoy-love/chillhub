// <copyright file="ModPackFiles.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Core.Sync;

    /// <summary>Что осталось в папке от установленного модпака.</summary>
    /// <param name="Known">
    /// Есть ли вообще с чем сверяться: копия манифеста модпака лежит в этой папке.
    /// false — модпак сюда не ставили (или память о нём потеряна), и говорить о его
    /// целости нечего.
    /// </param>
    /// <param name="Total">Сколько файлов числится за модпаком.</param>
    /// <param name="Missing">Сколько из них пропало.</param>
    internal readonly record struct ModPackState(bool Known, int Total, int Missing) {
        /// <summary>Модпак заявлен установленным, но на диске он неполон.</summary>
        internal bool Broken => this.Known && this.Missing > 0;

        /// <summary>Строка для журнала: без неё в отчёте видно только «восстановить моды».</summary>
        /// <returns>Короткое описание находки.</returns>
        public override string ToString() =>
            this.Known ? $"файлов={this.Total} пропало={this.Missing}" : "модпак не установлен";
    }

    /// <summary>
    /// Лежат ли на месте файлы установленного модпака.
    /// <para>
    /// УСТАНОВЛЕННОСТЬ МОДПАКА ДО СИХ ПОР ОПРЕДЕЛЯЛ ОДИН МАРКЕР ВЕРСИИ. Игрок удалял мод
    /// из папки руками — маркер оставался, и лаунчер по-прежнему обещал «Steam · с
    /// модами», запуская игру без того мода. Ни обновление списка, ни проверка файлов
    /// расхождения не видели: список сверяет версии, а проверка ходила только в сборку
    /// Chill Hub.
    /// </para>
    /// <para>
    /// Спрашивается ТОЛЬКО наличие файла — ни хеши, ни размер. Хеши здесь неуместны:
    /// вопрос задаётся при каждом пересчёте вариантов запуска, то есть на UI-потоке и
    /// часто. А размер сравнивать нельзя вовсе: модпак приносит с собой свои
    /// <c>BepInEx/config/*.cfg</c>, и BepInEx переписывает их при запуске игры,
    /// дописывая настройки новых модов. По размеру такой файл «испорчен» сразу после
    /// первой же сессии — и кнопка звала бы восстанавливать моды снова и снова, починяя
    /// то, что не ломалось. Сверка с хешами осталась за «Проверить файлы».
    /// </para>
    /// <para>
    /// Каталоги обходятся по одному разу, а не по файлу за раз: у модпака под тысячу
    /// файлов на восемь десятков папок, и разница между восемью десятками обходов и
    /// тысячей обращений к диску заметна там, где вопрос задаётся раз в секунду.
    /// </para>
    /// <para>
    /// РАЗБОР МАНИФЕСТА ЗАПОМИНАЕТСЯ, ОБХОД ПАПОК — НЕТ. Вопрос задаётся до двух раз в
    /// секунду (копия из Steam и сборка с сервера) и на UI-потоке, а список файлов
    /// модпака — это чтение и разбор JSON на тысячу записей, всякий раз одинаковый.
    /// Ключ запоминания — время правки и размер самого файла манифеста: меняется он
    /// только установкой модпака, и тогда список читается заново. А вот ПРОПАЖУ файла
    /// манифест не отражает никак, поэтому папки обходятся каждый раз — иначе
    /// удалённый руками мод остался бы незамеченным до следующей установки.
    /// </para>
    /// </summary>
    internal static class ModPackFiles {
        /// <summary>Сколько папок помним. Дальше кеш чистится целиком: он не про попадания, а про повтор.</summary>
        private const int MaxRemembered = 32;

        /// <summary>
        /// Разобранные списки файлов по папкам: ключ — папка игры. Больше пары записей
        /// здесь не живёт (у игры две копии), но игр за сеанс перебирают много, поэтому
        /// на всякий случай стоит потолок.
        /// </summary>
        private static readonly ConcurrentDictionary<string, WantedFiles> WantedCache =
            new ConcurrentDictionary<string, WantedFiles>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Смотрит, всё ли на месте.</summary>
        /// <param name="root">Папка игры: копия из Steam или сборка с сервера.</param>
        /// <returns>Что нашлось.</returns>
        internal static ModPackState Inspect(string? root) {
            try {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                    return default;
                }

                var wanted = WantedFor(root);
                if (wanted == null) {
                    return default;
                }

                return Compare(root, wanted);
            }
            catch (Exception ex) {
                // Не смогли посмотреть — молчим о поломке. Ошибка в эту сторону стоит
                // ненайденного мода, в обратную — навязанной переустановки полутора
                // гигабайт у того, у кого всё в порядке.
                Logging.Logger.Warn($"[mods] ModPackFiles.Inspect('{root}'): {ex.Message}");
                return default;
            }
        }

        /// <summary>Цел ли модпак в этой папке. Незнание поломкой не считается.</summary>
        /// <param name="root">Папка игры.</param>
        /// <returns>true, если модпак установлен и чего-то из него нет.</returns>
        internal static bool Broken(string? root) => Inspect(root).Broken;

        /// <summary>
        /// Раскладывает файлы манифеста по папкам: ключ — папка относительно корня.
        /// <para>
        /// <c>doorstop_config.ini</c> из списка выпадает: его правит сам лаунчер при
        /// каждом переключении «с модами / без модов». Список берётся оттуда же, откуда
        /// его берёт планировщик, — расходиться этим двум местам нельзя.
        /// </para>
        /// </summary>
        /// <param name="manifest">Копия установленного манифеста модпака.</param>
        /// <returns>Файлы по папкам.</returns>
        private static Dictionary<string, List<string>> Wanted(Manifest manifest) {
            var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in PlanOptions.ModPackSelfManagedPaths) {
                preserve.Add(SimpleSyncService.NormalizeRel(p));
            }

            var byDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files) {
                var rel = SimpleSyncService.NormalizeRel(f?.Path);
                if (rel.Length == 0 || preserve.Contains(rel)) {
                    continue;
                }

                var cut = rel.LastIndexOf('/');
                var dir = cut < 0 ? string.Empty : rel.Substring(0, cut);
                if (!byDir.TryGetValue(dir, out var list)) {
                    list = new List<string>();
                    byDir[dir] = list;
                }

                list.Add(rel.Substring(cut + 1));
            }

            return byDir;
        }

        /// <summary>
        /// Список файлов модпака этой папки — из памяти, если файл манифеста с прошлого
        /// раза не менялся. null — модпака здесь нет.
        /// </summary>
        /// <param name="root">Папка игры.</param>
        /// <returns>Имена файлов модпака по папкам.</returns>
        private static Dictionary<string, List<string>>? WantedFor(string root) {
            var manifestPath = Path.Combine(root, Home.GameLocalState.ModsManifestFileName);
            var info = new FileInfo(manifestPath);
            if (!info.Exists) {
                WantedCache.TryRemove(root, out _);
                return null;
            }

            if (WantedCache.TryGetValue(root, out var remembered)
                && remembered.Stamp == info.LastWriteTimeUtc.Ticks
                && remembered.Length == info.Length) {
                return remembered.ByDir;
            }

            var manifest = Home.GameLocalState.ReadInstalledModPackManifest(root);
            if (manifest?.Files == null || manifest.Files.Count == 0) {
                WantedCache.TryRemove(root, out _);
                return null;
            }

            var byDir = Wanted(manifest);
            if (WantedCache.Count >= MaxRemembered) {
                WantedCache.Clear();
            }

            WantedCache[root] = new WantedFiles(info.LastWriteTimeUtc.Ticks, info.Length, byDir);
            return byDir;
        }

        /// <summary>Сверяет ожидаемое с тем, что лежит в папках.</summary>
        /// <param name="root">Папка игры.</param>
        /// <param name="byDir">Имена файлов модпака по папкам.</param>
        /// <returns>Что нашлось.</returns>
        private static ModPackState Compare(string root, Dictionary<string, List<string>> byDir) {
            var total = 0;
            var missing = 0;

            foreach (var (dir, names) in byDir) {
                total += names.Count;
                var present = NamesIn(Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar)));
                if (present == null) {
                    // Папки нет вовсе — пропал весь её кусок модпака.
                    missing += names.Count;
                    continue;
                }

                foreach (var name in names) {
                    if (!present.Contains(name)) {
                        missing++;
                    }
                }
            }

            return new ModPackState(true, total, missing);
        }

        /// <summary>Имена файлов папки; null — папки нет.</summary>
        /// <param name="dir">Полный путь к папке.</param>
        /// <returns>Что лежит в папке.</returns>
        private static HashSet<string>? NamesIn(string dir) {
            if (!Directory.Exists(dir)) {
                return null;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(dir)) {
                names.Add(Path.GetFileName(path));
            }

            return names;
        }

        /// <summary>Запомненный разбор манифеста одной папки.</summary>
        /// <param name="Stamp">Время правки файла манифеста.</param>
        /// <param name="Length">Его размер.</param>
        /// <param name="ByDir">Имена файлов модпака по папкам.</param>
        private sealed record WantedFiles(long Stamp, long Length, Dictionary<string, List<string>> ByDir);
    }
}
