// <copyright file="LocalDonors.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Update;

    /// <summary>
    /// Папка, из которой можно взять уже лежащий на диске файл вместо скачивания.
    /// </summary>
    /// <param name="Root">Корень папки-донора.</param>
    /// <param name="Files">Что в ней стоит: путь манифеста → запись манифеста.</param>
    public sealed record DonorRoot(string Root, IReadOnlyDictionary<string, ManifestFile> Files);

    /// <summary>
    /// Поиск файлов, которые уже есть на диске в другой копии той же игры.
    /// <para>
    /// ОДИН И ТОТ ЖЕ МОДПАК ПРИЕЗЖАЛ ПО СЕТИ СТОЛЬКО РАЗ, ВО СКОЛЬКО ПАПОК ЕГО СТАВИЛИ.
    /// Модпак принадлежит папке: чтобы играть и в копию из Steam, и в сборку с сервера,
    /// его кладут в обе. План загрузки при этом смотрит только в свой корень — файл с
    /// нужным хешем в соседней папке для него не существует, — и полтора гигабайта
    /// качались повторно, при том что побайтово это те же файлы.
    /// </para>
    /// <para>
    /// Что именно лежит в соседней папке, известно точно: рядом с файлами хранится копия
    /// установленного манифеста — та самая, по которой считается принадлежность файлов.
    /// Хешам оттуда доверия ровно столько, сколько нужно: скопированный файл всё равно
    /// проходит ту же сверку, что и скачанный, и при расхождении молча уезжает в обычную
    /// загрузку.
    /// </para>
    /// </summary>
    public static class LocalDonors {
        /// <summary>
        /// Собирает доноров из установленных модпаков указанных папок.
        /// <para>
        /// Читает диск, поэтому вызывать стоит оттуда же, откуда строится план, — не с
        /// UI-потока.
        /// </para>
        /// </summary>
        /// <param name="roots">Папки-кандидаты; пустые и повторяющиеся отбрасываются.</param>
        /// <param name="exclude">Папка, в которую идёт установка: сама себе донором не бывает.</param>
        /// <returns>Доноры, у которых есть что предложить.</returns>
        public static IReadOnlyList<DonorRoot> FromModPacks(IEnumerable<string?>? roots, string? exclude = null) {
            if (roots == null) {
                return Array.Empty<DonorRoot>();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(exclude)) {
                seen.Add(Normalize(exclude));
            }

            var donors = new List<DonorRoot>();
            foreach (var root in roots) {
                if (string.IsNullOrWhiteSpace(root) || !seen.Add(Normalize(root))) {
                    continue;
                }

                var manifest = Home.GameLocalState.ReadInstalledModPackManifest(root);
                if (manifest?.Files == null || manifest.Files.Count == 0) {
                    continue;
                }

                var files = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.Files) {
                    if (f == null || string.IsNullOrWhiteSpace(f.Path)) {
                        continue;
                    }

                    files[f.Path.Replace('\\', '/').TrimStart('/')] = f;
                }

                if (files.Count > 0) {
                    donors.Add(new DonorRoot(root!, files));
                    Logging.Logger.Info($"[sync] донор '{root}': {files.Count} файл(ов) модпака '{manifest.Version}'");
                }
            }

            return donors;
        }

        /// <summary>
        /// Ищет готовый файл под задачу загрузки.
        /// <para>
        /// Совпасть обязаны и путь, и размер, и оба хеша, какие есть в манифесте: путь
        /// сам по себе ничего не значит — под одним и тем же именем в соседней папке
        /// вполне может лежать другая версия мода.
        /// </para>
        /// </summary>
        /// <param name="donors">Где искать.</param>
        /// <param name="task">Что нужно.</param>
        /// <returns>Полный путь к файлу-донору или null.</returns>
        public static string? Find(IReadOnlyList<DonorRoot>? donors, FileTask task) {
            if (donors == null || donors.Count == 0 || task == null) {
                return null;
            }

            var rel = task.RelativePath.Replace('\\', '/').TrimStart('/');
            foreach (var donor in donors) {
                if (!donor.Files.TryGetValue(rel, out var known) || known == null) {
                    continue;
                }

                // Хотя бы одно СРАВНЕНИЕ должно состояться: два пустых хеша с обеих
                // сторон — это не совпадение, а отсутствие сведений, и брать по нему
                // чужой файл нельзя.
                var compared = false;
                if (!Match(known.Blake3, task.Blake3, ref compared)
                    || !Match(known.Sha256, task.Sha256, ref compared)
                    || !compared) {
                    continue;
                }

                string path;
                try {
                    path = ManifestPath.Combine(donor.Root, rel);
                }
                catch (Exception ex) {
                    // Путь из чужого манифеста, который не лёг в корень донора: это не
                    // повод падать — просто скачаем файл, как раньше.
                    Logging.Logger.Warn($"[sync] донор '{donor.Root}' пропущен для '{rel}': {ex.Message}");
                    continue;
                }

                try {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length == task.Size) {
                        return path;
                    }
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"[sync] донор '{path}' недоступен: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Сверяет одну пару хешей. Пустой с любой стороны — не отказ, но и не
        /// подтверждение: у манифеста может не быть sha256, и тогда решает blake3.
        /// </summary>
        /// <param name="left">Хеш донора.</param>
        /// <param name="right">Хеш из задачи.</param>
        /// <param name="compared">Ставится в true, если сравнение действительно состоялось.</param>
        /// <returns>false только при настоящем расхождении.</returns>
        private static bool Match(string? left, string? right, ref bool compared) {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) {
                return true;
            }

            compared = true;
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Приводит путь папки к виду, по которому их сравнивают.</summary>
        /// <param name="root">Папка.</param>
        /// <returns>Нормализованный путь.</returns>
        private static string Normalize(string? root)
            => (root ?? string.Empty).Trim().TrimEnd('\\', '/');
    }
}
