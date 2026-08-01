// <copyright file="UpdateWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Documents;
    using System.Windows.Media;

    using ChillHub.Core;
    using ChillHub.Core.Net;
    using ChillHub.Core.Sync;
    using ChillHub.Update;

    public partial class UpdateWindow : Window {
        /// <summary>
        /// Сколько раз подряд разрешено применять обновление на одну и ту же версию.
        /// Больше — значит апдейтер не доводит дело до конца, и мы крутимся в петле.
        /// </summary>
        private const int MaxSameVersionAttempts = 3;

        /// <summary>UTF-8 без BOM: BOM ломает сверку размеров/хешей служебных списков.</summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Единый список preserve-правил, общий с апдейтером.</summary>
        private static readonly PreserveMatcher Preserve = new PreserveMatcher();

        private string BaseApi => ConfigService.Current.ApiBaseUrl;

        private readonly HttpClient http = HttpClientProvider.Shared;
        private bool updateRequired = false; // есть ли новая версия
        private bool downloaded = false;     // скачан ли пакет
        private string? remoteVersion;
        private string stripPrefix = string.Empty; // корневая папка внутри пакета (обычно пусто)
        private readonly ISyncService sync = new SimpleSyncService();

        public bool Proceed { get; private set; } = false;

        private sealed class LatestMeta {
            public string Version { get; set; } = string.Empty;
        }

        public UpdateWindow() {
            this.InitializeComponent();
            TryCleanupTempUpdaterDirs();
            TryCleanupInstalledUpdaterArtifacts();

            // In DEBUG builds, pre-check the DEV skip checkbox by default
            // so developers can easily bypass self-update if they choose.
#if DEBUG
            try {
                this.DevSkipCheck.IsChecked = true;
            }
            catch {
            }
#endif

            // In Release builds, hide the development-only controls to prevent skipping updates.
            // Window uses SizeToContent=Height so it will shrink automatically.
#if !DEBUG
            try
            {
                this.DevPanel.Visibility = Visibility.Collapsed;
            }
            catch
            {
            }
#endif
        }

        private void SetUpdateAvailableStatus(string local, string remote) {
            // Resolve theme brushes
            var danger = (Brush)(this.TryFindResource("Brush.Danger") ?? new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));
            var success = (Brush)(this.TryFindResource("Brush.Success") ?? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)));
            var normal = (Brush)(this.TryFindResource("Brush.Text") ?? SystemColors.ControlTextBrush);

            this.StatusText.Inlines.Clear();
            this.StatusText.Inlines.Add(new Run("Доступно обновление лаунчера: ") { Foreground = normal });
            this.StatusText.Inlines.Add(new Run(local) { Foreground = danger, FontWeight = FontWeights.SemiBold });
            this.StatusText.Inlines.Add(new Run(" → ") { Foreground = normal });
            var boldNew = new Bold(new Run(remote) { Foreground = success });
            this.StatusText.Inlines.Add(boldNew);
            this.StatusText.Inlines.Add(new Run(".") { Foreground = normal });
        }

        /// <summary>Помечает состояние «установлена актуальная версия».</summary>
        private void SetUpToDate() {
            this.StatusText.Text = "Установлена актуальная версия лаунчера.";
            this.Progress.IsIndeterminate = false;
            this.Progress.Value = 100;
            this.PrimaryBtn.Content = "Продолжить";
            this.updateRequired = false;
        }

        /// <summary>Помечает состояние «доступно обновление» и настраивает DEV-скип.</summary>
        private void SetUpdateRequired(string local, string remote) {
            this.updateRequired = true;
            this.remoteVersion = remote;
            this.Progress.IsIndeterminate = false;
            this.Progress.Value = 0;
            this.SetUpdateAvailableStatus(local, remote);
            this.PrimaryBtn.Content = "Обновить и перезапустить";
#if DEBUG
            try {
                if (this.DevPanel.Visibility == Visibility.Visible) {
                    this.DevSkipCheck.Checked += (s, _) => { this.PrimaryBtn.Content = "Продолжить без обновления (DEV)"; };
                    this.DevSkipCheck.Unchecked += (s, _) => { this.PrimaryBtn.Content = "Обновить и перезапустить"; };
                    if (this.DevSkipCheck.IsChecked == true) {
                        this.PrimaryBtn.Content = "Продолжить без обновления (DEV)";
                    }
                }
            }
            catch {
            }
#endif
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e) {
            try {
                this.StatusText.Text = "Проверка обновлений лаунчера...";
                this.Progress.IsIndeterminate = true;
                this.PrimaryBtn.IsEnabled = false;
                var latest = await this.http.GetFromJsonAsync<LatestMeta>($"{this.BaseApi}/manifests/launcher/latest.json");
                var remote = latest?.Version?.Trim();
                // Prefer a version marker written by updater; fallback to assembly version
                string local;
                try
                {
                    var markerPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.version");
                    if (System.IO.File.Exists(markerPath))
                    {
                        local = (System.IO.File.ReadAllText(markerPath) ?? string.Empty).Trim();
                    }
                    else
                    {
                        var asm = Assembly.GetExecutingAssembly();
                        var v = asm?.GetName()?.Version;
                        local = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
                    }
                }
                catch
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var v = asm?.GetName()?.Version;
                    local = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
                }

                if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(local)) {
                    // Ничего не знаем — даём пользователю решить
                    this.StatusText.Text = "Информация о версии отсутствует.";
                    this.Progress.IsIndeterminate = false;
                    this.Progress.Value = 0;
                    this.PrimaryBtn.Content = "Продолжить";
                    this.updateRequired = false;
                    this.PrimaryBtn.IsEnabled = true;
                    return;
                }

                // A1. Главный предохранитель: если версии совпали — обновляться не надо ВООБЩЕ.
                // Посимвольную сверку хешей запускать нельзя: preserve-файлы (config.json,
                // launcher.version) заведомо расходятся с манифестом и дают вечную петлю.
                if (string.Equals(remote, local, StringComparison.OrdinalIgnoreCase)) {
                    ResetUpdateAttempts();
                    this.SetUpToDate();
                    this.PrimaryBtn.IsEnabled = true;
                    return;
                }

                // Версии разные — уточняем решение по манифесту (вдруг файлы уже на месте).
                Manifest? mf = null;
                try {
                    var manifestUrl = $"{this.BaseApi}/manifests/launcher/{remote}.json";
                    mf = await this.sync.GetManifestAsync(manifestUrl, System.Threading.CancellationToken.None);
                }
                catch {
                    // Фоллбэк: если манифест не доступен — используем сравнение по версии, как раньше
                    this.ApplyDecision(true, local, remote);
                    this.PrimaryBtn.IsEnabled = true;
                    return;
                }

                // A10. Пакет может быть упакован с корневой папкой — считаем префикс один раз
                // и используем его симметрично: и в сверке хешей, и в списке удалений, и в аргументах апдейтера.
                this.stripPrefix = ComputeStripPrefix(mf);

                // 2) Сравниваем локальные файлы с хешами из манифеста
                bool allMatch = true;
                try {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    foreach (var f in mf.Files) {
                        var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                        if (rel.Length == 0) {
                            continue;
                        }

                        // A2. Preserve-файлы принципиально не совпадают с манифестом
                        // (апдейтер их не перезаписывает) — они не могут быть причиной обновления.
                        if (Preserve.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel)) {
                            continue;
                        }

                        var localRel = this.StripLocal(rel);
                        var localPath = System.IO.Path.Combine(baseDir, localRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        if (!System.IO.File.Exists(localPath)) {
                            allMatch = false;
                            break;
                        }

                        // Если в манифесте есть sha256/blake3 — считаем оба, иначе считаем совпадением по размеру
                        if (!string.IsNullOrWhiteSpace(f.Sha256) || !string.IsNullOrWhiteSpace(f.Blake3)) {
                            using var fs = new System.IO.FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: false);
                            using var sha = System.Security.Cryptography.SHA256.Create();
                            var b3 = Blake3.Hasher.New();
                            var buf = new byte[256 * 1024];
                            int r;
                            while ((r = fs.Read(buf, 0, buf.Length)) > 0) {
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
                            if (!(okSha && okB3)) {
                                allMatch = false;
                                break;
                            }
                        }
                        else {
                            var info = new System.IO.FileInfo(localPath);
                            if (info.Length != f.Size) {
                                allMatch = false;
                                break;
                            }
                        }
                    }
                }
                catch {
                    allMatch = false;
                }

                this.ApplyDecision(!allMatch, local, remote);

                // Разблокируем кнопку после завершения проверки
                this.PrimaryBtn.IsEnabled = true;
            }
            catch (Exception ex) {
                // Нет сети/latest — даём пользователю решить
                this.StatusText.Text = $"Не удалось проверить обновление (GET {this.BaseApi}/manifests/launcher/latest.json): {ex.Message}";
                this.Progress.IsIndeterminate = false;
                this.Progress.Value = 0;
                this.PrimaryBtn.Content = "Продолжить";
                this.updateRequired = false;
                this.PrimaryBtn.IsEnabled = true;
                try {
                    Core.Logging.Logger.Error(ex, "UpdateWindow.Window_Loaded");
                }
                catch {
                }
            }
        }

        /// <summary>
        /// Применяет решение «нужно обновление / не нужно» с учётом защиты от зацикливания (A1).
        /// </summary>
        private void ApplyDecision(bool needUpdate, string local, string remote) {
            if (!needUpdate) {
                ResetUpdateAttempts();
                this.SetUpToDate();
                return;
            }

            var attempts = GetUpdateAttempts(remote);
            if (attempts >= MaxSameVersionAttempts) {
                // Обновление на одну и ту же версию применяется по кругу — дальше не пускаем.
                this.updateRequired = false;
                this.remoteVersion = remote;
                this.Progress.IsIndeterminate = false;
                this.Progress.Value = 0;
                var logDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", remote, "work");
                this.StatusText.Text =
                    $"Обновление {local} → {remote} применялось {attempts} раз(а) подряд и не завершилось успехом.\n" +
                    "Чтобы не зацикливаться, автообновление остановлено.\n" +
                    $"Журнал: {System.IO.Path.Combine(logDir, "apply-update.log")}\n" +
                    $"Счётчик попыток: {AttemptsFilePath}\n" +
                    "Переустановите лаунчер вручную или обратитесь в поддержку.";
                this.PrimaryBtn.Content = "Продолжить";
                try {
                    Core.Logging.Logger.Error(new InvalidOperationException($"Self-update loop detected: {local} -> {remote}, attempts={attempts}"), "UpdateWindow.LoopGuard");
                }
                catch {
                }

                return;
            }

            this.SetUpdateRequired(local, remote);
        }

        /// <summary>
        /// Определяет общий корневой каталог всех путей манифеста (strip-prefix).
        /// Пустая строка — файлы лежат в корне пакета (текущий случай).
        /// </summary>
        private static string ComputeStripPrefix(Manifest mf) {
            string? candidate = null;
            foreach (var f in mf.Files) {
                var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                if (rel.Length == 0) {
                    continue;
                }

                var idx = rel.IndexOf('/', StringComparison.Ordinal);
                if (idx <= 0) {
                    // Есть файл в корне пакета — значит общей корневой папки нет.
                    return string.Empty;
                }

                var seg = rel.Substring(0, idx);
                if (candidate == null) {
                    candidate = seg;
                }
                else if (!candidate.Equals(seg, StringComparison.OrdinalIgnoreCase)) {
                    return string.Empty;
                }
            }

            return candidate ?? string.Empty;
        }

        /// <summary>Переводит путь из манифеста в путь относительно папки установки.</summary>
        private string StripLocal(string rel) {
            var norm = (rel ?? string.Empty).Replace('\\', '/').Trim('/');
            if (this.stripPrefix.Length == 0) {
                return norm;
            }

            return norm.StartsWith(this.stripPrefix + "/", StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(this.stripPrefix.Length + 1)
                : norm;
        }

        private void ExitBtn_Click(object sender, RoutedEventArgs e) {
            this.DialogResult = false;
        }

        private string? pendingTempRoot;
        private string? pendingWorkDir;

        // ---------------------------------------------------------------------
        // Защита от зацикливания: счётчик применений обновления на одну версию.
        // ---------------------------------------------------------------------
        private static string AttemptsFilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChillHub",
            "selfupdate-attempts.txt");

        private static int GetUpdateAttempts(string version) {
            try {
                var path = AttemptsFilePath;
                if (!System.IO.File.Exists(path)) {
                    return 0;
                }

                var parts = (System.IO.File.ReadAllText(path) ?? string.Empty).Split('|');
                if (parts.Length < 2) {
                    return 0;
                }

                if (!string.Equals(parts[0].Trim(), version, StringComparison.OrdinalIgnoreCase)) {
                    return 0;
                }

                return int.TryParse(parts[1].Trim(), out var n) ? n : 0;
            }
            catch {
                return 0;
            }
        }

        private static void RegisterUpdateAttempt(string version) {
            try {
                var path = AttemptsFilePath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var n = GetUpdateAttempts(version) + 1;
                System.IO.File.WriteAllText(path, $"{version}|{n}|{DateTime.Now:O}", Utf8NoBom);
            }
            catch {
            }
        }

        private static void ResetUpdateAttempts() {
            try {
                var path = AttemptsFilePath;
                if (System.IO.File.Exists(path)) {
                    System.IO.File.Delete(path);
                }
            }
            catch {
            }
        }

        // Cleanup TEMP updater directories from previous runs
        private static void TryCleanupTempUpdaterDirs() {
            try {
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate");
                if (!System.IO.Directory.Exists(root)) {
                    return;
                }

                foreach (var verDir in System.IO.Directory.EnumerateDirectories(root)) {
                    // Старая раскладка (updater прямо в папке версии) и новая (work\updater).
                    foreach (var candidate in new[] {
                        System.IO.Path.Combine(verDir, PreserveMatcher.UpdaterArtifactDir),
                        System.IO.Path.Combine(verDir, "work", PreserveMatcher.UpdaterArtifactDir),
                    }) {
                        try {
                            if (System.IO.Directory.Exists(candidate)) {
                                System.IO.Directory.Delete(candidate, true);
                            }
                        }
                        catch {
                        }
                    }
                }
            }
            catch {
            }
        }

        /// <summary>
        /// A6. Разовая очистка папки установки от служебных файлов апдейтера,
        /// которые прошлые версии «зеркалили» из TEMP (filelist.txt, apply-update.log, updater\ и т.п.).
        /// </summary>
        private static void TryCleanupInstalledUpdaterArtifacts() {
            try {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var name in PreserveMatcher.UpdaterArtifactFiles) {
                    try {
                        var p = System.IO.Path.Combine(baseDir, name);
                        if (System.IO.File.Exists(p)) {
                            System.IO.File.Delete(p);
                        }
                    }
                    catch {
                    }
                }

                try {
                    var dir = System.IO.Path.Combine(baseDir, PreserveMatcher.UpdaterArtifactDir);
                    if (System.IO.Directory.Exists(dir)) {
                        System.IO.Directory.Delete(dir, true);
                    }
                }
                catch {
                }
            }
            catch {
            }
        }

        private async void PrimaryBtn_Click(object sender, RoutedEventArgs e) {
            // DEV-скип: только в Debug и только если панель видима; в Release невозможно
#if DEBUG
            var devSkip = this.DevPanel.Visibility == Visibility.Visible && this.DevSkipCheck.IsChecked == true;
#endif

#if DEBUG
            if (!this.updateRequired || devSkip)
#else
            if (!this.updateRequired)
#endif
            {
                this.Proceed = true;
                try {
                    this.DialogResult = true;
                }
                catch {
                    this.Close();
                }
                return;
            }

            // Если пакет не скачан — качаем
            if (!this.downloaded) {
                if (string.IsNullOrWhiteSpace(this.remoteVersion)) {
                    return;
                }

                string manifestUrl = string.Empty;
                string contentBase = string.Empty;
                try {
                    this.PrimaryBtn.IsEnabled = false;
                    this.StatusText.Text = "Запрос манифеста лаунчера...";
                    this.Progress.IsIndeterminate = true;

                    manifestUrl = $"{this.BaseApi}/manifests/launcher/{this.remoteVersion}.json";
                    contentBase = $"{this.BaseApi}/content/launcher/{this.remoteVersion}/files";
                    this.StatusText.Text = $"Манифест: {manifestUrl}";
                    var manifest = await this.sync.GetManifestAsync(manifestUrl, System.Threading.CancellationToken.None);
                    this.stripPrefix = ComputeStripPrefix(manifest);

                    this.StatusText.Text = "Подготовка каталога загрузки...";

                    // A6. Полезная нагрузка и служебные файлы — в РАЗНЫХ подкаталогах.
                    // Раньше это был один путь, из-за чего «остаточное зеркалирование» в апдейтере
                    // копировало filelist.txt / apply-update.log / updater\ прямо в папку установки.
                    var sessionRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", this.remoteVersion);
                    var tempRoot = System.IO.Path.Combine(sessionRoot, "payload");
                    var workDir = System.IO.Path.Combine(sessionRoot, "work");

                    // Чистим сессию целиком, чтобы не было нулевых файлов от прошлых попыток
                    try {
                        if (System.IO.Directory.Exists(sessionRoot)) {
                            System.IO.Directory.Delete(sessionRoot, true);
                        }
                    }
                    catch {
                    }

                    System.IO.Directory.CreateDirectory(tempRoot);
                    System.IO.Directory.CreateDirectory(workDir);

                    var filesListPath = System.IO.Path.Combine(workDir, "filelist.txt");
                    var emptyDirsPath = System.IO.Path.Combine(workDir, "emptydirs.txt");
                    var deleteListPath = System.IO.Path.Combine(workDir, "deletelist.txt");

                    var plan = await this.sync.PlanAsync(manifest, tempRoot, contentBase, System.Threading.CancellationToken.None);

                    // Формируем файлы для копирования из реально изменённых (diff plan),
                    // исключая preserve-файлы: апдейтер их всё равно не тронет.
                    try {
                        var changed = System.Linq.Enumerable.ToArray(
                            System.Linq.Enumerable.Where(
                                System.Linq.Enumerable.Select(plan.Downloads, t => t.RelativePath.Replace('\\', '/')),
                                rel => !Preserve.ShouldPreserve(rel) && !PreserveMatcher.IsUpdaterArtifact(rel)));
                        System.IO.File.WriteAllLines(filesListPath, changed, Utf8NoBom);
                    }
                    catch {
                    }

                    // Пустые директории — из манифеста
                    try {
                        var dirLines = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(manifest.EmptyDirs, d => this.StripLocal(d)));
                        System.IO.File.WriteAllLines(emptyDirsPath, dirLines, Utf8NoBom);
                    }
                    catch {
                    }

                    // Список удалений — всё, чего нет в манифесте.
                    // A10: пути манифеста приводим к путям относительно папки установки (strip-prefix),
                    // иначе при упакованной корневой папке в список попадёт ВСЯ папка установки.
                    try {
                        var targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                        var manifestSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        foreach (var f in manifest.Files) {
                            manifestSet.Add(this.StripLocal(f.Path ?? string.Empty));
                        }

                        if (manifestSet.Count == 0) {
                            // Пустой манифест — удалять нечего; страхуемся от сноса установки.
                            System.IO.File.WriteAllLines(deleteListPath, Array.Empty<string>(), Utf8NoBom);
                        }
                        else {
                            var toDelete = new System.Collections.Generic.List<string>();
                            foreach (var diskFile in System.IO.Directory.EnumerateFiles(targetDir, "*", System.IO.SearchOption.AllDirectories)) {
                                var rel = diskFile.Substring(targetDir.Length).TrimStart(System.IO.Path.DirectorySeparatorChar).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                                if (Preserve.ShouldPreserve(rel)) {
                                    continue;
                                }

                                if (PreserveMatcher.IsUpdaterArtifact(rel)) {
                                    // Служебный мусор апдейтера удаляет он сам (CleanupUpdaterArtifacts).
                                    continue;
                                }

                                if (!manifestSet.Contains(rel)) {
                                    toDelete.Add(rel);
                                }
                            }

                            System.IO.File.WriteAllLines(deleteListPath, toDelete, Utf8NoBom);
                        }
                    }
                    catch {
                    }

                    this.StatusText.Text = $"Скачивание из: {contentBase}\nВременная папка: {tempRoot}";

                    this.StatusText.Text = "Скачивание файлов обновления...";
                    var prog = new Progress<SyncProgress>(p => {
                        this.Progress.IsIndeterminate = false;
                        if (p.TotalBytes > 0) {
                            this.Progress.Value = Math.Min(100, Math.Max(0, (p.BytesDownloaded * 100.0) / p.TotalBytes));
                        }
                    });

                    await this.sync.ExecuteAsync(plan, prog, System.Threading.CancellationToken.None);

                    this.pendingTempRoot = tempRoot;
                    this.pendingWorkDir = workDir;
                    this.downloaded = true;
                    this.StatusText.Text = "Обновление загружено. Применяем и перезапускаем...";
                }
                catch (InvalidDataException ex) {
                    // Обычно это несоответствие хэшей (sha256/blake3)
                    this.StatusText.Text = $"Проверка целостности не пройдена: {ex.Message}. Попробуйте ещё раз. Если проблема повторяется — обратитесь в поддержку.";
                    this.PrimaryBtn.IsEnabled = true;
                    this.downloaded = false;
                    try {
                        Core.Logging.Logger.Error(ex, "UpdateWindow.DownloadIntegrity");
                    }
                    catch {
                    }
                    return;
                }
                catch (Exception ex) {
                    this.StatusText.Text = $"Ошибка загрузки/проверки обновления (manifest: {manifestUrl}, content: {contentBase}): {ex.Message}";
                    this.PrimaryBtn.IsEnabled = true;
                    this.downloaded = false;
                    try {
                        Core.Logging.Logger.Error(ex, "UpdateWindow.DownloadUpdate");
                    }
                    catch {
                    }
                    return;
                }
                finally {
                    // fallthrough к применению
                }
            }

            // Применение (создание скрипта, копирование и перезапуск)
            try {
                if (string.IsNullOrWhiteSpace(this.pendingTempRoot) || !System.IO.Directory.Exists(this.pendingTempRoot)) {
                    this.StatusText.Text = "Не найден пакет обновления.";
                    this.PrimaryBtn.IsEnabled = true;
                    return;
                }

                var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetExecutingAssembly().Location;

                // Надёжнее берем корень через AppDomain (папка запуска)
                var targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                var selfUpdateDir = this.pendingWorkDir ?? System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", this.remoteVersion ?? "pending", "work");
                var logPath = System.IO.Path.Combine(selfUpdateDir, "apply-update.log");
                System.IO.Directory.CreateDirectory(selfUpdateDir);

                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

                // Pre-create log with header for the native updater
                try {
                    // Use a single interpolated string to avoid writing literal placeholders
                    var header = $"[{DateTime.Now:o}] Apply started. SRC={this.pendingTempRoot} DST={targetDir} EXE={currentExe} PID={pid}\r\n";
                    System.IO.File.WriteAllText(logPath, header, Utf8NoBom);
                }
                catch {
                }

                // Prepare native updater in TEMP so DST copies can be freely replaced
                var tempUpdaterDir = System.IO.Path.Combine(selfUpdateDir, PreserveMatcher.UpdaterArtifactDir);
                try {
                    System.IO.Directory.CreateDirectory(tempUpdaterDir);
                }
                catch {
                }
                try {
                    foreach (var f in System.IO.Directory.EnumerateFiles(targetDir, "YourLauncher.Updater*", System.IO.SearchOption.TopDirectoryOnly)) {
                        try {
                            var dstF = System.IO.Path.Combine(tempUpdaterDir, System.IO.Path.GetFileName(f));
                            System.IO.File.Copy(f, dstF, true);
                        }
                        catch {
                        }
                    }
                }
                catch {
                }

                // Invoke native updater executable from TEMP (not locked in DST)
                var updaterPath = System.IO.Path.Combine(tempUpdaterDir, "YourLauncher.Updater.exe");
                if (!System.IO.File.Exists(updaterPath)) {
                    // A8. Без апдейтера гасить приложение нельзя — пользователь просто потеряет лаунчер.
                    this.StatusText.Text = $"Не найден модуль обновления: {updaterPath}\nОбновление не применено. Переустановите лаунчер вручную.";
                    this.PrimaryBtn.IsEnabled = true;
                    try {
                        Core.Logging.Logger.Error(new FileNotFoundException("Updater not found", updaterPath), "UpdateWindow.ApplyUpdate");
                    }
                    catch {
                    }

                    return;
                }

                var args = new System.Text.StringBuilder();
                void A(string s) {
                    if (args.Length > 0) {
                        args.Append(' ');
                    }
                    args.Append(s);
                }
                string Q(string p) => "\"" + p.Replace("\"", "\\\"") + "\"";
                A("--src " + Q(this.pendingTempRoot!));
                A("--dst " + Q(targetDir));
                A("--exe " + Q(currentExe));
                A("--parent " + pid.ToString());
                A("--log " + Q(logPath));
                A("--files " + Q(System.IO.Path.Combine(selfUpdateDir, "filelist.txt")));
                A("--dirs " + Q(System.IO.Path.Combine(selfUpdateDir, "emptydirs.txt")));
                A("--del " + Q(System.IO.Path.Combine(selfUpdateDir, "deletelist.txt")));
                if (!string.IsNullOrWhiteSpace(this.remoteVersion))
                {
                    A("--version " + Q(this.remoteVersion!));
                }

                // A10. Strip-prefix считаем на стороне лаунчера (по манифесту) и запрещаем автодетект,
                // чтобы обе стороны одинаково понимали пути.
                A("--auto-strip false");
                if (this.stripPrefix.Length > 0) {
                    A("--strip-prefix " + Q(this.stripPrefix));
                }

                // A2. Preserve-правила берём из общего PreserveMatcher, а не из строкового литерала.
                A("--preserve " + Q(PreserveMatcher.DefaultRulesArg));

                var psi = new System.Diagnostics.ProcessStartInfo {
                    FileName = updaterPath,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempUpdaterDir,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };

                System.Diagnostics.Process? started = null;
                Exception? startError = null;
                try {
                    started = System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex) {
                    startError = ex;
                }

                if (started == null) {
                    // A8. Апдейтер не стартовал — НЕ закрываем приложение.
                    this.StatusText.Text = $"Не удалось запустить модуль обновления:\n{updaterPath}\n{startError?.Message ?? "процесс не создан"}";
                    this.PrimaryBtn.IsEnabled = true;
                    try {
                        Core.Logging.Logger.Error(startError ?? new InvalidOperationException("Process.Start returned null"), "UpdateWindow.StartUpdater");
                    }
                    catch {
                    }

                    return;
                }

                // Фиксируем попытку только когда апдейтер реально запущен (A1: защита от петли).
                RegisterUpdateAttempt(this.remoteVersion ?? string.Empty);

                // Завершаем приложение: освобождаем файлы и даём скрипту применить обновление
                this.StatusText.Text = $"Применение обновления...\nUpdater: {updaterPath}\nLog: {logPath}";
                Application.Current.Shutdown();
            }
            catch (Exception ex) {
                this.StatusText.Text = $"Ошибка применения обновления: {ex.Message}";
                this.PrimaryBtn.IsEnabled = true;
                try {
                    Core.Logging.Logger.Error(ex, "UpdateWindow.ApplyUpdate");
                }
                catch {
                }
            }
        }
    }
}
