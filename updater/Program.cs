using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ChillHub.Update;

internal static class Program {
    // Коды возврата: 0 — успех, 2 — были ошибки копирования (обновление НЕ применено полностью), 3 — фатальная ошибка.
    // Читать их некому (родителя к этому моменту уже нет), поэтому исход дублируется
    // в файл состояния рядом с маркером версии — см. UpdateStatus (A12).
    private const int ExitOk = 0;
    private const int ExitCopyErrors = 2;
    private const int ExitFatal = 3;

    /// <summary>Сколько ждём освобождения замка на каталог установки (A3).</summary>
    private const int LockWaitMs = 30_000;

    /// <summary>Сколько ждём выхода родительского процесса.</summary>
    private const int ParentWaitMs = 120_000;

    // Все служебные списки пишем в UTF-8 БЕЗ BOM: BOM ломает сверку размера/хеша
    // (например, launcher.version становится 10 байт вместо 8).
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public static Task<int> Main(string[] args) => RunMainAsync(args, new UpdaterHost());

    /// <summary>
    /// Точка входа с подставляемым швом к процессам.
    /// </summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="host">Ожидание родителя и запуск лаунчера.</param>
    /// <returns>Код возврата процесса.</returns>
    internal static async Task<int> RunMainAsync(string[] args, UpdaterHost host) {
        var log = new UpdateLog();
        var ctx = new RunContext();
        var exit = ExitFatal;

        // A5/A9. Ни одна строчка подготовки больше не живёт вне try: раньше
        // отсутствующий --log или несоздаваемый каталог убивали процесс до
        // единственной записи в журнал — и лаунчер, который к этому моменту уже
        // завершился, не перезапускал никто. Теперь любой исход проходит через
        // finally: состояние на диск, лаунчер обратно на экран.
        try {
            exit = await RunAsync(args, log, ctx, host);
        }
        catch (Exception ex) {
            log.Write($"fatal: {ex}");
            ctx.Outcome = "fatal";
            ctx.Message = $"{ex.GetType().Name}: {ex.Message}";
            exit = ExitFatal;
        }
        finally {
            UpdateLock.Release(ctx.Lock);
            WriteStatus(ctx, exit, log);
            Restart(ctx, log, host);
            log.Write($"updater finished with exit code {exit} ({ctx.Outcome})");
        }

        return exit;
    }

    /// <summary>
    /// Разбор командной строки, замок на каталог установки и ожидание родителя.
    /// <para>
    /// Всё, что здесь происходит, завязано на окружение процесса: аргументы,
    /// именованный мьютекс, чужой pid. Сама работа с файлами вынесена в
    /// <see cref="ApplyAsync"/> — ради неё апдейтер и существует, и проверять её
    /// нужно без запуска процессов.
    /// </para>
    /// </summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="log">Журнал.</param>
    /// <param name="ctx">Состояние прогона для блока finally.</param>
    /// <param name="host">Шов к процессам операционной системы.</param>
    /// <returns>Код возврата.</returns>
    internal static async Task<int> RunAsync(string[] args, UpdateLog log, RunContext ctx, UpdaterHost host) {
        var argsMap = ParseArgs(args);
        string Req(string key) {
            if (!argsMap.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) {
                throw new ArgumentException($"Missing required option {key}");
            }
            return v!;
        }
        string Opt(string key, string def = "") => (argsMap.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) ? v! : def;

        // Журнал открываем ПЕРВЫМ делом: всё, что случится дальше, должно попасть в файл.
        log.Open(Opt("--log"));

        // A9. Каталог установки и exe лаунчера запоминаем СРАЗУ, до остальных
        // обязательных аргументов. Пока они присваивались после всех проверок,
        // отсутствие любого другого аргумента оставляло блок finally без путей:
        // ни файла состояния (писать некуда), ни перезапуска (запускать нечего) —
        // а лаунчер к этому моменту уже закрыл себя сам.
        var dst = Req("--dst");
        ctx.Dst = dst;
        var exe = Req("--exe");
        ctx.Exe = exe;
        ctx.ExeArgsFile = Opt("--exe-args-file");
        var src = Req("--src");

        // A13. Молча превращать мусор в parent=0 нельзя: это отказ от ожидания
        // родителя, то есть копирование поверх ЖИВОГО лаунчера. Половина файлов
        // залочена, обновление разваливается — и никто не понимает почему.
        // Отсутствующее значение опаснее мусора: "--parent --dst C:\app" (ключ
        // без значения) выглядит как обычная команда, поэтому его нельзя добирать
        // значением по умолчанию — только фатальный отказ.
        var parentStr = argsMap.TryGetValue("--parent", out var parentRaw) ? parentRaw : null;
        if (!TryParseParentPid(parentStr, out var parent, out var parentProblem)) {
            log.Write($"FATAL: --parent: {parentProblem}; ждать родителя нечего, копировать поверх работающего лаунчера нельзя");
            ctx.Outcome = "fatal";
            ctx.Message = $"Некорректный аргумент --parent: {parentProblem}.";
            return ExitFatal;
        }

        var files = Opt("--files", string.Empty);
        var dirs = Opt("--dirs", string.Empty);
        var del = Opt("--del", string.Empty);
        var strip = Opt("--strip-prefix", string.Empty);
        // --auto-strip false отключает автоопределение корневой папки архива:
        // лаунчер считает strip-prefix сам по манифесту и передаёт его явно (см. A10).
        var autoStrip = !string.Equals(Opt("--auto-strip", "true"), "false", StringComparison.OrdinalIgnoreCase);
        var preserve = Opt("--preserve", PreserveMatcher.DefaultRulesArg);
        var newVersion = Opt("--version", string.Empty);
        ctx.Version = newVersion.Trim();

        // Log all options and basic file stats
        string ExistsStat(string? p) => string.IsNullOrWhiteSpace(p) ? "<null>" : ($"'{p}' exists={(File.Exists(p) ? "file" : Directory.Exists(p) ? "dir" : "no")} ");
        log.Write($"Updater start\n  --src={ExistsStat(src)}\n  --dst={ExistsStat(dst)}\n  --exe={ExistsStat(exe)}\n  --parent={parent}\n  --log='{log.Path}'\n  --files={ExistsStat(files)}\n  --dirs={ExistsStat(dirs)}\n  --del={ExistsStat(del)}\n  --strip-prefix='{strip}'\n  --auto-strip={autoStrip}\n  --preserve='{preserve}'\n  --exe-args-file={ExistsStat(ctx.ExeArgsFile)}");

        // A3. Замок на каталог установки. Два апдейтера в одной папке — это
        // перемешанные бэкапы и невосстановимая смесь версий.
        if (!UpdateLock.TryAcquire(dst, LockWaitMs, out var mutex)) {
            log.Write($"FATAL: другой процесс уже применяет обновление в '{dst}' (ждали {LockWaitMs / 1000} с)");
            ctx.Outcome = "busy";
            ctx.Message = "Обновление уже применяется другим процессом.";

            // Перезапуск лаунчера — забота того апдейтера, который держит замок.
            ctx.Restart = false;
            return ExitFatal;
        }

        ctx.Lock = mutex;
        log.Write($"install lock acquired: {UpdateLock.MutexName(dst)}");

        host.WaitForParent(parent, log);

        return await ApplyAsync(
            new ApplyRequest {
                Src = src,
                Dst = dst,
                Files = files,
                Dirs = dirs,
                Del = del,
                Strip = strip,
                AutoStrip = autoStrip,
                Preserve = preserve,
                Version = newVersion,
            },
            log,
            ctx);
    }

