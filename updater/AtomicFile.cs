// <copyright file="AtomicFile.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Update;

using System;
using System.IO;
using System.Text;

/// <summary>
/// Запись файла «всё или ничего».
/// <para>
/// Обычный <c>File.WriteAllText</c> — это truncate + write: между этими двумя
/// операциями файл существует и он ПУСТОЙ. Для маркера версии
/// (<c>launcher.version</c>) это худший из возможных исходов: пустой маркер
/// читается как «версия неизвестна», и обновление после этого не предлагается
/// уже никогда. Поэтому содержимое сначала целиком попадает во временный файл
/// рядом с целевым (тот же том — значит замена атомарна), и только потом
/// подменяет цель одной операцией файловой системы.
/// </para>
/// </summary>
public static class AtomicFile {
    /// <summary>Суффикс временного файла. Виден только в момент записи.</summary>
    public const string TempSuffix = ".chtmp";

    /// <summary>Суффикс файла-бэкапа, который оставляет <c>File.Replace</c>.</summary>
    public const string BackupSuffix = ".chbak";

    /// <summary>
    /// Пишет текст в файл атомарно: временный файл рядом + подмена.
    /// </summary>
    /// <param name="path">Целевой файл.</param>
    /// <param name="content">Содержимое.</param>
    /// <param name="encoding">Кодировка (для служебных файлов — UTF-8 без BOM).</param>
    public static void WriteAllText(string path, string content, Encoding encoding) {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + TempSuffix;
        try {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                var bytes = encoding.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            Replace(tmp, path, backup: null);
        }
        finally {
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// Подменяет <paramref name="destination"/> файлом <paramref name="source"/>.
    /// <para>
    /// Если цель существует и <paramref name="backup"/> задан — старое содержимое
    /// сохраняется в бэкап (это и есть основа отката). Если цель занята другим
    /// процессом, <c>File.Replace</c> падает; тогда цель переименовывается в бэкап
    /// (переименование открытого файла Windows разрешает), а новый файл встаёт на её место.
    /// </para>
    /// </summary>
    /// <param name="source">Новый файл (после успеха его больше нет).</param>
    /// <param name="destination">Целевой путь.</param>
    /// <param name="backup">Путь бэкапа либо <c>null</c>, если бэкап не нужен.</param>
    public static void Replace(string source, string destination, string? backup) {
        if (!File.Exists(destination)) {
            File.Move(source, destination);
            return;
        }

        try {
            var fi = new FileInfo(destination);
            if (fi.IsReadOnly) {
                fi.IsReadOnly = false;
            }
        }
        catch {
            // Снять read-only не удалось — пусть решает сама операция замены.
        }

        if (backup != null) {
            TryDelete(backup);
        }

        try {
            if (backup != null) {
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            }
            else {
                File.Move(source, destination, overwrite: true);
            }

            return;
        }
        catch (IOException) when (backup != null) {
            // Цель занята: уводим её в сторону и ставим новый файл на освободившееся имя.
            File.Move(destination, backup);
            try {
                File.Move(source, destination);
            }
            catch {
                // Новый файл встать не смог — возвращаем старый, иначе останется дыра.
                File.Move(backup, destination);
                throw;
            }
        }
    }

    /// <summary>Удаляет файл, не бросая исключений (снимая read-only).</summary>
    /// <param name="path">Путь к файлу.</param>
    /// <returns>true, если файла больше нет.</returns>
    public static bool TryDelete(string path) {
        try {
            if (!File.Exists(path)) {
                return true;
            }

            var fi = new FileInfo(path);
            if (fi.IsReadOnly) {
                fi.IsReadOnly = false;
            }

            File.Delete(path);
            return true;
        }
        catch {
            return false;
        }
    }
}
