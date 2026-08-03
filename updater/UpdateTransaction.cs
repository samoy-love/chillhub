// <copyright file="UpdateTransaction.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Update;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Транзакция замены файлов в папке установки.
/// <para>
/// Апдейтер пишет поверх БОЕВОЙ установки: единственной копии лаунчера на машине.
/// Пока запись шла прямо в целевой файл (<c>FileMode.Create</c>), любая остановка
/// посреди процесса — снятое питание, убитый процесс, кончившееся место —
/// оставляла усечённый или нулевой <c>ChillHub.dll</c>, а старого не оставалось
/// нигде. Лаунчер после этого не стартует, а значит и обновиться сам уже не может:
/// восстановление возможно только переустановкой.
/// </para>
/// <para>
/// Поэтому каждый файл кладётся рядом во временный, подменяет цель одной операцией
/// и оставляет бэкап. Пока транзакция не подтверждена, бэкапы живы и
/// <see cref="Rollback"/> возвращает установку в исходное состояние целиком.
/// </para>
/// </summary>
public sealed class UpdateTransaction {
    private readonly List<Entry> entries = new();

    /// <summary>
    /// Пути, для которых бэкап уже снят. Имя бэкапа выводится из имени цели
    /// (<c>файл.chbak</c>) и потому одно на все записи в один и тот же путь.
    /// </summary>
    private readonly HashSet<string> backed = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<string> log;

    /// <summary>Initializes a new instance of the <see cref="UpdateTransaction"/> class.</summary>
    /// <param name="log">Логгер.</param>
    public UpdateTransaction(Action<string> log) {
        this.log = log ?? (_ => { });
    }

    /// <summary>Gets количество файлов, изменённых в рамках транзакции.</summary>
    public int Count => this.entries.Count;

    /// <summary>
    /// Ставит файл на место назначения атомарно, сохранив предыдущее содержимое.
    /// </summary>
    /// <param name="sourceFile">Файл-источник (не изменяется).</param>
    /// <param name="destFile">Целевой путь в папке установки.</param>
    public void CopyFile(string sourceFile, string destFile) {
        var dir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        var tmp = destFile + AtomicFile.TempSuffix;
        var existed = File.Exists(destFile);

        // ВТОРАЯ запись в тот же путь бэкап НЕ снимает.
        //
        // Имя бэкапа детерминировано, а AtomicFile.Replace перед подменой сносит старый
        // бэкап. Значит повторное копирование того же файла (починка после расхождения
        // хешей в VerifyAsync либо дубль строки в filelist) затирало бы единственную
        // копию ИСХОДНОГО содержимого, оставляя вместо неё содержимое первой копии —
        // то есть уже новое. Откат после этого «успешно восстанавливал» новый файл
        // и давал ровно ту смесь старых и новых сборок, ради которой транзакция и заведена.
        //
        // Исходное содержимое сохранила первая запись; последующие идут без бэкапа
        // и не добавляют вторую запись в журнал — откат обязан вернуться к состоянию
        // ДО транзакции, а не к промежуточному.
        var known = this.backed.Contains(destFile);
        var backup = existed && !known ? destFile + AtomicFile.BackupSuffix : null;

        try {
            using (var srcFs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dstFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                srcFs.CopyTo(dstFs);
                dstFs.Flush(true);
            }

            AtomicFile.Replace(tmp, destFile, backup);
        }
        catch {
            AtomicFile.TryDelete(tmp);
            throw;
        }

        // Запись в журнал делаем ТОЛЬКО после успешной подмены: иначе откат
        // попытался бы «восстановить» бэкап, которого нет, и снёс бы живой файл.
        if (!known) {
            this.backed.Add(destFile);
            this.entries.Add(new Entry(destFile, backup, created: !existed));
        }
    }

    /// <summary>
    /// Подтверждает транзакцию: бэкапы больше не нужны и удаляются.
    /// </summary>
    public void Commit() {
        var left = 0;
        foreach (var e in this.entries) {
            if (e.Backup != null && !AtomicFile.TryDelete(e.Backup)) {
                left++;
            }
        }

        if (left > 0) {
            this.log($"commit: {left} backup file(s) could not be removed; they are harmless and will be cleaned next run");
        }

        this.entries.Clear();
        this.backed.Clear();
    }

    /// <summary>
    /// Откатывает транзакцию: возвращает бэкапы на место, удаляет созданные файлы.
    /// Идёт в обратном порядке — так последний применённый файл откатывается первым.
    /// </summary>
    public void Rollback() {
        var restored = 0;
        var failed = 0;
        for (var i = this.entries.Count - 1; i >= 0; i--) {
            var e = this.entries[i];
            try {
                if (e.Created) {
                    AtomicFile.TryDelete(e.Path);
                    restored++;
                    continue;
                }

                if (e.Backup == null) {
                    continue;
                }

                if (File.Exists(e.Backup)) {
                    AtomicFile.Replace(e.Backup, e.Path, backup: null);
                    restored++;
                    continue;
                }

                // Бэкап есть в журнале, но не на диске — исходное содержимое
                // потеряно, и на месте файла осталось новое. Молчать об этом нельзя:
                // «rollback: failed=0» читается как «установка вернулась в прежнее
                // состояние», а она в этот момент смешанная.
                failed++;
                this.log($"ROLLBACK FAILED for {e.Path}: бэкап '{e.Backup}' отсутствует, прежнее содержимое НЕ восстановлено");
            }
            catch (Exception ex) {
                failed++;
                this.log($"ROLLBACK FAILED for {e.Path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        this.log($"rollback: restored={restored} failed={failed} of {this.entries.Count} change(s)");
        this.entries.Clear();
        this.backed.Clear();
    }

    /// <summary>
    /// Убирает из папки установки временные файлы и бэкапы, оставшиеся от прерванного прогона.
    /// </summary>
    /// <param name="root">Папка установки.</param>
    /// <param name="log">Логгер.</param>
    public static void CleanupLeftovers(string root, Action<string> log) {
        try {
            if (!Directory.Exists(root)) {
                return;
            }

            var removed = 0;
            foreach (var p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                if (p.EndsWith(AtomicFile.TempSuffix, StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(AtomicFile.BackupSuffix, StringComparison.OrdinalIgnoreCase)) {
                    if (AtomicFile.TryDelete(p)) {
                        removed++;
                    }
                }
            }

            if (removed > 0) {
                log($"cleanup: removed {removed} leftover temp/backup file(s)");
            }
        }
        catch (Exception ex) {
            log($"cleanup leftovers error: {ex.Message}");
        }
    }

    private readonly struct Entry {
        public Entry(string path, string? backup, bool created) {
            this.Path = path;
            this.Backup = backup;
            this.Created = created;
        }

        public string Path { get; }

        public string? Backup { get; }

        /// <summary>Gets a value indicating whether файла раньше не было (откат = удалить).</summary>
        public bool Created { get; }
    }
}