    /// <summary>
    /// Применяет обновление к папке установки: копирование, сверка, удаления,
    /// пустые каталоги, маркер версии.
    /// <para>
    /// Здесь не запускается ни один процесс и не ждётся ни один pid — только файлы.
    /// Это и есть та часть, ошибка в которой оставляет пользователя с неработающим
    /// лаунчером, поэтому она отделена от окружения процесса и проверяется целиком.
    /// </para>
    /// <para>
    /// Порядок операций менять нельзя: транзакция → сверка → удаления/маркер →
    /// коммит. Любая ошибка до коммита откатывает установку в исходное состояние.
    /// </para>
    /// </summary>
    /// <param name="req">Что и куда применять.</param>
    /// <param name="log">Журнал.</param>
    /// <param name="ctx">Состояние прогона (исход, сообщение, версия).</param>
    /// <returns>Код возврата: 0 — успех, 2 — обновление не доехало, 3 — фатально.</returns>
    internal static async Task<int> ApplyAsync(ApplyRequest req, UpdateLog log, RunContext ctx) {
        var src = req.Src;
        var dst = req.Dst;
        var files = req.Files;
        var dirs = req.Dirs;
        var del = req.Del;
        var strip = req.Strip;
        var autoStrip = req.AutoStrip;
        var preserve = req.Preserve;
        var newVersion = req.Version;

        ctx.Dst = dst;
        ctx.Version = newVersion.Trim();

        // Ensure dst
        try { Directory.CreateDirectory(dst); } catch (Exception ex) { log.Write($"create dst error: {ex.Message}"); }

        // A2. Права на запись проверяем ОДИН РАЗ и заранее. Отказ в доступе —
        // не временная помеха: раньше он ретраился по 10 раз с бэкоффом на КАЖДЫЙ
        // файл (~26 секунд), и на сотне файлов это десятки минут тишины,
        // после которых обновление всё равно не применялось.
        var accessProblem = DescribeWriteAccess(dst);
        if (accessProblem != null) {
            log.Write($"FATAL: нет прав на запись в '{dst}': {accessProblem}");
            ctx.Outcome = "access-denied";
            ctx.Message = $"Нет прав на запись в папку установки '{dst}'. Обновление не применялось.";
            return ExitFatal;
        }

        // Detect strip prefix if not provided (только если автоопределение разрешено)
        if (string.IsNullOrWhiteSpace(strip) && autoStrip) {
            strip = DetectStripPrefix(src, files, log.Write) ?? strip;
        }
        log.Write($"effective strip-prefix='{strip}'");

        // Пути из списков — данные, а не команды. Апдейтер пишет в папку УСТАНОВКИ
        // и работает с правами пользователя (а после UAC — и выше), поэтому запись
        // по пути с ".." или "C:\..." уводит файл куда угодно: в автозагрузку,
        // в System32, в чужой профиль. Проверяем ВСЕ списки до первой операции
        // и отказываемся целиком: частично применённое обновление хуже неприменённого.
        if (!ValidateLists(new[] { files, dirs, del }, strip, log.Write)) {
            log.Write("FATAL: списки содержат небезопасные пути, обновление не применялось");
            ctx.Outcome = "fatal";
            ctx.Message = "Списки обновления содержат небезопасные пути.";
            return ExitFatal;
        }

        // Preserve rules: единый матчер, общий с лаунчером (ChillHub.Update.PreserveMatcher)
        var matcher = new PreserveMatcher(preserve);
        try { log.Write($"preserve rules: [{string.Join(", ", matcher.Rules)}]"); } catch { }

        bool ShouldPreserve(string rel, string reason)
            => matcher.ShouldPreserve(rel, m => log.Write($"skip {reason}: {m}"));

        LogLists(files, dirs, del, log);

        // Хвосты прерванного прогона (*.chtmp/*.chbak) убираем ДО начала работы,
        // иначе они смешаются с бэкапами текущей транзакции.
        UpdateTransaction.CleanupLeftovers(dst, log.Write);

        var tx = new UpdateTransaction(log.Write);
        var copyErrors = 0;
        var copyOk = 0;

        // B2. Копирование идёт через транзакцию: каждый файл встаёт на место
        // атомарной подменой, старое содержимое лежит в бэкапе. Пока транзакция
        // не подтверждена, откат возвращает установку в исходное состояние целиком.
        async Task<bool> CopyFileAsync(string sourceFile, string destFile) {
            const int maxAttempts = 5;
            var attempt = 0;
            while (true) {
                try {
                    tx.CopyFile(sourceFile, destFile);
                    copyOk++;
                    return true;
                }
                catch (UnauthorizedAccessException ex) {
                    // A2. Права не появятся от повторов — прерываем весь проход.
                    throw new UpdaterAbortException(
                        $"отказ в доступе при записи '{destFile}': {ex.Message}. " +
                        "Проверьте права на папку установки и антивирус; обновление не применено.",
                        ex);
                }
                catch (Exception ex) {
                    attempt++;
                    if (attempt >= maxAttempts) {
                        copyErrors++;
                        log.Write($"copy FAILED (giving up after {maxAttempts}) {sourceFile} -> {destFile}: {ex.Message}");
                        return false;
                    }
                    var delay = Math.Min(2000, 200 * (int)Math.Pow(2, Math.Max(0, attempt - 1)));
                    log.Write($"copy retry {attempt}/{maxAttempts} {sourceFile}: {ex.Message}; {delay}ms");
                    await Task.Delay(delay);
                }
            }
        }

        // A12 (диффовый режим). Лаунчер посчитал план против ПАПКИ УСТАНОВКИ и скачал
        // только изменившиеся файлы. Значит SRC — это не полный пакет, а дифф,
        // и «остаточное зеркалирование» всего SRC больше не нужно.
        var haveFileList = !string.IsNullOrWhiteSpace(files) && File.Exists(files);
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var integrityErrors = 0;

        try {
            // If file list provided, copy them first (diff), respecting strip-prefix
            if (haveFileList) {
                foreach (var rel in File.ReadAllLines(files, Encoding.UTF8)) {
                    var clean = CleanListEntry(rel, out var entryReason);
                    if (entryReason != null) {
                        // Сюда доходить нечему: ValidateLists уже отвергла бы весь список.
                        log.Write($"copy rejected '{rel}': {entryReason}");
                        continue;
                    }
                    if (string.IsNullOrEmpty(clean)) {
                        continue;
                    }
                    if (ShouldPreserve(clean, "copy")) { continue; }
                    if (PreserveMatcher.IsUpdaterArtifact(clean)) { log.Write($"skip copy updater artifact {clean}"); continue; }
                    var srcRel = clean;
                    var dstRel = StripOf(clean, strip);
                    var s = ManifestPath.Combine(src, srcRel);
                    var d = ManifestPath.Combine(dst, dstRel);
                    if (!File.Exists(s)) {
                        // A1. Отсутствие файла в SRC — это не «нечего копировать», а сбой:
                        // лаунчер включил файл в план, значит на диске обязана оказаться
                        // новая версия. Раньше запись просто пропускалась, и прогон
                        // доходил до маркера версии с копиями старых сборок на диске.
                        // Дальше это уже не чинится: предохранитель `remote == local`
                        // в лаунчере выходит из проверки ДО сверки хешей, и установка
                        // считается исправной навсегда. Считаем ошибкой копирования —
                        // прогон откатывается, маркер не пишется, обновление предложится снова.
                        copyErrors++;
                        log.Write($"copy FAILED: файла нет в пакете обновления (diff src missing) {srcRel}");
                        continue;
                    }
                    copied.Add(srcRel);
                    await CopyFileAsync(s, d);
                }
            }

            // Residual mirror of all SRC files (ensures runtimes/, prereqs/ etc.).
            // Только для полного пакета (список файлов не передан): при диффе SRC содержит
            // ровно то, что надо скопировать, и оно уже скопировано выше.
            if (!haveFileList && Directory.Exists(src)) {
                foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)) {
                    var rel = Path.GetRelativePath(src, s).Replace('\\', '/');
                    if (matcher.ShouldPreserve(rel)) { continue; }
                    // Служебные файлы апдейтера в папку установки не переносим никогда (A6).
                    if (PreserveMatcher.IsUpdaterArtifact(rel)) { log.Write($"mirror skip updater artifact {rel}"); continue; }
                    var dstRel = StripOf(rel, strip);
                    var d = ManifestPath.Combine(dst, dstRel);
                    copied.Add(rel);
                    // Cheap skip: same size
                    try {
                        if (File.Exists(d)) {
                            var s1 = new FileInfo(s).Length; var s2 = new FileInfo(d).Length;
                            if (s1 == s2) {
                                continue;
                            }
                        }
                    }
                    catch { }
                    await CopyFileAsync(s, d);
                }
            }

