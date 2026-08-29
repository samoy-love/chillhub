// <copyright file="InstallFingerprint.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Слепок папки игры: сколько в ней файлов, сколько они весят и когда последний из них
    /// менялся.
    /// </summary>
    /// <param name="Files">Число файлов игры (служебные не в счёт).</param>
    /// <param name="Bytes">Их суммарный размер.</param>
    /// <param name="LatestWriteTicks">Самое позднее время изменения среди них, UTC-тики.</param>
    public sealed record FolderFingerprint(int Files, long Bytes, long LatestWriteTicks);

    /// <summary>
    /// Быстрый ответ на вопрос «файлы игры на месте и с прошлого раза не менялись».
    /// <para>
    /// ЗАЧЕМ ОН НУЖЕН. Раньше на каждом запуске лаунчер для КАЖДОЙ установленной игры
    /// скачивал манифест и строил полный план различий: обходил все его файлы, сверял у
    /// каждого размер и время, а при промахе кеша считал хеш. Для сборки в пятнадцать
    /// тысяч файлов это секунды дисковой работы — и так по каждой игре, при каждом старте,
    /// притом что почти всегда ответ один и тот же: «ничего не изменилось».
    /// </para>
    /// <para>
    /// ПОЧЕМУ ЭТО НАДЁЖНО. Слепок не заменяет проверку целостности, а отвечает на другой,
    /// более дешёвый вопрос: трогал ли кто-нибудь папку с тех пор, как мы её проверили.
    /// Он снимается обходом каталогов БЕЗ чтения содержимого файлов, поэтому дёшев, и
    /// расходится от любого практического повреждения: удалили файл — изменится счётчик,
    /// подменили — размер или время, оборвали обновление — и то и другое. Совпал — значит,
    /// папка ровно та, которую мы уже признали целой. Разошёлся — идём длинным путём, как
    /// раньше.
    /// </para>
    /// <para>
    /// Чего слепок НЕ ловит: правку файла с сохранением его размера И времени изменения.
    /// Для этого есть явная проверка целостности из «Об игре», и она никуда не делась.
    /// </para>
    /// </summary>
    public static class InstallFingerprint {
        /// <summary>Имя файла со слепком в папке игры.</summary>
        public const string FileName = ".integrity.json";

        /// <summary>Снимает слепок папки прямо сейчас.</summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <returns>Слепок; для отсутствующей папки — пустой.</returns>
        public static FolderFingerprint Compute(string? localRoot) {
            var files = 0;
            var bytes = 0L;
            var latest = 0L;

            try {
                if (string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot)) {
                    return new FolderFingerprint(0, 0, 0);
                }

                foreach (var path in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)) {
                    var rel = Path.GetRelativePath(localRoot, path).Replace('\\', '/');

                    // Служебные файлы не в счёт: их пишет сам лаунчер, и без этого слепок
                    // расходился бы сам с собой — запись .version меняла бы то, что слепок
                    // только что зафиксировал.
                    if (rel.StartsWith(".staging/", StringComparison.OrdinalIgnoreCase)
                        || SimpleSyncService.IsServiceRelFile(rel)) {
                        continue;
                    }

                    var info = new FileInfo(path);
                    files++;
                    bytes += info.Length;

                    var ticks = info.LastWriteTimeUtc.Ticks;
                    if (ticks > latest) {
                        latest = ticks;
                    }
                }
            }
            catch (Exception ex) {
                // Слепок — ускорение, а не источник правды: не снялся, значит пойдём
                // длинным путём. Ронять из-за этого проверку статуса нельзя.
                Logging.Logger.Warn($"InstallFingerprint.Compute('{localRoot}'): {ex.Message}");
                return new FolderFingerprint(0, 0, 0);
            }

            return new FolderFingerprint(files, bytes, latest);
        }

        /// <summary>Читает сохранённый слепок; null — его нет или он нечитаем.</summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <returns>Слепок или null.</returns>
        public static FolderFingerprint? Read(string? localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot)) {
                    return null;
                }

                var path = Path.Combine(localRoot, FileName);
                if (!File.Exists(path)) {
                    return null;
                }

                var stored = JsonSerializer.Deserialize<FolderFingerprint>(File.ReadAllText(path));

                // Нулевой слепок ничем не отличается от пустой папки: считаем, что его нет,
                // иначе пустая папка «подтверждала» бы сама себя.
                return stored is { Files: > 0 } ? stored : null;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"InstallFingerprint.Read('{localRoot}'): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Запоминает слепок папки: игру только что скачали или проверили, и с этого
        /// момента любое расхождение означает, что её трогали снаружи.
        /// </summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <returns>true, если слепок записан.</returns>
        public static bool Save(string? localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot)) {
                    return false;
                }

                var fingerprint = Compute(localRoot);
                if (fingerprint.Files == 0) {
                    // Пустую папку запоминать нечего: игры в ней нет.
                    return false;
                }

                var json = JsonSerializer.Serialize(fingerprint, new JsonSerializerOptions { WriteIndented = true });
                ChillHub.Update.AtomicFile.WriteAllText(
                    Path.Combine(localRoot, FileName), json, SelfUpdate.SelfUpdateRules.Utf8NoBom);
                return true;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"InstallFingerprint.Save('{localRoot}'): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Совпадает ли папка с тем, какой её запомнили. Отсутствие слепка — это НЕ
        /// совпадение: у игры, поставленной до появления слепков, его просто нет, и
        /// проверить её надо полным путём (заодно слепок и появится).
        /// </summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <returns>true, если папку с прошлой проверки не трогали.</returns>
        public static bool Matches(string? localRoot) {
            var stored = Read(localRoot);
            return stored != null && stored == Compute(localRoot);
        }
    }
}
