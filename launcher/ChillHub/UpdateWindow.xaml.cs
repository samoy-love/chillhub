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
            TryCleanupTempUpdaterDirs();
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
        
        // Cleanup TEMP updater directories from previous runs
        private static void TryCleanupTempUpdaterDirs()
        {
            try
            {
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate");
                if (!System.IO.Directory.Exists(root)) return;
                foreach (var verDir in System.IO.Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        var updaterDir = System.IO.Path.Combine(verDir, "updater");
                        if (System.IO.Directory.Exists(updaterDir))
                        {
                            try { System.IO.Directory.Delete(updaterDir, true); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        
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

                    // Каталог списков и план синхронизации
                    var selfUpdateDirDl = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", _remoteVersion);
                    try { if (System.IO.Directory.Exists(selfUpdateDirDl)) System.IO.Directory.Delete(selfUpdateDirDl, true); } catch { }
                    System.IO.Directory.CreateDirectory(selfUpdateDirDl);
                    var filesListPath = System.IO.Path.Combine(selfUpdateDirDl, "filelist.txt");
                    var emptyDirsPath = System.IO.Path.Combine(selfUpdateDirDl, "emptydirs.txt");
                    var deleteListPath = System.IO.Path.Combine(selfUpdateDirDl, "deletelist.txt");

                    var plan = await _sync.PlanAsync(manifest, tempRoot, contentBase, System.Threading.CancellationToken.None);
                    // Формируем файлы для копирования из реально изменённых (diff plan)
                    try
                    {
                        var changed = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(plan.Downloads, t => t.RelativePath.Replace('\\','/')));
                        System.IO.File.WriteAllLines(filesListPath, changed, System.Text.Encoding.UTF8);
                    }
                    catch { }
                    // Пустые директории — из манифеста
                    try
                    {
                        var dirLines = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(manifest.EmptyDirs, d => d.Replace('\\','/').Trim('/')));
                        System.IO.File.WriteAllLines(emptyDirsPath, dirLines, System.Text.Encoding.UTF8);
                    }
                    catch { }
                    // Список удалений — всё, чего нет в манифесте
                    try
                    {
                        var targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                        var manifestSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        foreach (var f in manifest.Files) manifestSet.Add((f.Path ?? string.Empty).Replace('\\','/').TrimStart('/'));
                        var toDelete = new System.Collections.Generic.List<string>();
                        foreach (var diskFile in System.IO.Directory.EnumerateFiles(targetDir, "*", System.IO.SearchOption.AllDirectories))
                        {
                            var rel = diskFile.Substring(targetDir.Length).TrimStart(System.IO.Path.DirectorySeparatorChar).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                            if (string.Equals(rel, "apply-update.cmd", System.StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(rel, "apply-update.log", System.StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            if (!manifestSet.Contains(rel)) toDelete.Add(rel);
                        }
                        System.IO.File.WriteAllLines(deleteListPath, toDelete, System.Text.Encoding.UTF8);
                    }
                    catch { }
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
                var logPath = System.IO.Path.Combine(selfUpdateDir, "apply-update.log");
                System.IO.Directory.CreateDirectory(selfUpdateDir);

                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                // Pre-create log with header for the native updater
                try
                {
                    // Use a single interpolated string to avoid writing literal placeholders
                    var header = $"[{DateTime.Now:o}] Apply started. SRC={_pendingTempRoot} DST={targetDir} EXE={currentExe} PID={pid}\r\n";
                    System.IO.File.WriteAllText(logPath, header);
                }
                catch { }

                // Prepare native updater in TEMP so DST copies can be freely replaced
                var tempUpdaterDir = System.IO.Path.Combine(selfUpdateDir, "updater");
                try { System.IO.Directory.CreateDirectory(tempUpdaterDir); } catch { }
                try
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(targetDir, "YourLauncher.Updater*", System.IO.SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var dstF = System.IO.Path.Combine(tempUpdaterDir, System.IO.Path.GetFileName(f));
                            System.IO.File.Copy(f, dstF, true);
                        }
                        catch { }
                    }
                }
                catch { }

                // Invoke native updater executable from TEMP (not locked in DST)
                var updaterPath = System.IO.Path.Combine(tempUpdaterDir, "YourLauncher.Updater.exe");
                var args = new System.Text.StringBuilder();
                void A(string s) { if (args.Length > 0) args.Append(' '); args.Append(s); }
                string Q(string p) => "\"" + p.Replace("\"", "\\\"") + "\"";
                A("--src " + Q(_pendingTempRoot!));
                A("--dst " + Q(targetDir));
                A("--exe " + Q(currentExe));
                A("--parent " + pid.ToString());
                A("--log " + Q(logPath));
                A("--files " + Q(System.IO.Path.Combine(selfUpdateDir, "filelist.txt")));
                A("--dirs " + Q(System.IO.Path.Combine(selfUpdateDir, "emptydirs.txt")));
                A("--del " + Q(System.IO.Path.Combine(selfUpdateDir, "deletelist.txt")));
                // Preserve rules: only config.json
                A("--preserve " + Q("config.json"));  // e.g.: "config.json,/logs,/cache" 

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempUpdaterDir,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                try { System.Diagnostics.Process.Start(psi); } catch { }

                // Завершаем приложение: освобождаем файлы и даём скрипту применить обновление
                StatusText.Text = $"Применение обновления...\nUpdater: {updaterPath}\nLog: {logPath}";
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