            // Диагностика диффа: всё, что лежит в SRC, но не попало в список копирования.
            // В норме таких файлов нет; если появились — значит лаунчер и апдейтер разошлись.
            if (haveFileList && Directory.Exists(src)) {
                try {
                    foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)) {
                        var rel = Path.GetRelativePath(src, s).Replace('\\', '/');
                        if (copied.Contains(rel) || matcher.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel)) {
                            continue;
                        }
                        log.Write($"diff: SRC file not in FILES list, skipped: {rel}");
                    }
                }
                catch (Exception ex) { log.Write($"diff audit error: {ex.Message}"); }
            }

            // A1. Сверка хешей — это ПРОВЕРКА, а не заметка в журнале. Раньше её итог
            // (mismatch / dst_missing) не влиял ни на что: маркер писался, код возврата
            // оставался нулевым, и установка «скопировалось, но байт не тот» считалась
            // исправной навсегда. Теперь расхождение — такая же ошибка, как отказ копирования.
            integrityErrors = await VerifyAsync(src, dst, strip, copied, haveFileList, matcher, log, CopyFileAsync);
        }
        catch (UpdaterAbortException ex) {
            log.Write($"ABORT: {ex.Message}");
            tx.Rollback();
            ctx.Outcome = "access-denied";
            ctx.Message = ex.Message;
            return ExitFatal;
        }
        catch (ManifestPathException ex) {
            log.Write($"ABORT: небезопасный путь: {ex.Message}");
            tx.Rollback();
            ctx.Outcome = "fatal";
            ctx.Message = ex.Message;
            return ExitFatal;
        }

        // B3. Удаления — только после ПОЛНОСТЬЮ успешного копирования.
        // Раньше deletelist применялся до проверки счётчика ошибок: старые файлы
        // уже снесены, новые не легли, и на диске оставалась дыра, из которой
        // лаунчер не стартует. Порядок «сначала всё скопировать, потом удалять»
        // и есть то, что делает откат возможным.
        if (copyErrors > 0 || integrityErrors > 0) {
            log.Write($"COPY SUMMARY: FAILED. ok={copyOk} copy_errors={copyErrors} integrity_errors={integrityErrors}. " +
                      "Удаления НЕ выполнялись, маркер версии НЕ записан, изменения откатываются.");
            tx.Rollback();
            ctx.Outcome = "copy-errors";
            ctx.Message = $"Обновление до {(string.IsNullOrWhiteSpace(ctx.Version) ? "новой версии" : ctx.Version)} не применено: " +
                          $"{copyErrors} ошибок копирования, {integrityErrors} расхождений по хешу. Прежняя версия восстановлена.";
            return ExitCopyErrors;
        }

        // Deletions
        if (!string.IsNullOrWhiteSpace(del) && File.Exists(del)) {
            foreach (var rel in File.ReadAllLines(del, Encoding.UTF8)) {
                var clean = CleanListEntry(rel, out var entryReason);
                if (entryReason != null) {
                    log.Write($"delete rejected '{rel}': {entryReason}");
                    continue;
                }
                if (string.IsNullOrEmpty(clean)) {
                    continue;
                }
                if (ShouldPreserve(clean, "delete")) { continue; }
                string delPath;
                try { delPath = ManifestPath.Combine(dst, clean); }
                catch (ManifestPathException ex) { log.Write($"delete rejected {clean}: {ex.Reason}"); continue; }
                try { if (File.Exists(delPath)) { var fi = new FileInfo(delPath); fi.IsReadOnly = false; File.Delete(delPath); log.Write($"deleted {clean}"); } }
                catch (Exception ex) { log.Write($"delete failed {clean}: {ex.Message}"); }
            }
        }

        // Empty dirs
        if (!string.IsNullOrWhiteSpace(dirs) && File.Exists(dirs)) {
            foreach (var rel in File.ReadAllLines(dirs, Encoding.UTF8)) {
                var clean = CleanListEntry(rel, out var entryReason);
                if (entryReason != null) {
                    log.Write($"mkdir rejected '{rel}': {entryReason}");
                    continue;
                }
                if (string.IsNullOrEmpty(clean)) {
                    continue;
                }
                try { Directory.CreateDirectory(ManifestPath.Combine(dst, clean)); } catch (Exception ex) { log.Write($"mkdir failed {clean}: {ex.Message}"); }
            }
        }

        // Разовая очистка уже засорённых инсталляций: служебные файлы апдейтера,
        // которые прошлые версии копировали прямо в папку установки (A6).
        CleanupUpdaterArtifacts(dst, log.Write);

        // Write version marker (if provided) — ТОЛЬКО при полностью успешном копировании.
        // UTF-8 без BOM и без завершающего перевода строки — ровно как пишет installer.nsi.
        //
        // Почему условие обязательно: маркер — это утверждение «на диске лежит
        // версия N». Лаунчер верит ему безоговорочно: предохранитель
        // `remote == local` выходит из проверки ДО сверки хешей. Если записать
        // маркер после частичного копирования (пара файлов залочена антивирусом),
        // установка со смесью старых и новых сборок будет считаться исправной
        // навсегда, счётчик попыток обнулится, и расхождение уже никто не заметит.
        //
        // A7: пишем атомарно. File.WriteAllText — это truncate+write, и обрыв
        // между ними оставляет ПУСТОЙ маркер, после которого обновление
        // не предлагается уже никогда.
        if (!string.IsNullOrWhiteSpace(newVersion)) {
            try {
                var marker = Path.Combine(dst, "launcher.version");
                AtomicFile.WriteAllText(marker, newVersion.Trim(), Utf8NoBom);
                log.Write($"wrote version marker: {marker} = '{newVersion.Trim()}'");
            }
            catch (Exception ex) {
                // Маркер не записан — установка новая, а лаунчер считает её старой.
                // Это не порча данных, но обновление предложится снова: сообщаем честно.
                log.Write($"version marker write error: {ex.Message}");
                tx.Commit();
                ctx.Outcome = "marker-failed";
                ctx.Message = $"Файлы обновлены, но маркер версии не записан: {ex.Message}";
                return ExitCopyErrors;
            }
        }

        tx.Commit();
        log.Write($"COPY SUMMARY: OK. ok={copyOk} errors=0");
        ctx.Outcome = "ok";
        ctx.Message = string.IsNullOrWhiteSpace(ctx.Version)
            ? "Обновление применено."
            : $"Обновление до {ctx.Version} применено.";
        return ExitOk;
    }

    /// <summary>
    /// Сверяет то, что должно было скопироваться, с источником. Одно расхождение
    /// пробуем починить повторным копированием (частый случай — «пропустили по
    /// совпадению размера»), и только затем считаем ошибкой.
    /// </summary>
    /// <returns>Количество неустранённых расхождений.</returns>
    internal static async Task<int> VerifyAsync(
        string src,
        string dst,
        string strip,
        HashSet<string> copied,
        bool haveFileList,
        PreserveMatcher matcher,
        UpdateLog log,
        Func<string, string, Task<bool>> copy) {
        var errors = 0;
        try {
            var map = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var logFileName = Path.GetFileName(log.Path);
            bool IgnoreForHash(string rel) {
                var r = (rel ?? string.Empty).Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(r)) {
                    return true;
                }
                // ignore updater artifacts and logs/lists
                if (!string.IsNullOrEmpty(logFileName) && string.Equals(Path.GetFileName(r), logFileName, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
                if (PreserveMatcher.IsUpdaterArtifact(r)) {
                    return true;
                }
                // preserve-файлы намеренно расходятся — они не участвуют в сверке
                if (matcher.ShouldPreserve(r)) {
                    return true;
                }
                return false;
            }

            // Сверяем ровно то, что должны были положить на диск. Полный обход папки
            // установки бессмысленен: при диффе в SRC лежат только изменившиеся файлы,
            // а файлы, которых нет в SRC, к результату копирования отношения не имеют.
            foreach (var rel in copied) {
                if (!IgnoreForHash(rel)) {
                    map.Add(rel);
                }
            }

            int ok = 0, mm = 0, missS = 0, missD = 0, total = 0, repaired = 0, unreadable = 0;
            foreach (var key in map) {
                total++;
                var relSrc = key;
                var relDst = StripOf(key, strip);
                var sp = ManifestPath.Combine(src, relSrc);
                var dp = ManifestPath.Combine(dst, relDst);
                // Источник был на месте в момент копирования (иначе запись не попала бы
                // в `copied`), а теперь его нет — файл увели из-под ног прямо во время
                // прогона. Сверить содержимое не с чем, значит «в порядке» утверждать
                // нельзя: непроверенное — это ошибка, а не пустая строка в журнале.
                if (!File.Exists(sp)) {
                    missS++;
                    errors++;
                    log.Write($"hash ERROR {relSrc}: файл-источник исчез во время обновления — содержимое НЕ сравнивалось");
                    continue;
                }

                // Нечитаемый ИСТОЧНИК — это не расхождение. Раньше пустой хеш источника
                // молча проваливался в общую ветку и объявлял «содержимое не совпадает»,
                // хотя сравнивать было нечего: файл не прочитан (чаще всего его держит
                // антивирус). Провал безопасный, но настоящая причина в журнал не попадала.
                var h1 = Sha256Hex(sp, out var srcError);
                if (srcError != null) {
                    unreadable++;
                    errors++;
                    log.Write($"hash ERROR {relSrc}: не удалось прочитать файл-источник — {srcError}. " +
                              "Содержимое НЕ сравнивалось; типичная причина — файл держит антивирус.");
                    continue;
                }

                if (File.Exists(dp) && h1.Equals(Sha256Hex(dp), StringComparison.OrdinalIgnoreCase)) {
                    ok++;
                    continue;
                }

                // Одна попытка починки: файл мог быть пропущен по совпадению размера
                // либо перезаписан кем-то между копированием и сверкой.
                log.Write($"hash: {(File.Exists(dp) ? "MISMATCH" : "DST missing")} {relDst} — повторное копирование");
                await copy(sp, dp);

                if (!File.Exists(dp)) {
                    missD++;
                    errors++;
                    log.Write($"hash ERROR {relDst}: файла нет в папке установки после повторного копирования");
                    continue;
                }

                var h2 = Sha256Hex(dp, out var dstError);
                if (dstError != null) {
                    unreadable++;
                    errors++;
                    log.Write($"hash ERROR {relDst}: не удалось прочитать записанный файл — {dstError}. " +
                              "Содержимое НЕ сравнивалось; типичная причина — файл держит антивирус.");
                    continue;
                }

                if (h1.Equals(h2, StringComparison.OrdinalIgnoreCase)) {
                    repaired++;
                    continue;
                }

                mm++;
                errors++;
                log.Write($"hash ERROR {relDst}: содержимое не совпадает с источником после повторного копирования");
            }

            log.Write($"hash summary: total={total} ok={ok} repaired={repaired} mismatch={mm} src_missing={missS} dst_missing={missD} unreadable={unreadable}");
        }
        catch (UpdaterAbortException) {
            throw;
        }
        catch (Exception ex) {
            // Не смогли проверить — считаем это ошибкой: «не проверено» не равно «в порядке».
            log.Write($"hash compare error: {ex.Message}");
            errors++;
        }

        return errors;
    }

    /// <summary>
    /// A13. Разбирает идентификатор родительского процесса (--parent).
    /// <para>
    /// Значение обязано быть передано и обязано быть числом. Раньше отсутствующее
    /// значение добиралось умолчанием "0", а parent=0 означает «ждать некого»:
    /// апдейтер начинал копировать поверх ЖИВОГО лаунчера, половина файлов была
    /// залочена, обновление разваливалось. Причём отсутствие значения — не
    /// экзотика: "--parent --dst C:\app" (ключ, у которого значение потерялось при
    /// сборке команды) разбирается в null и внешне ничем не отличается от нормы.
    /// Поэтому любой непонятый --parent — фатальная ошибка, а не тихий ноль.
    /// </para>
    /// </summary>
    /// <param name="raw">Значение из командной строки (null — ключа или значения не было).</param>
    /// <param name="pid">Разобранный идентификатор процесса.</param>
    /// <param name="problem">Описание проблемы для журнала и статуса; пустое при успехе.</param>
    /// <returns>true, если значение разобрано.</returns>
    internal static bool TryParseParentPid(string? raw, out int pid, out string problem) {
        pid = 0;

        if (raw == null) {
            problem = "значение не передано";
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw)) {
            problem = $"значение '{raw}' пустое";
            return false;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) {
            problem = $"значение '{raw}' не число";
            return false;
        }

        if (parsed < 0) {
            problem = $"значение '{raw}' отрицательное — такого процесса не бывает";
            return false;
        }

        pid = parsed;
        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Приводит строку списка (filelist/emptydirs/deletelist) к относительному пути.
    /// <para>
    /// Единая точка для проверки и для циклов копирования/удаления: разойдись они,
    /// проверка смотрела бы на одну строку, а на диск уходила другая.
    /// </para>
    /// <para>
    /// Ведущий слеш и UNC отвергаются ЯВНО. Раньше "/Windows/System32/x.dll"
    /// просто обрезался до "Windows/System32/x.dll" и принимался как обычная
    /// запись внутри папки установки: за корень она не выходит, но это заведомо
    /// не тот файл, который имел в виду автор списка. Тихо «починить» такую
    /// строку — значит применить обновление не туда, вместо отказа.
    /// </para>
    /// </summary>
    /// <param name="line">Строка списка как она записана в файле.</param>
    /// <param name="reason">Причина отказа либо null.</param>
    /// <returns>Относительный путь; пустая строка означает «строку пропустить» (пустая либо отвергнутая).</returns>
    internal static string CleanListEntry(string? line, out string? reason) {
        reason = null;
        var raw = (line ?? string.Empty).Replace('\\', '/');
        var clean = raw.Trim('/');

        // Пустые строки (включая "///" и хвостовой перевод строки) — не пути.
        if (string.IsNullOrWhiteSpace(clean)) {
            return string.Empty;
        }

        if (raw.StartsWith("//", StringComparison.Ordinal)) {
            reason = "UNC-путь (\\\\сервер\\ресурс) — файл ушёл бы мимо папки установки";
            return string.Empty;
        }

        if (raw[0] == '/') {
            reason = "ведущий слеш — путь от корня диска, а не от папки установки";
            return string.Empty;
        }

        return clean;
    }

    /// <summary>Убирает strip-prefix из относительного пути.</summary>
    internal static string StripOf(string rel, string strip)
        => string.IsNullOrWhiteSpace(strip)
            ? rel
            : rel.StartsWith(strip + "/", StringComparison.OrdinalIgnoreCase) ? rel.Substring(strip.Length + 1) : rel;

    internal static Dictionary<string, string?> ParseArgs(string[] a) {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++) {
            var tok = a[i];
            if (!tok.StartsWith("--", StringComparison.Ordinal)) {
                continue;
            }
            var key = tok;
            string? val = null;
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal)) { val = a[++i]; }
            dict[key] = val;
        }
        return dict;
    }

    /// <summary>
    /// A2. Проверка прав на запись в папку установки ДО первой операции.
    /// Возвращает описание проблемы либо null.
    /// </summary>
    internal static string? DescribeWriteAccess(string dir) {
        try {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".chillhub-write-probe-{Environment.ProcessId}{AtomicFile.TempSuffix}");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) {
                fs.WriteByte(0);
            }

            return null;
        }
        catch (Exception ex) {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Определяет общий префикс архива (обёртку вида «ChillHub-1.2.3/»).
    /// <para>
    /// Сбой ввода-вывода здесь обязан быть виден в журнале: молчаливый null
    /// неотличим от честного «обёртки нет», а последствия разные. Если список
    /// файлов не прочитался (занят, нет прав), префикс не находится, вся новая
    /// сборка ложится в ПОДПАПКУ установки, запускаемый лаунчер остаётся старым —
    /// и обновление предлагается при каждом старте, навсегда.
    /// </para>
    /// </summary>
    /// <param name="src">Каталог с распакованным обновлением.</param>
    /// <param name="files">Путь к списку файлов (может отсутствовать).</param>
    /// <param name="log">Куда писать причину сбоя (необязательно).</param>
    /// <returns>Префикс либо null.</returns>
    internal static string? DetectStripPrefix(string src, string files, Action<string>? log = null) {
        try {
            // Prefer detection from FILES list if present: require a single shared top-level segment
            if (!string.IsNullOrWhiteSpace(files) && File.Exists(files)) {
                var lines = File.ReadAllLines(files, Encoding.UTF8)
                    .Select(l => (l ?? string.Empty).Replace('\\', '/').Trim('/'))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToArray();
                var firstSegs = lines
                    .Select(l => l.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (firstSegs.Length == 1) {
                    var candidate = firstSegs[0];
                    var allHave = lines.All(l => l.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase));
                    if (allHave && Directory.Exists(Path.Combine(src, candidate))) {
                        return candidate;
                    }
                }
            }

            // Fallback: top-level of SRC has exactly one directory and no files
            if (Directory.Exists(src)) {
                var topFiles = Directory.EnumerateFiles(src, "*", SearchOption.TopDirectoryOnly).Any();
                var topDirs = Directory.EnumerateDirectories(src, "*", SearchOption.TopDirectoryOnly).ToArray();
                if (!topFiles && topDirs.Length == 1) {
                    return Path.GetFileName(topDirs[0]);
                }
            }
        }
        catch (Exception ex) {
            log?.Invoke($"strip-prefix detect error: {ex.GetType().Name}: {ex.Message}. " +
                        "Обёртка архива не определена — если она есть, обновление ляжет в подпапку установки.");
        }

        return null;
    }

    /// <summary>
    /// Выкладывает содержимое списков в журнал.
    /// <para>
    /// Это единственная запись о том, что именно апдейтер собирался сделать:
    /// временные списки после прогона удаляются, и разбирать чужой сбой больше не по чему.
    /// Отсутствующий или нечитаемый список обязан быть строкой в журнале, а не отказом:
    /// диагностика не имеет права мешать обновлению.
    /// </para>
    /// </summary>
    /// <param name="files">Путь к filelist.</param>
    /// <param name="dirs">Путь к emptydirs.</param>
    /// <param name="del">Путь к deletelist.</param>
    /// <param name="log">Журнал.</param>
    internal static void LogLists(string files, string dirs, string del, UpdateLog log) {
        try {
            foreach (var (name, path) in new[] { ("FILES", files), ("DIRS", dirs), ("DEL", del) }) {
                if (string.IsNullOrWhiteSpace(path)) {
                    continue;
                }

                if (!File.Exists(path)) {
                    log.Write($"{name} list missing: '{path}'");
                    continue;
                }

                var lines = File.ReadAllLines(path, Encoding.UTF8);
                log.Write($"{name} list: path='{path}', count={lines.Length}");
                foreach (var l in lines) {
                    log.Write($"  {name}: {l}");
                }
            }
        }
        catch (Exception ex) { log.Write($"lists log error: {ex.Message}"); }
    }

    /// <summary>
    /// A12. Кладёт исход рядом с маркером версии, чтобы лаунчер при следующем
    /// запуске мог объяснить, почему обновление не применилось.
    /// </summary>
    /// <param name="ctx">Состояние прогона.</param>
    /// <param name="exit">Код возврата.</param>
    /// <param name="log">Журнал.</param>
    internal static void WriteStatus(RunContext ctx, int exit, UpdateLog log) {
        if (string.IsNullOrWhiteSpace(ctx.Dst)) {
            log.Write("update status not written: каталог установки неизвестен");
            return;
        }

        UpdateStatus.Write(
            ctx.Dst,
            new UpdateStatus {
                Outcome = string.IsNullOrWhiteSpace(ctx.Outcome) ? "fatal" : ctx.Outcome,
                ExitCode = exit,
                Version = ctx.Version,
                Message = ctx.Message,
                LogPath = log.Path,
            },
            log.Write);
    }

    /// <summary>
    /// A9. Перезапуск лаунчера — в finally и с повторами.
    /// <para>
    /// Лаунчер завершил себя сам, чтобы освободить файлы. Если апдейтер после
    /// этого просто умрёт (исключение, недоступный лог, отказ в правах), у
    /// пользователя не останется вообще ничего: окно закрылось, новое не
    /// открылось, а причина видна только в логе, который он не найдёт.
    /// Поэтому лаунчер поднимается при ЛЮБОМ исходе, включая фатальный.
    /// </para>
    /// </summary>
    /// <param name="ctx">Состояние прогона.</param>
    /// <param name="log">Журнал.</param>
    /// <param name="host">Шов к процессам операционной системы.</param>
    internal static void Restart(RunContext ctx, UpdateLog log, UpdaterHost host) {
        if (!ctx.Restart) {
            log.Write("restart skipped by design");
            return;
        }

        var candidates = RestartCandidates(ctx);
        var exeArgs = ReadExeArgs(ctx.ExeArgsFile, log);
        for (var attempt = 1; attempt <= 3; attempt++) {
            foreach (var exe in candidates) {
                try {
                    if (!File.Exists(exe)) {
                        continue;
                    }

                    var psi = new ProcessStartInfo {
                        FileName = exe,
                        WorkingDirectory = Directory.Exists(ctx.Dst) ? ctx.Dst : (Path.GetDirectoryName(exe) ?? string.Empty),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    // Исходные аргументы лаунчера восстанавливаем через ArgumentList:
                    // так пути с пробелами и кавычками доезжают дословно.
                    foreach (var a in exeArgs) {
                        psi.ArgumentList.Add(a);
                    }

                    var pid = host.StartProcess(psi);
                    if (pid != null) {
                        log.Write($"launcher restarted: '{exe}' pid={pid.Value} args={exeArgs.Count}");
                        return;
                    }

                    log.Write($"restart: Process.Start('{exe}') вернул null");
                }
                catch (Exception ex) {
                    log.Write($"restart attempt {attempt} for '{exe}' failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            host.Sleep(500);
        }

        log.Write("CRITICAL: перезапустить лаунчер не удалось — пользователь остался без запущенного приложения. " +
                  "Кандидаты: " + string.Join(", ", candidates));
    }

    /// <summary>
    /// Кандидаты на перезапуск, в порядке предпочтения.
    /// <para>
    /// Один путь здесь недостаточен: exe лаунчера мог переехать (обновление сменило
    /// имя файла) или прийти из временной копии, из которой лаунчер запускал апдейтер.
    /// Промах по всем кандидатам оставляет пользователя без запущенного приложения —
    /// окно он закрыл сам, чтобы освободить файлы.
    /// </para>
    /// </summary>
    /// <param name="ctx">Состояние прогона (exe и каталог установки).</param>
    /// <returns>Пути к exe в порядке проверки.</returns>
    internal static List<string> RestartCandidates(RunContext ctx) {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.Exe)) {
            candidates.Add(ctx.Exe);
            if (!string.IsNullOrWhiteSpace(ctx.Dst)) {
                var sameName = Path.Combine(ctx.Dst, Path.GetFileName(ctx.Exe));
                if (!string.Equals(sameName, ctx.Exe, StringComparison.OrdinalIgnoreCase)) {
                    candidates.Add(sameName);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(ctx.Dst)) {
            candidates.Add(Path.Combine(ctx.Dst, "ChillHub.exe"));
        }

        return candidates;
    }

    /// <summary>
    /// Читает исходные аргументы командной строки лаунчера (по одному на строку).
    /// Файл, а не строка в командной строке: так не нужно ничего экранировать и
    /// нечему потеряться при повторном разборе.
    /// </summary>
    internal static List<string> ReadExeArgs(string? path, UpdateLog log) {
        var result = new List<string>();
        try {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
                return result;
            }

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8)) {
                if (!string.IsNullOrEmpty(line)) {
                    result.Add(line);
                }
            }
        }
        catch (Exception ex) {
            log.Write($"exe args read error: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Проверяет, что все пути в переданных списках безопасны.
    /// <para>
    /// Проверяется ровно та форма пути, которая потом уходит в Path.Combine
    /// (после замены слешей и обрезки краевых) — иначе проверка и использование
    /// смотрели бы на разные строки.
    /// </para>
    /// </summary>
    /// <param name="listPaths">Пути к файлам списков (filelist/emptydirs/deletelist).</param>
    /// <param name="strip">Префикс корневой папки архива — он тоже подставляется в пути.</param>
    /// <param name="log">Логгер.</param>
    /// <returns>true, если всё безопасно.</returns>
    internal static bool ValidateLists(IEnumerable<string?> listPaths, string strip, Action<string> log) {
        var ok = true;

        if (!string.IsNullOrWhiteSpace(strip)) {
            var reason = ManifestPath.Describe(strip.Replace('\\', '/').Trim('/'));
            if (reason != null) {
                log($"REJECT strip-prefix '{strip}': {reason}");
                ok = false;
            }
        }

        foreach (var listPath in listPaths) {
            if (string.IsNullOrWhiteSpace(listPath) || !File.Exists(listPath)) {
                continue;
            }

            string[] lines;
            try {
                lines = File.ReadAllLines(listPath, Encoding.UTF8);
            }
            catch (Exception ex) {
                log($"REJECT list '{listPath}': не читается ({ex.Message})");
                return false;
            }

            for (var i = 0; i < lines.Length; i++) {
                var clean = CleanListEntry(lines[i], out var entryReason);
                if (entryReason != null) {
                    log($"REJECT '{listPath}' строка {i + 1}: '{lines[i]}' — {entryReason}");
                    ok = false;
                    continue;
                }

                if (string.IsNullOrEmpty(clean)) {
                    continue;
                }

                var reason = ManifestPath.Describe(clean);
                if (reason != null) {
                    log($"REJECT '{listPath}' строка {i + 1}: '{clean}' — {reason}");
                    ok = false;
                }
            }
        }

        return ok;
    }

    /// <summary>
    /// Удаляет из папки установки служебные файлы апдейтера, оставшиеся от прошлых версий
    /// (filelist.txt / deletelist.txt / emptydirs.txt / apply-update.log / apply-update.cmd и подпапку updater\).
    /// </summary>
    internal static void CleanupUpdaterArtifacts(string dst, Action<string> log) {
        try {
            foreach (var name in PreserveMatcher.UpdaterArtifactFiles) {
                var p = Path.Combine(dst, name);
                try {
                    if (File.Exists(p)) {
                        // Атрибут «только чтение» снимаем перед удалением: иначе File.Delete
                        // откажет и артефакт останется в папке установки навсегда.
                        new FileInfo(p).IsReadOnly = false;
                        File.Delete(p);
                        log($"cleanup: removed stale updater artifact {name}");
                    }
                }
                catch (Exception ex) { log($"cleanup failed {name}: {ex.Message}"); }
            }

            var dir = Path.Combine(dst, PreserveMatcher.UpdaterArtifactDir);
            try {
                if (Directory.Exists(dir)) {
                    // Directory.Delete(recursive) спотыкается о файлы «только для чтения»:
                    // достаточно одного такого внутри updater\, и вся папка остаётся
                    // в установке, попадая в сверку с сервером как лишние файлы.
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) {
                        try { new FileInfo(f).IsReadOnly = false; }
                        catch (Exception ex) { log($"cleanup: не снят атрибут «только чтение» с '{f}': {ex.Message}"); }
                    }

                    Directory.Delete(dir, true);
                    log($"cleanup: removed stale updater directory '{PreserveMatcher.UpdaterArtifactDir}'");
                }
            }
            catch (Exception ex) { log($"cleanup failed dir '{PreserveMatcher.UpdaterArtifactDir}': {ex.Message}"); }
        }
        catch (Exception ex) { log($"cleanup error: {ex.Message}"); }
    }

    /// <summary>
    /// SHA-256 файла в hex (нижний регистр). Пустая строка — файл прочитать не удалось.
    /// </summary>
    /// <param name="path">Путь к файлу.</param>
    /// <returns>Хеш либо пустая строка.</returns>
    internal static string Sha256Hex(string path) => Sha256Hex(path, out _);

    /// <summary>
    /// То же, но с причиной неудачи.
    /// <para>
    /// Причина обязана выходить наружу: типичный отказ здесь — антивирус, который
    /// держит только что распакованный файл. Без неё в журнале оставалось
    /// «содержимое не совпадает с источником», и разбор уходил в поиск
    /// несуществующей порчи файлов вместо настоящей причины — файл НЕ ПРОЧИТАН.
    /// </para>
    /// </summary>
    /// <param name="path">Путь к файлу.</param>
    /// <param name="error">Описание ошибки чтения либо null.</param>
    /// <returns>Хеш либо пустая строка.</returns>
    internal static string Sha256Hex(string path, out string? error) {
        error = null;
        try {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var buf = new byte[262144];
            int r;
            while ((r = fs.Read(buf, 0, buf.Length)) > 0) {
                sha.TransformBlock(buf, 0, r, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (Exception ex) {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return string.Empty;
        }
    }

    /// <summary>Что и куда применять — исходные данные файловой части обновления.</summary>
    internal sealed class ApplyRequest {
        /// <summary>Каталог с распакованным обновлением.</summary>
        public string Src = string.Empty;

        /// <summary>Каталог установки — сюда пишем.</summary>
        public string Dst = string.Empty;

        /// <summary>Путь к filelist (пусто — полный пакет без диффа).</summary>
        public string Files = string.Empty;

        /// <summary>Путь к emptydirs.</summary>
        public string Dirs = string.Empty;

        /// <summary>Путь к deletelist.</summary>
        public string Del = string.Empty;

        /// <summary>Общий префикс архива, который нужно снять с путей.</summary>
        public string Strip = string.Empty;

        /// <summary>Разрешено ли определять префикс самостоятельно.</summary>
        public bool AutoStrip = true;

        /// <summary>Правила preserve через запятую.</summary>
        public string Preserve = PreserveMatcher.DefaultRulesArg;

        /// <summary>Версия для маркера (пусто — маркер не писать).</summary>
        public string Version = string.Empty;
    }

    /// <summary>
    /// Шов к операционной системе: ожидание родителя, запуск процессов, сон.
    /// <para>
    /// Всё, что здесь собрано, нельзя выполнить в тесте: настоящий лаунчер
    /// запускать нельзя, а ждать чужой процесс — значит ждать по-настоящему.
    /// Поведение по умолчанию — боевое, подмена нужна только проверкам.
    /// </para>
    /// </summary>
    internal sealed class UpdaterHost {
        /// <summary>Ждёт выхода родительского процесса (pid 0 — ждать некого).</summary>
        public Action<int, UpdateLog> WaitForParent = DefaultWaitForParent;

        /// <summary>Запускает процесс; возвращает pid либо null, если запуск не дал процесса.</summary>
        public Func<ProcessStartInfo, int?> StartProcess = psi => Process.Start(psi)?.Id;

        /// <summary>Пауза между попытками перезапуска.</summary>
        public Action<int> Sleep = Thread.Sleep;

        /// <summary>
        /// Ждать нужно обязательно: пока лаунчер жив, его exe и dll заблокированы,
        /// и копирование поверх них провалится. Но ждать БЕЗ ограничения нельзя —
        /// подвисший лаунчер оставлял апдейтер висеть вечно, без окна и без
        /// единой строки в логе, а пользователь видел просто «обновление не
        /// заканчивается».
        /// <para>
        /// По таймауту всё равно идём дальше: копирование само упадёт на
        /// заблокированных файлах, а маркер версии при ошибках копирования
        /// не пишется — значит следующий запуск честно повторит обновление.
        /// </para>
        /// </summary>
        /// <param name="parent">Идентификатор родительского процесса.</param>
        /// <param name="log">Журнал.</param>
        private static void DefaultWaitForParent(int parent, UpdateLog log) {
            if (parent <= 0) {
                return;
            }

            try {
                var proc = Process.GetProcessById(parent);
                if (proc.WaitForExit(ParentWaitMs)) {
                    log.Write($"Parent {parent} exited");
                }
                else {
                    log.Write($"WARNING: parent {parent} is still running after {ParentWaitMs / 1000}s; " +
                        "proceeding anyway — locked files will fail to copy and the update will be retried on next launch");
                }
            }
            catch (ArgumentException) {
                // Процесса с таким id уже нет — ровно то, чего мы и ждали.
                log.Write($"Parent {parent} already gone");
            }
            catch (Exception ex) {
                log.Write($"WARNING: cannot wait for parent {parent}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Состояние прогона, нужное блоку finally (лог, статус, перезапуск).</summary>
    internal sealed class RunContext {
        public string Exe = string.Empty;
        public string Dst = string.Empty;
        public string Version = string.Empty;
        public string? ExeArgsFile;
        public string Outcome = "fatal";
        public string Message = string.Empty;
        public bool Restart = true;
        public Mutex? Lock;
    }

    /// <summary>Прерывание всего прохода: повторять бессмысленно (например, отказ в доступе).</summary>
    private sealed class UpdaterAbortException : Exception {
        public UpdaterAbortException(string message, Exception? inner = null)
            : base(message, inner) {
        }
    }
}
