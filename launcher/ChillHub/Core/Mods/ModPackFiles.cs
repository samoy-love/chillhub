// <copyright file="ModPackFiles.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
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
    /// <param name="Damaged">Сколько лежит, но другого размера, чем ставили.</param>
    internal readonly record struct ModPackState(bool Known, int Total, int Missing, int Damaged) {
        /// <summary>Модпак заявлен установленным, но на диске он неполон.</summary>
        internal bool Broken => this.Known && (this.Missing > 0 || this.Damaged > 0);

        /// <summary>Строка для журнала: без неё в отчёте видно только «восстановить моды».</summary>
        /// <returns>Короткое описание находки.</returns>
        public override string ToString() =>
            this.Known ? $"файлов={this.Total} пропало={this.Missing} не того размера={this.Damaged}" : "модпак не установлен";
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
    /// Сверяются наличие и размер, а не хеши: это ответ на вопрос «модпак ещё цел?»,
    /// который задаётся при каждом пересчёте вариантов запуска — то есть на UI-потоке
    /// и часто. Пересчёт хешей полутора гигабайт там неуместен, а удалённый или
    /// обрезанный файл виден и так. Настоящая сверка с хешами остаётся за «Проверить
    /// файлы», которая от этого никуда не делась.
    /// </para>
    /// <para>
    /// Каталоги обходятся по одному разу, а не по файлу за раз: у модпака полторы
    /// тысячи файлов на полсотни папок, и разница между пятьюдесятью обходами и
    /// полутора тысячами обращений к диску заметна там, где вопрос задаётся раз в
    /// секунду.
    /// </para>
    /// </summary>
    internal static class ModPackFiles {
        /// <summary>Смотрит, всё ли на месте.</summary>
        /// <param name="root">Папка игры: копия из Steam или сборка с сервера.</param>
        /// <returns>Что нашлось.</returns>
        internal static ModPackState Inspect(string? root) {
            try {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                    return default;
                }

                var manifest = Home.GameLocalState.ReadInstalledModPackManifest(root);
                if (manifest?.Files == null || manifest.Files.Count == 0) {
                    return default;
                }

                return Compare(root, Wanted(manifest));
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
        /// каждом переключении «с модами / без модов», и размер файла при этом меняется.
        /// Список берётся оттуда же, откуда его берёт планировщик, — расходиться этим
        /// двум местам нельзя.
        /// </para>
        /// </summary>
        /// <param name="manifest">Копия установленного манифеста модпака.</param>
        /// <returns>Файлы по папкам.</returns>
        private static Dictionary<string, List<ManifestFile>> Wanted(Manifest manifest) {
            var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in PlanOptions.ModPackSelfManagedPaths) {
                preserve.Add(SimpleSyncService.NormalizeRel(p));
            }

            var byDir = new Dictionary<string, List<ManifestFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files) {
                var rel = SimpleSyncService.NormalizeRel(f?.Path);
                if (rel.Length == 0 || preserve.Contains(rel)) {
                    continue;
                }

                var cut = rel.LastIndexOf('/');
                var dir = cut < 0 ? string.Empty : rel.Substring(0, cut);
                if (!byDir.TryGetValue(dir, out var list)) {
                    list = new List<ManifestFile>();
                    byDir[dir] = list;
                }

                list.Add(f!);
            }

            return byDir;
        }

        /// <summary>Сверяет ожидаемое с тем, что лежит в папках.</summary>
        /// <param name="root">Папка игры.</param>
        /// <param name="byDir">Файлы модпака по папкам.</param>
        /// <returns>Что нашлось.</returns>
        private static ModPackState Compare(string root, Dictionary<string, List<ManifestFile>> byDir) {
            var total = 0;
            var missing = 0;
            var damaged = 0;

            foreach (var (dir, files) in byDir) {
                total += files.Count;
                var sizes = SizesIn(Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar)));
                if (sizes == null) {
                    // Папки нет вовсе — пропал весь её кусок модпака.
                    missing += files.Count;
                    continue;
                }

                foreach (var f in files) {
                    var name = f.Path.Substring(f.Path.LastIndexOfAny(new[] { '/', '\\' }) + 1);
                    if (!sizes.TryGetValue(name, out var onDisk)) {
                        missing++;
                    }
                    else if (f.Size > 0 && onDisk != f.Size) {
                        damaged++;
                    }
                }
            }

            return new ModPackState(true, total, missing, damaged);
        }

        /// <summary>Размеры файлов папки по именам; null — папки нет.</summary>
        /// <param name="dir">Полный путь к папке.</param>
        /// <returns>Имя файла — его размер.</returns>
        private static Dictionary<string, long>? SizesIn(string dir) {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) {
                return null;
            }

            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in info.EnumerateFiles()) {
                sizes[file.Name] = file.Length;
            }

            return sizes;
        }
    }
}
