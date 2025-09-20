using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.IO;
using ChillHub.Core;
using ChillHub.Core.Sync;
using ChillHub.Core.Net;

namespace ChillHub
{
    public partial class UpdateWindow : Window
    {
        private string BaseApi => ConfigService.Current.ApiBaseUrl;
        private readonly HttpClient _http = HttpClientProvider.Shared;
        private bool _updateRequired = false; // есть ли новая версия
        private bool _downloaded = false;     // скачан ли пакет
        private string? _remoteVersion;
        private readonly ISyncService _sync = new SimpleSyncService();
        public bool Proceed { get; private set; } = false;

        private sealed class LatestMeta { public string version { get; set; } = string.Empty; }

        public UpdateWindow()
        {
            InitializeComponent();
        }

        private void SetUpdateAvailableStatus(string local, string remote)
        {
            // Resolve theme brushes
            var danger = (Brush)(TryFindResource("Brush.Danger") ?? new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));
            var success = (Brush)(TryFindResource("Brush.Success") ?? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)));
            var normal = (Brush)(TryFindResource("Brush.Text") ?? SystemColors.ControlTextBrush);

            StatusText.Inlines.Clear();
            StatusText.Inlines.Add(new Run("Доступно обновление лаунчера: ") { Foreground = normal });
            StatusText.Inlines.Add(new Run(local) { Foreground = danger, FontWeight = FontWeights.SemiBold });
            StatusText.Inlines.Add(new Run(" → ") { Foreground = normal });
            var boldNew = new Bold(new Run(remote) { Foreground = success });
            StatusText.Inlines.Add(boldNew);
            StatusText.Inlines.Add(new Run(".") { Foreground = normal });
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // DEV-флаг окружения: позволяет пропустить проверку
            var dev = Environment.GetEnvironmentVariable("YL_DEV_SKIP_SELF_UPDATE");
            if (string.Equals(dev, "1", StringComparison.Ordinal))
            {
                StatusText.Text = "DEV: проверка пропущена";
                Progress.Value = 100;
                PrimaryBtn.Content = "Продолжить";
                _updateRequired = false;
                return; // ждём нажатия
            }

            try
            {
                StatusText.Text = "Проверка обновлений лаунчера...";
                Progress.IsIndeterminate = true;
                var latest = await _http.GetFromJsonAsync<LatestMeta>($"{BaseApi}/manifests/launcher/latest.json");
                var remote = latest?.version?.Trim();
                var asm = Assembly.GetExecutingAssembly();
                var v = asm?.GetName()?.Version;
                var local = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;

                if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(local))
                {
                    // Ничего не знаем — даём пользователю решить
                    StatusText.Text = "Информация о версии отсутствует.";
                    Progress.IsIndeterminate = false;
                    Progress.Value = 0;
                    PrimaryBtn.Content = "Продолжить";
                    _updateRequired = false;
                    return;
                }

                // Даже если версии совпадают/различаются, решение принимаем по хешам
                // 1) Получаем манифест последней версии
                Manifest? mf = null;
                try
                {
                    var manifestUrl = $"{BaseApi}/manifests/launcher/{remote}.json";
                    mf = await _sync.GetManifestAsync(manifestUrl, System.Threading.CancellationToken.None);
                }
                catch
                {
                    // Фоллбэк: если манифест не доступен — используем сравнение по версии, как раньше
                    if (!string.Equals(remote, local, StringComparison.OrdinalIgnoreCase))
                    {
                        _updateRequired = true;
                        _remoteVersion = remote;
                        Progress.IsIndeterminate = false;
                        Progress.Value = 0;
                        SetUpdateAvailableStatus(local, remote);
                        PrimaryBtn.Content = "Обновить и перезапустить";
                        DevSkipCheck.Checked += (s, _) => { PrimaryBtn.Content = "Продолжить без обновления (DEV)"; };
                        DevSkipCheck.Unchecked += (s, _) => { PrimaryBtn.Content = "Обновить и перезапустить"; };
                    }
                    else
                    {
                        StatusText.Text = "Установлена актуальная версия лаунчера.";
                        Progress.IsIndeterminate = false;
                        Progress.Value = 100;
                        PrimaryBtn.Content = "Продолжить";
                        _updateRequired = false;
                    }
                    return;
                }

                // 2) Сравниваем локальные файлы с хешами из манифеста
                bool allMatch = true;
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    foreach (var f in mf.Files)
                    {
                        var rel = f.Path.Replace('\\', '/');
                        var localPath = System.IO.Path.Combine(baseDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        if (!System.IO.File.Exists(localPath)) { allMatch = false; break; }
                        // Если в манифесте есть sha256/blake3 — считаем оба, иначе считаем совпадением по размеру
                        if (!string.IsNullOrWhiteSpace(f.Sha256) || !string.IsNullOrWhiteSpace(f.Blake3))
                        {
                            using var fs = new System.IO.FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: false);
                            using var sha = System.Security.Cryptography.SHA256.Create();
                            var b3 = Blake3.Hasher.New();
                            var buf = new byte[256 * 1024];
                            int r;
                            while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                            {
                                sha.TransformBlock(buf, 0, r, null, 0);
                                b3.Update(new ReadOnlySpan<byte>(buf, 0, r));
                            }
                            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            var shaHex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                            var b3out = new byte[32];
                            b3.Finalize(b3out);
                            var b3Hex = Convert.ToHexString(b3out).ToLowerInvariant();
                            var okSha = string.IsNullOrWhiteSpace(f.Sha256) || string.Equals(shaHex, f.Sha256, StringComparison.OrdinalIgnoreCase);
                            var okB3 = string.IsNullOrWhiteSpace(f.Blake3) || string.Equals(b3Hex, f.Blake3, StringComparison.OrdinalIgnoreCase);
                            if (!(okSha && okB3)) { allMatch = false; break; }
                        }
                        else
                        {
                            var info = new System.IO.FileInfo(localPath);
                            if (info.Length != f.Size) { allMatch = false; break; }
                        }
                    }
                }
                catch { allMatch = false; }

                if (!allMatch)
                {
                    _updateRequired = true;
                    _remoteVersion = remote;
                    Progress.IsIndeterminate = false;
                    Progress.Value = 0;
                    SetUpdateAvailableStatus(local, remote);
                    PrimaryBtn.Content = "Обновить и перезапустить";
                    DevSkipCheck.Checked += (s, _) => { PrimaryBtn.Content = "Продолжить без обновления (DEV)"; };
                    DevSkipCheck.Unchecked += (s, _) => { PrimaryBtn.Content = "Обновить и перезапустить"; };
                }
                else
                {
                    StatusText.Text = "Установлена актуальная версия лаунчера.";
                    Progress.IsIndeterminate = false;
                    Progress.Value = 100;
                    PrimaryBtn.Content = "Продолжить";
                    _updateRequired = false;
                }
            }
            catch (Exception ex)
            {
                // Нет сети/latest — даём пользователю решить
                StatusText.Text = $"Не удалось проверить обновление (GET {BaseApi}/manifests/launcher/latest.json): {ex.Message}";
                Progress.IsIndeterminate = false;
                Progress.Value = 0;
                PrimaryBtn.Content = "Продолжить";
                _updateRequired = false;
                try { Core.Logging.Logger.Error(ex, "UpdateWindow.Window_Loaded"); } catch { }
            }
        }

        private void ExitBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private string? _pendingTempRoot;
        
        private async void PrimaryBtn_Click(object sender, RoutedEventArgs e)
        {
            // DEV-скип: просто закрываем окно и продолжаем запуск
            if (!_updateRequired || DevSkipCheck.IsChecked == true)
            {
                Proceed = true;
                try { DialogResult = true; }
                catch { this.Close(); }
                return;
            }

            // Если пакет не скачан — качаем
            if (!_downloaded)
            {
                if (string.IsNullOrWhiteSpace(_remoteVersion)) return;
                string manifestUrl = string.Empty;
                string contentBase = string.Empty;
                try
                {
                    PrimaryBtn.IsEnabled = false;
                    StatusText.Text = "Запрос манифеста лаунчера...";
                    Progress.IsIndeterminate = true;

                    manifestUrl = $"{BaseApi}/manifests/launcher/{_remoteVersion}.json";
                    contentBase = $"{BaseApi}/content/launcher/{_remoteVersion}/files";
                    StatusText.Text = $"Манифест: {manifestUrl}";
                    var manifest = await _sync.GetManifestAsync(manifestUrl, System.Threading.CancellationToken.None);

                    StatusText.Text = "Подготовка каталога загрузки...";
                    var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", _remoteVersion);
                    // Очистим tempRoot, чтобы не было нулевых файлов от прошлых попыток
                    try { if (System.IO.Directory.Exists(tempRoot)) System.IO.Directory.Delete(tempRoot, true); } catch { }
                    System.IO.Directory.CreateDirectory(tempRoot);

                    // Подготовим вспомогательные списки для точечного копирования (без зеркалирования всей папки)
                    var selfUpdateDirDl = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", _remoteVersion);
                    System.IO.Directory.CreateDirectory(selfUpdateDirDl);
                    var filesListPath = System.IO.Path.Combine(selfUpdateDirDl, "filelist.txt");
                    var emptyDirsPath = System.IO.Path.Combine(selfUpdateDirDl, "emptydirs.txt");
                    var deleteListPath = System.IO.Path.Combine(selfUpdateDirDl, "deletelist.txt");
                    try
                    {
                        var fileLines = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(manifest.Files, f => f.Path.Replace('\\','/')));
                        System.IO.File.WriteAllLines(filesListPath, fileLines, System.Text.Encoding.UTF8);
                        var dirLines = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(manifest.EmptyDirs, d => d.Replace('\\','/').Trim('/')));
                        System.IO.File.WriteAllLines(emptyDirsPath, dirLines, System.Text.Encoding.UTF8);
                        // Build deletion list: any existing file in target launcher directory that is NOT present in the manifest
                        try
                        {
                            var targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                            var manifestSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                            foreach (var p in fileLines) manifestSet.Add(p.Replace('\\','/').TrimStart('/'));
                            var toDelete = new System.Collections.Generic.List<string>();
                            foreach (var diskFile in System.IO.Directory.EnumerateFiles(targetDir, "*", System.IO.SearchOption.AllDirectories))
                            {
                                // compute relative unix-style path
                                var rel = diskFile.Substring(targetDir.Length).TrimStart(System.IO.Path.DirectorySeparatorChar);
                                rel = rel.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                                // skip our own updater artifacts if accidentally placed in target (they are in %TEMP%, so just in case)
                                if (string.Equals(rel, "apply-update.cmd", System.StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rel, "apply-update.log", System.StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                if (!manifestSet.Contains(rel))
                                {
                                    toDelete.Add(rel);
                                }
                            }
                            System.IO.File.WriteAllLines(deleteListPath, toDelete, System.Text.Encoding.UTF8);
                        }
                        catch { }
                    }
                    catch { }

                    var plan = await _sync.PlanAsync(manifest, tempRoot, contentBase, System.Threading.CancellationToken.None);
                    StatusText.Text = $"Скачивание из: {contentBase}\nВременная папка: {tempRoot}";

                    StatusText.Text = "Скачивание файлов обновления...";
                    var prog = new Progress<SyncProgress>(p =>
                    {
                        Progress.IsIndeterminate = false;
                        if (p.TotalBytes > 0)
                        {
                            Progress.Value = Math.Min(100, Math.Max(0, (p.BytesDownloaded * 100.0) / p.TotalBytes));
                        }
                    });

                    await _sync.ExecuteAsync(plan, prog, System.Threading.CancellationToken.None);

                    _pendingTempRoot = tempRoot;
                    _downloaded = true;
                    StatusText.Text = "Обновление загружено. Применяем и перезапускаем...";
                }
                catch (InvalidDataException ex)
                {
                    // Обычно это несоответствие хэшей (sha256/blake3)
                    StatusText.Text = $"Проверка целостности не пройдена: {ex.Message}. Попробуйте ещё раз. Если проблема повторяется — обратитесь в поддержку.";
                    PrimaryBtn.IsEnabled = true;
                    _downloaded = false;
                    try { Core.Logging.Logger.Error(ex, "UpdateWindow.DownloadIntegrity"); } catch { }
                    return;
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Ошибка загрузки/проверки обновления (manifest: {manifestUrl}, content: {contentBase}): {ex.Message}";
                    PrimaryBtn.IsEnabled = true;
                    _downloaded = false;
                    try { Core.Logging.Logger.Error(ex, "UpdateWindow.DownloadUpdate"); } catch { }
                    return;
                }
                finally
                {
                    // fallthrough к применению
                }
            }

            // Применение (создание скрипта, копирование и перезапуск)
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingTempRoot) || !System.IO.Directory.Exists(_pendingTempRoot))
                {
                    StatusText.Text = "Не найден пакет обновления.";
                    PrimaryBtn.IsEnabled = true;
                    return;
                }

                var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetExecutingAssembly().Location;
                // Надёжнее берем корень через AppDomain (папка запуска)
                var targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                var selfUpdateDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", _remoteVersion ?? "pending");
                var ps1Path = System.IO.Path.Combine(selfUpdateDir, "apply-update.ps1");
                var logPath = System.IO.Path.Combine(selfUpdateDir, "apply-update.log");
                System.IO.Directory.CreateDirectory(selfUpdateDir);

                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var ps = """
param(
    [string]$SRC,
    [string]$DST,
    [string]$EXE,
    [int]$PARENT_PID,
    [string]$LOG,
    [string]$FILES,
    [string]$DIRS,
    [string]$DEL
)

function Log($msg) {
    try { Add-Content -Path $LOG -Value ("[" + (Get-Date).ToString('o') + "] " + $msg) } catch {}
}

function Get-FileHashSha256Hex([string]$path) {
    try {
        if (-not (Test-Path -LiteralPath $path)) { return $null }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        try {
            $buf = New-Object byte[] (262144)
            while (($r = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $null = $sha.TransformBlock($buf, 0, $r, $buf, 0) }
            $sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
            return ([System.BitConverter]::ToString($sha.Hash)).Replace('-', '').ToLowerInvariant()
        } finally { $fs.Dispose(); $sha.Dispose() }
    } catch { return $null }
}

try { Log "Waiting for process $PARENT_PID to exit"; Wait-Process -Id $PARENT_PID -ErrorAction SilentlyContinue } catch {}
try { if (-not (Test-Path -LiteralPath $DST)) { New-Item -ItemType Directory -Path $DST -Force | Out-Null } } catch {}

# Detect common top-level prefix in file list to strip (e.g., 'launcher/' wrapping all paths)
$STRIP_PREFIX = ''
try {
    if (Test-Path -LiteralPath $FILES) {
        $all = Get-Content -LiteralPath $FILES | Where-Object { $_ -and $_.Trim().Length -gt 0 }
        if ($all -and $all.Count -gt 0) {
            $seg = ($all[0] -replace '\\','/').Trim('/') -split '/' | Select-Object -First 1
            if ($seg) {
                $ok = $true
                foreach ($ln in $all) {
                    $n = ($ln -replace '\\','/').Trim('/')
                    if (-not $n.StartsWith($seg + '/', [System.StringComparison]::OrdinalIgnoreCase)) { $ok = $false; break }
                }
                if ($ok) { $STRIP_PREFIX = $seg }
            }
        }
    }
} catch { Log "prefix detect error: $($_.Exception.Message)" }

Log "Deleting stale files..."
try {
    if (Test-Path -LiteralPath $DEL) {
        Get-Content -LiteralPath $DEL | ForEach-Object {
            $rel = ($_ -replace '\\','/')
            if ($STRIP_PREFIX) { $rel = $rel.TrimStart('/') -replace ("^" + [regex]::Escape($STRIP_PREFIX) + "/"), '' }
            $rel = ($rel -replace '/','\\')
            $name = Split-Path -Path $rel -Leaf
            if ($name -ieq 'config.json') { Log "skip delete config.json"; continue }
            $path = Join-Path -Path $DST -ChildPath $rel
            if (Test-Path -LiteralPath $path) {
                try { Remove-Item -LiteralPath $path -Force -ErrorAction Continue } catch { Log "delete failed: $rel : $($_.Exception.Message)" }
            }
        }
    }
} catch { Log "delete phase error: $($_.Exception.Message)" }

Log "Copy listed files..."
try {
    if (Test-Path -LiteralPath $FILES) {
        Get-Content -LiteralPath $FILES | ForEach-Object {
            $relRaw = ($_ -replace '\\','/').TrimStart('/')
            $relSrc = $relRaw
            $relDst = $relRaw
            if ($STRIP_PREFIX) { $relDst = $relRaw -replace ("^" + [regex]::Escape($STRIP_PREFIX) + "/"), '' }
            $relSrcWin = ($relSrc -replace '/','\\')
            $relDstWin = ($relDst -replace '/','\\')
            $name = Split-Path -Path $relDstWin -Leaf
            if ($name -ieq 'config.json') { Log "skip copy config.json"; continue }
            $srcPath = Join-Path -Path $SRC -ChildPath $relSrcWin
            $dstPath = Join-Path -Path $DST -ChildPath $relDstWin
            $dstDir  = Split-Path -Path $dstPath -Parent
            if (-not (Test-Path -LiteralPath $dstDir)) { try { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null } catch {} }
            $ok = $false
            $attempts = 0
            $maxAttempts = 10
            while (-not $ok -and $attempts -lt $maxAttempts) {
                $attempts++
                try {
                    if (Test-Path -LiteralPath $dstPath) {
                        try { (Get-Item -LiteralPath $dstPath).IsReadOnly = $false } catch {}
                    }
                    Copy-Item -LiteralPath $srcPath -Destination $dstPath -Force -ErrorAction Stop
                    # quick size validation
                    try {
                        $s1 = (Get-Item -LiteralPath $srcPath).Length
                        $s2 = (Get-Item -LiteralPath $dstPath).Length
                        if ($s1 -ne $s2) { throw "size_mismatch src=$s1 dst=$s2" }
                    } catch {}
                    $ok = $true
                    Log "copied $relDst"
                } catch {
                    $delay = [Math]::Min(5000, 250 * [Math]::Pow(2, [Math]::Max(0, $attempts-1)))
                    Log "retry copy ($attempts/$maxAttempts) $relDst : $($_.Exception.Message); sleep ${delay}ms"
                    Start-Sleep -Milliseconds $delay
                }
            }
            if (-not $ok) { Log "copy failed after $maxAttempts attempts: $relDst" }
        }
    }
} catch { Log "copy phase error: $($_.Exception.Message)" }

Log "Ensure empty dirs..."
try {
    if (Test-Path -LiteralPath $DIRS) {
        Get-Content -LiteralPath $DIRS | ForEach-Object {
            $d = ($_ -replace '\\','/').Trim('/'); if ($STRIP_PREFIX) { $d = $d -replace ("^" + [regex]::Escape($STRIP_PREFIX) + "/"), '' }
            $d = ($d -replace '/','\\').Trim('\\')
            $p = Join-Path -Path $DST -ChildPath $d
            try { New-Item -ItemType Directory -Path $p -Force | Out-Null } catch {}
        }
    }
} catch { Log "dirs phase error: $($_.Exception.Message)" }

# Extra safety: ensure target EXE was updated (if present in SRC mirror) by re-copying explicitly with retries
try {
    $relExe = $null
    try {
        if ($EXE -and $DST -and $EXE.StartsWith($DST, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relExe = $EXE.Substring($DST.Length).TrimStart('\\')
        }
    } catch {}
    if ($relExe) {
        $relExeSrc = $relExe
        if ($STRIP_PREFIX) { $relExeSrc = Join-Path -Path $STRIP_PREFIX -ChildPath $relExe }
        $srcExe = Join-Path -Path $SRC -ChildPath $relExeSrc
        if (Test-Path -LiteralPath $srcExe) {
            Log "ensure EXE updated: $relExe"
            $attempts = 0; $maxAttempts = 10; $ok = $false
            while (-not $ok -and $attempts -lt $maxAttempts) {
                $attempts++
                try {
                    if (Test-Path -LiteralPath $EXE) { try { (Get-Item -LiteralPath $EXE).IsReadOnly = $false } catch {} }
                    Copy-Item -LiteralPath $srcExe -Destination $EXE -Force -ErrorAction Stop
                    try {
                        $s1=(Get-Item -LiteralPath $srcExe).Length; $s2=(Get-Item -LiteralPath $EXE).Length; if ($s1 -ne $s2) { throw "size_mismatch src=$s1 dst=$s2" }
                        $h1 = Get-FileHashSha256Hex -path $srcExe; $h2 = Get-FileHashSha256Hex -path $EXE
                        Log "exe sizes src=$s1 dst=$s2 hashes src=$h1 dst=$h2"
                    } catch {}
                    $ok = $true
                } catch {
                    $delay = [Math]::Min(5000, 250 * [Math]::Pow(2, [Math]::Max(0, $attempts-1)))
                    Log "retry EXE copy ($attempts/$maxAttempts) $relExe : $($_.Exception.Message); sleep ${delay}ms"
                    Start-Sleep -Milliseconds $delay
                }
            }
            try {
                $verSrc = (Get-Item -LiteralPath $srcExe).VersionInfo.FileVersion
                $verDst = (Get-Item -LiteralPath $EXE).VersionInfo.FileVersion
                Log "exe versions src=$verSrc dst=$verDst"
            } catch {}
        } else {
            Log "EXE not found in SRC mirror: $srcExe"
        }
    } else {
        Log "cannot compute relative EXE path from DST"
    }
} catch { Log "exe ensure error: $($_.Exception.Message)" }

# Compare hashes between SRC and DST for all files from manifest
try {
    Log "Hash compare SRC vs DST (manifest scope)"
    $okCount = 0; $mismatchCount = 0; $missingSrc = 0; $missingDst = 0
    if (Test-Path -LiteralPath $FILES) {
        Get-Content -LiteralPath $FILES | ForEach-Object {
            $relRaw = ($_ -replace '\\','/').Trim()
            if (-not $relRaw) { return }
            $relSrc = $relRaw.TrimStart('/')
            $relDst = $relSrc
            if ($STRIP_PREFIX) { $relDst = $relSrc -replace ("^" + [regex]::Escape($STRIP_PREFIX) + "/"), '' }
            $srcPath = Join-Path -Path $SRC -ChildPath ($relSrc -replace '/','\\')
            $dstPath = Join-Path -Path $DST -ChildPath ($relDst -replace '/','\\')
            $srcExists = Test-Path -LiteralPath $srcPath
            $dstExists = Test-Path -LiteralPath $dstPath
            if (-not $srcExists) { $missingSrc++; Log "hash: SRC missing $relSrc"; return }
            if (-not $dstExists) { $missingDst++; Log "hash: DST missing $relDst"; return }
            $h1 = Get-FileHashSha256Hex -path $srcPath
            $h2 = Get-FileHashSha256Hex -path $dstPath
            if ($h1 -and $h2 -and $h1 -eq $h2) { $okCount++; Log "hash ok $relDst $h2" }
            else { $mismatchCount++; Log "hash MISMATCH $relDst src=$h1 dst=$h2" }
        }
        Log "hash summary: ok=$okCount mismatch=$mismatchCount src_missing=$missingSrc dst_missing=$missingDst"
    } else {
        Log "hash: FILES list not found, skipping"
    }
} catch { Log "hash compare error: $($_.Exception.Message)" }

Log "Starting updated launcher from '$DST'"
try {
    if (Test-Path -LiteralPath $EXE) {
        Start-Process -FilePath $EXE -WorkingDirectory $DST | Out-Null
        Log "Start issued for $EXE"
    } else {
        Log "EXE not found at $EXE, searching for any .exe in $DST"
        $cand = Get-ChildItem -Path $DST -File -Filter *.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cand) {
            Start-Process -FilePath $cand.FullName -WorkingDirectory $DST | Out-Null
            Log "Fallback start: $($cand.FullName)"
        } else {
            Log "No executable found to start"
        }
    }
} catch { Log "start phase error: $($_.Exception.Message)" }
""";
                System.IO.File.WriteAllText(ps1Path, ps, System.Text.Encoding.UTF8);
                // Pre-create log with header to ensure the file exists even if robocopy doesn't run
                try
                {
                    // Use a single interpolated string to avoid writing literal placeholders
                    var header = $"[{DateTime.Now:o}] Apply started. SRC={_pendingTempRoot} DST={targetDir} EXE={currentExe} PID={pid}\r\n";
                    System.IO.File.WriteAllText(logPath, header);
                }
                catch { }

                // Запускаем один процесс PowerShell в скрытом режиме без всплывающих окон
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\" -SRC \"{_pendingTempRoot}\" -DST \"{targetDir}\" -EXE \"{currentExe}\" -PARENT_PID {pid} -LOG \"{logPath}\" -FILES \"{System.IO.Path.Combine(selfUpdateDir, "filelist.txt")}\" -DIRS \"{System.IO.Path.Combine(selfUpdateDir, "emptydirs.txt")}\" -DEL \"{System.IO.Path.Combine(selfUpdateDir, "deletelist.txt")}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = selfUpdateDir,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                };
                try { System.Diagnostics.Process.Start(psi); } catch { }

                // Завершаем приложение: освобождаем файлы и даём скрипту применить обновление
                StatusText.Text = $"Применение обновления...\nScript: {ps1Path}\nLog: {logPath}";
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка применения обновления: {ex.Message}";
                PrimaryBtn.IsEnabled = true;
                try { Core.Logging.Logger.Error(ex, "UpdateWindow.ApplyUpdate"); } catch { }
            }
        }
    }
}
