// <copyright file="UpdateWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
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

        /// <summary>
        /// Сколько каталогов версий в %TEMP%\ChillHub\SelfUpdate оставляем при уборке (19b):
        /// самый свежий (его мог только что использовать апдейтер) и один предыдущий.
        /// </summary>
        private const int KeepTempSessionDirs = 2;

        /// <summary>
        /// A14. Возраст, после которого каталог сессии удаляется независимо от того,
        /// входит ли он в число самых свежих: иначе у пользователя, который давно не
        /// обновлялся, пара каталогов-ветеранов лежала бы в %TEMP% вечно.
        /// </summary>
        private const int StaleSessionDays = 7;

        /// <summary>Суффикс имени каталога, отложенного до следующей уборки (A14).</summary>
        private const string TrashSuffix = ".trash-";

        /// <summary>UTF-8 без BOM: BOM ломает сверку размеров/хешей служебных списков.</summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Единый список preserve-правил, общий с апдейтером.</summary>
        private static readonly PreserveMatcher Preserve = new PreserveMatcher();

        /// <summary>
        /// A6. Допустимая форма версии: 1.2.3, 1.2.3.4, 1.2.3-beta.1.
        /// <para>
        /// Строка приходит из latest.json, то есть С СЕТИ, и дальше подставляется в
        /// Path.Combine (%TEMP%\ChillHub\SelfUpdate\&lt;версия&gt;), в URL манифеста и в
        /// аргументы внешнего процесса. Значение вроде "..\..\Startup" уводит каталог
        /// обновления куда угодно, а любой сюрприз в кавычках/бэкслешах меняет разбор
        /// командной строки апдейтера. Проверяем ДО первого использования: версия —
        /// это данные, а не команда.
        /// </para>
        /// </summary>
        private static readonly Regex VersionPattern = new Regex(
            @"^[0-9]{1,6}(\.[0-9]{1,6}){1,3}(-[0-9A-Za-z][0-9A-Za-z.]{0,31})?$",
            RegexOptions.CultureInvariant);

        private string BaseApi => ConfigService.Current.ApiBaseUrl;

        private readonly HttpClient http = HttpClientProvider.Shared;
        private bool updateRequired = false; // есть ли новая версия
        private bool loopBlocked = false;    // A4: автообновление остановлено защитой от петли
        private bool updaterStarted = false; // A14: апдейтер запущен, его временный каталог трогать нельзя
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
            TryCleanupTempSelfUpdateDirs();
            TryCleanupInstalledUpdaterArtifacts();

            // A14. Второй заход при закрытии окна: к этому моменту каталоги, которые
            // в конструкторе были заняты (лог апдейтера, дочитывавшийся при старте),
            // обычно уже свободны. Пропускаем только случай «мы сами запустили
            // апдейтер» — там временный каталог нужен работающему процессу.
            this.Closed += (_, _) => {
                if (!this.updaterStarted) {
                    TryCleanupTempSelfUpdateDirs();
                }
            };

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
                try {
                    var markerPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.version");
                    if (System.IO.File.Exists(markerPath)) {
                        local = (System.IO.File.ReadAllText(markerPath) ?? string.Empty).Trim();
                    }
                    else {
                        var asm = Assembly.GetExecutingAssembly();
                        var v = asm?.GetName()?.Version;
                        local = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
                    }
                }
                catch {
                    var asm = Assembly.GetExecutingAssembly();
                    var v = asm?.GetName()?.Version;
                    local = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
                }

                // A6. Версия с сервера — недоверенные данные: она станет частью пути,
                // URL и аргументов внешнего процесса. Всё, что не похоже на версию,
                // отбрасываем целиком, а не «чистим».
                if (!string.IsNullOrWhiteSpace(remote) && !IsValidVersion(remote)) {
                    this.StatusText.Text =
                        "Сервер сообщил недопустимый номер версии — обновление заблокировано.\n" +
                        "Обратитесь в поддержку.";
                    this.Progress.IsIndeterminate = false;
                    this.Progress.Value = 0;
                    this.PrimaryBtn.Content = "Продолжить";
                    this.updateRequired = false;
                    this.PrimaryBtn.IsEnabled = true;
                    try {
                        Core.Logging.Logger.Error(new InvalidOperationException($"Rejected remote version from latest.json: '{remote}'"), "UpdateWindow.VersionValidation");
                    }
                    catch {
                    }

                    return;
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
                catch (Core.Sync.ManifestValidationException ex) {
                    // Манифест отклонён проверкой структуры — предлагать обновление
                    // нельзя: качать по такому манифесту мы всё равно откажемся.
                    try {
                        Core.Logging.Logger.Error(ex, "UpdateWindow.CheckManifestValidation");
                    }
                    catch {
                    }

                    this.StatusText.Text = $"Обновление заблокировано: {ex.Message}";
                    this.PrimaryBtn.IsEnabled = false;
                    return;
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

                        if (!this.LocalFileMatches(baseDir, f, out _)) {
                            allMatch = false;
                            break;
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
            finally {
                this.ShowPreviousUpdateOutcome();
            }
        }

        /// <summary>
        /// A12. Показывает исход ПРОШЛОГО запуска апдейтера.
        /// <para>
        /// Апдейтер возвращает 2 («скопировалось не всё») и 3 («фатально»), но читать
        /// эти коды некому: лаунчер к тому моменту уже завершился, а сам апдейтер
        /// умирает последним. Поэтому исход он пишет в файл состояния рядом с маркером
        /// версии, а лаунчер при следующем старте показывает его один раз — иначе
        /// неудавшееся обновление выглядит как «ничего не произошло», и пользователь
        /// снова жмёт «Обновить», не понимая, почему предыдущий раз не сработал.
        /// </para>
        /// </summary>
        private void ShowPreviousUpdateOutcome() {
            try {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var status = UpdateStatus.TryRead(baseDir);
                if (status == null) {
                    return;
                }

                // Показываем один раз: файл перезапишет следующий запуск апдейтера.
                UpdateStatus.Clear(baseDir);

                if (status.IsSuccess) {
                    try {
                        Core.Logging.Logger.Info($"Previous self-update: ok, version={status.Version}");
                    }
                    catch {
                    }

                    return;
                }

                try {
                    Core.Logging.Logger.Error(
                        new InvalidOperationException($"Previous self-update failed: outcome={status.Outcome} exit={status.ExitCode} message={status.Message} log={status.LogPath}"),
                        "UpdateWindow.PreviousUpdateOutcome");
                }
                catch {
                }

                var danger = (Brush)(this.TryFindResource("Brush.Danger") ?? new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));
                var text = "Предыдущее обновление не было применено: " +
                    (string.IsNullOrWhiteSpace(status.Message) ? status.Outcome : status.Message);
                if (!string.IsNullOrWhiteSpace(status.LogPath)) {
                    text += $"\nЖурнал: {status.LogPath}";
                }

                this.StatusText.Inlines.Add(new LineBreak());
                this.StatusText.Inlines.Add(new Run(text) { Foreground = danger });
            }
            catch {
                // Диагностика не должна мешать запуску.
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
                //
                // A4. Но и тупик здесь недопустим. К этому моменту установка уже в
                // смешанном состоянии, а счётчик сбрасывался ТОЛЬКО при remote == local,
                // то есть ровно в том случае, до которого зацикленный лаунчер и не
                // доходит: обновление запрещалось навсегда, и единственным выходом
                // оставалась переустановка вслепую. Даём конкретное действие —
                // проверку целостности: если файлы на самом деле в порядке, счётчик
                // сбрасывается и лаунчер продолжает работу.
                this.updateRequired = false;
                this.loopBlocked = true;
                this.remoteVersion = remote;
                this.Progress.IsIndeterminate = false;
                this.Progress.Value = 0;
                var logDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate", remote, "work");
                this.StatusText.Text =
                    $"Обновление {local} → {remote} применялось {attempts} раз(а) подряд и не завершилось успехом.\n" +
                    "Чтобы не зацикливаться, автообновление остановлено.\n" +
                    $"Журнал: {System.IO.Path.Combine(logDir, "apply-update.log")}\n" +
                    $"Счётчик попыток: {AttemptsFilePath}\n" +
                    "Нажмите «Проверить целостность»: файлы будут сверены с манифестом версии " +
                    $"{remote}. Если расхождений нет, счётчик сбросится и лаунчер продолжит работу; " +
                    "если есть — вы увидите список файлов, и лаунчер всё равно можно будет запустить.";
                this.PrimaryBtn.Content = "Проверить целостность";
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

        /// <summary>A6. Проверяет, что строка версии безопасна для пути, URL и аргументов.</summary>
        /// <param name="version">Версия из latest.json.</param>
        /// <returns>true, если версия допустима.</returns>
        private static bool IsValidVersion(string? version) {
            var v = (version ?? string.Empty).Trim();
            return v.Length > 0 && v.Length <= 64 && VersionPattern.IsMatch(v);
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

        /// <summary>
        /// A12. Сравнивает один файл манифеста с ФАКТИЧЕСКИМ файлом в папке установки.
        /// Возвращает true, если файл на месте и совпадает (по хешам, а при их отсутствии — по размеру).
        /// Любая ошибка чтения трактуется как «не совпадает»: лучше лишний раз скачать файл,
        /// чем оставить установку в неконсистентном состоянии.
        /// </summary>
        /// <param name="baseDir">Папка установки лаунчера.</param>
        /// <param name="f">Запись манифеста.</param>
        /// <param name="reason">Человекочитаемая причина расхождения (для лога).</param>
        /// <param name="ct">Токен отмены: подсчёт хеша большого файла — это минуты.</param>
        /// <returns>true, если локальный файл соответствует манифесту.</returns>
        private bool LocalFileMatches(string baseDir, ManifestFile f, out string reason, CancellationToken ct = default) {
            reason = string.Empty;
            try {
                var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                if (rel.Length == 0) {
                    return true;
                }

                var localRel = this.StripLocal(rel);
                var localPath = System.IO.Path.Combine(baseDir, localRel.Replace('/', System.IO.Path.DirectorySeparatorChar));

                // Р5. Раньше здесь лежала своя копия цикла хеширования — она успела разойтись
                // с копией планировщика игр (не проверяла отмену). Разные вердикты на одинаковых
                // входах приводили к тому, что лаунчер бесконечно предлагал одно и то же обновление.
                // Теперь и сверка самообновления, и сверка файлов игры идут через FileHasher.
                return FileHasher.Matches(localPath, f.Size, f.Sha256, f.Blake3, out reason, ct);
            }
            catch (OperationCanceledException) {
                // Отмену не глушим: иначе отменённая проверка «подтвердила» бы битый файл.
                throw;
            }
            catch (Exception ex) {
                reason = $"io_error {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// A12. Строит ЧЕСТНЫЙ диффовый план самообновления.
        ///
        /// Раньше план считался против пустого временного каталога, поэтому «недостающими»
        /// оказывались ВСЕ файлы манифеста и каждое обновление тянуло лаунчер целиком.
        /// Теперь сравнение идёт с фактической папкой установки, а качаем всё равно во временный
        /// каталог: файлы работающего лаунчера залочены, копирует их внешний updater после выхода.
        /// </summary>
        /// <param name="manifest">Манифест целевой версии.</param>
        /// <param name="tempRoot">Временный каталог загрузки (LocalRoot плана).</param>
        /// <param name="contentBase">База URL с файлами версии.</param>
        /// <returns>План, в котором Downloads — только реально изменившиеся файлы.</returns>
        private DiffPlan BuildSelfUpdatePlan(Manifest manifest, string tempRoot, string contentBase) {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var plan = new DiffPlan {
                GameId = manifest.GameId,
                Version = manifest.Version,
                LocalRoot = tempRoot,

                // Качаем в %TEMP%, а применяем в каталог установки — это может быть
                // другой диск. Без ApplyRoot проверка места смотрела бы только на TEMP.
                ApplyRoot = baseDir,
            };

            foreach (var f in manifest.Files) {
                var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                if (rel.Length == 0) {
                    continue;
                }

                // Preserve-файлы апдейтер не перезаписывает — качать их бессмысленно,
                // а служебный мусор апдейтера в пакет вообще попадать не должен.
                if (Preserve.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel)) {
                    continue;
                }

                if (this.LocalFileMatches(baseDir, f, out var reason)) {
                    continue;
                }

                plan.Downloads.Add(new FileTask {
                    RelativePath = rel,
                    Size = f.Size,
                    Url = contentBase.TrimEnd('/') + "/" + rel,
                    Blake3 = f.Blake3,
                    Sha256 = f.Sha256,
                    Executable = f.Executable,
                });
                plan.TotalDownloadBytes += f.Size;
                try {
                    Core.Logging.Logger.Info($"SelfUpdate diff include '{rel}' size={f.Size} reason={reason}");
                }
                catch {
                }
            }

            plan.TotalFilesToDownload = plan.Downloads.Count;

            // ВАЖНО: ToDelete и EmptyDirsToCreate плана намеренно пусты.
            // Их LocalRoot — это временный каталог, и ExecuteAsync применил бы их к нему,
            // а не к папке установки. Реальные удаления/пустые каталоги едут отдельными
            // списками (deletelist.txt / emptydirs.txt) и применяются апдейтером.
            return plan;
        }

        /// <summary>
        /// A12. Ситуация «версии разные, но все файлы манифеста уже лежат на месте».
        /// Гонять апдейтер незачем — копировать и удалять нечего. Обновляем только маркер версии,
        /// иначе диалог обновления будет всплывать при каждом запуске.
        /// </summary>
        /// <param name="manifest">Манифест целевой версии (пустой манифест маркер не обновляет).</param>
        private void MarkAlreadyUpToDate(Manifest manifest) {
            this.updateRequired = false;
            this.downloaded = false;
            this.Progress.IsIndeterminate = false;
            this.Progress.Value = 100;
            this.PrimaryBtn.Content = "Продолжить";
            this.PrimaryBtn.IsEnabled = true;

            // A8. Раньше ошибка записи маркера просто проглатывалась, а ResetUpdateAttempts()
            // вызывался всё равно. Итог: маркер по-прежнему показывает старую версию, диалог
            // обновления всплывает при КАЖДОМ запуске, а счётчик попыток обнулён — то есть
            // защита от петли, которая обязана была её остановить, обезврежена этим же кодом.
            // Теперь неудача — это неудача: счётчик не сбрасываем, попытку засчитываем
            // (после MaxSameVersionAttempts сработает loop guard и предложит выход),
            // и пользователь видит причину, а не молчаливо зацикленный диалог.
            if (manifest.Files.Count > 0 && !string.IsNullOrWhiteSpace(this.remoteVersion)) {
                if (!TryWriteVersionMarker(this.remoteVersion!, out var error)) {
                    RegisterUpdateAttempt(this.remoteVersion!);
                    this.StatusText.Text =
                        "Файлы лаунчера уже соответствуют новой версии, но записать отметку о версии не удалось:\n" +
                        $"{error}\n" +
                        $"Файл: {System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.version")}\n" +
                        "Пока это не исправлено, окно обновления будет появляться при каждом запуске.";
                    return;
                }
            }

            ResetUpdateAttempts();
            this.StatusText.Text = "Файлы лаунчера уже соответствуют новой версии — обновление не требуется.";
        }

        /// <summary>
        /// A7. Пишет маркер версии АТОМАРНО.
        /// <para>
        /// File.WriteAllText — это truncate + write: между ними файл существует и он
        /// пустой. Обрыв ровно в этот момент оставляет пустой launcher.version, а
        /// пустой маркер лаунчер читает как «версия неизвестна» — и обновление после
        /// этого не предлагается уже НИКОГДА. Поэтому содержимое сначала целиком
        /// ложится во временный файл рядом и лишь потом подменяет маркер.
        /// </para>
        /// </summary>
        /// <param name="version">Версия для записи.</param>
        /// <param name="error">Текст ошибки, если запись не удалась.</param>
        /// <returns>true, если маркер записан.</returns>
        private static bool TryWriteVersionMarker(string version, out string error) {
            error = string.Empty;
            try {
                var marker = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.version");
                AtomicFile.WriteAllText(marker, (version ?? string.Empty).Trim(), Utf8NoBom);
                return true;
            }
            catch (Exception ex) {
                error = ex.Message;
                try {
                    Core.Logging.Logger.Error(ex, "UpdateWindow.WriteVersionMarker");
                }
                catch {
                }

                return false;
            }
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

        /// <summary>
        /// 19b. Уборка %TEMP%\ChillHub\SelfUpdate от каталогов старых версий.
        ///
        /// Раньше чистилась только подпапка updater, а сами каталоги версий оставались навсегда —
        /// по копии пакета обновления на каждую версию. Теперь каталоги старых версий удаляются
        /// целиком; оставляем самые свежие: в новейшем может ещё дописывать лог апдейтер,
        /// который только что перезапустил лаунчер.
        ///
        /// Всё best-effort: залоченные файлы просто доживают до следующего запуска.
        /// </summary>
        private static void TryCleanupTempSelfUpdateDirs() {
            try {
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChillHub", "SelfUpdate");
                if (!System.IO.Directory.Exists(root)) {
                    return;
                }

                var dirs = new System.Collections.Generic.List<System.IO.DirectoryInfo>();
                foreach (var p in System.IO.Directory.EnumerateDirectories(root)) {
                    try {
                        // A14. Хвосты прошлых уборок: каталог, который не удалялся из-за
                        // залоченного файла, отправлялся в *.trash-* и добивается здесь.
                        if (System.IO.Path.GetFileName(p).Contains(TrashSuffix, StringComparison.OrdinalIgnoreCase)) {
                            TryDeleteDirectoryBestEffort(p);
                            continue;
                        }

                        dirs.Add(new System.IO.DirectoryInfo(p));
                    }
                    catch {
                    }
                }

                // Свежие — в начало списка.
                dirs.Sort((a, b) => DirStamp(b).CompareTo(DirStamp(a)));

                for (var i = 0; i < dirs.Count; i++) {
                    var dir = dirs[i].FullName;

                    // A14. «Свежесть» по позиции в списке недостаточна: если обновлений
                    // давно не было, два каталога-ветерана хранились бы вечно.
                    var stale = DirStamp(dirs[i]) < DateTime.UtcNow.AddDays(-StaleSessionDays);
                    if (i < KeepTempSessionDirs && !stale) {
                        // Свежие сессии целиком не сносим, но копию апдейтера из них выносим:
                        // старая раскладка (updater прямо в папке версии) и новая (work\updater).
                        TryDeleteDirectoryBestEffort(System.IO.Path.Combine(dir, PreserveMatcher.UpdaterArtifactDir));
                        TryDeleteDirectoryBestEffort(System.IO.Path.Combine(dir, "work", PreserveMatcher.UpdaterArtifactDir));
                        continue;
                    }

                    TryDeleteDirectoryBestEffort(dir);
                }
            }
            catch {
            }
        }

        /// <summary>Время последнего изменения каталога; при ошибке — «очень давно».</summary>
        private static DateTime DirStamp(System.IO.DirectoryInfo d) {
            try {
                var w = d.LastWriteTimeUtc;
                var c = d.CreationTimeUtc;
                return w > c ? w : c;
            }
            catch {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Удаляет каталог, переживая залоченные файлы и атрибут «только чтение».
        /// Ничего не бросает: уборка мусора не должна мешать запуску лаунчера.
        /// </summary>
        private static void TryDeleteDirectoryBestEffort(string path) {
            try {
                if (!System.IO.Directory.Exists(path)) {
                    return;
                }

                try {
                    System.IO.Directory.Delete(path, true);
                    return;
                }
                catch {
                }

                // Не вышло с первого раза: снимаем read-only и выносим файлы поштучно,
                // чтобы освободить место даже если один файл кем-то занят.
                var locked = new System.Collections.Generic.List<string>();
                try {
                    foreach (var f in System.IO.Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories)) {
                        try {
                            var attrs = System.IO.File.GetAttributes(f);
                            if ((attrs & (System.IO.FileAttributes.ReadOnly | System.IO.FileAttributes.System)) != 0) {
                                System.IO.File.SetAttributes(f, attrs & ~(System.IO.FileAttributes.ReadOnly | System.IO.FileAttributes.System));
                            }

                            System.IO.File.Delete(f);
                        }
                        catch {
                            locked.Add(f);
                        }
                    }
                }
                catch {
                }

                try {
                    System.IO.Directory.Delete(path, true);
                    return;
                }
                catch {
                }

                // A14. Каталог всё ещё занят. Раньше на этом уборка заканчивалась, и
                // залоченный каталог оставался в %TEMP% НАВСЕГДА: имя занято, при
                // следующем обновлении той же версии сессия создавалась поверх чужих
                // остатков. Уводим его в сторону (переименование работает даже с
                // открытыми внутри файлами) — имя освобождается сразу, а добьём при
                // следующем запуске, когда владелец отпустит файлы.
                try {
                    var trash = path + TrashSuffix + Guid.NewGuid().ToString("N").Substring(0, 8);
                    System.IO.Directory.Move(path, trash);
                    path = trash;
                    try {
                        System.IO.Directory.Delete(path, true);
                        return;
                    }
                    catch {
                    }
                }
                catch {
                }

                // Последний рубеж: просим систему удалить остатки при перезагрузке.
                // Работает не всегда (нужны права на HKLM), поэтому именно последний.
                foreach (var f in locked) {
                    try {
                        NativeMethods.MoveFileEx(f, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                    catch {
                    }
                }

                try {
                    Core.Logging.Logger.Warn($"SelfUpdate temp cleanup: каталог занят и оставлен до следующего запуска: {path}");
                }
                catch {
                }
            }
            catch {
            }
        }

        private static class NativeMethods {
            internal const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            internal static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
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

        /// <summary>
        /// A4. Выход из состояния «автообновление остановлено защитой от петли».
        /// <para>
        /// Сверяет установку с манифестом целевой версии. Совпало всё — установка
        /// исправна, значит петля была ложной (например, обновление уже применилось,
        /// а маркер не записался): пишем маркер и сбрасываем счётчик. Не совпало —
        /// счётчик НЕ трогаем (защита обязана остаться), но показываем конкретные
        /// файлы и разблокируем кнопку «Продолжить», чтобы пользователь не оставался
        /// заперт в диалоге обновления.
        /// </para>
        /// </summary>
        private async Task VerifyIntegrityAndUnblockAsync() {
            var remote = this.remoteVersion;
            if (string.IsNullOrWhiteSpace(remote) || !IsValidVersion(remote)) {
                this.loopBlocked = false;
                this.updateRequired = false;
                this.PrimaryBtn.Content = "Продолжить";
                this.PrimaryBtn.IsEnabled = true;
                return;
            }

            this.PrimaryBtn.IsEnabled = false;
            this.Progress.IsIndeterminate = true;
            this.StatusText.Text = $"Проверка целостности установки по манифесту {remote}...";
            try {
                var manifest = await this.sync.GetManifestAsync(
                    $"{this.BaseApi}/manifests/launcher/{remote}.json", System.Threading.CancellationToken.None);
                this.stripPrefix = ComputeStripPrefix(manifest);

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var bad = new System.Collections.Generic.List<string>();
                foreach (var f in manifest.Files) {
                    var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                    if (rel.Length == 0 || Preserve.ShouldPreserve(rel) || PreserveMatcher.IsUpdaterArtifact(rel)) {
                        continue;
                    }

                    if (!this.LocalFileMatches(baseDir, f, out var reason)) {
                        bad.Add($"{rel} — {reason}");
                    }
                }

                this.Progress.IsIndeterminate = false;
                if (bad.Count == 0) {
                    if (!TryWriteVersionMarker(remote!, out var markerError)) {
                        this.StatusText.Text =
                            "Файлы установки соответствуют новой версии, но записать отметку о версии не удалось:\n" +
                            $"{markerError}\n" +
                            "Счётчик попыток не сброшен. Проверьте права на папку установки.";
                        this.PrimaryBtn.Content = "Продолжить";
                        this.loopBlocked = false;
                        this.updateRequired = false;
                        this.PrimaryBtn.IsEnabled = true;
                        return;
                    }

                    ResetUpdateAttempts();
                    this.loopBlocked = false;
                    this.SetUpToDate();
                    this.PrimaryBtn.IsEnabled = true;
                    try {
                        Core.Logging.Logger.Info($"Loop guard released: integrity ok for {remote}, attempts reset");
                    }
                    catch {
                    }

                    return;
                }

                // Расхождения есть — счётчик оставляем как есть, но выпускаем пользователя.
                this.Progress.Value = 0;
                this.StatusText.Text =
                    $"Проверка целостности не пройдена: расхождений {bad.Count}.\n" +
                    string.Join("\n", bad.Take(5)) +
                    (bad.Count > 5 ? $"\n... и ещё {bad.Count - 5}" : string.Empty) + "\n" +
                    "Счётчик попыток не сброшен — автообновление остаётся остановленным.\n" +
                    "Переустановите лаунчер вручную или обратитесь в поддержку. Запустить лаунчер можно кнопкой ниже.";
                this.loopBlocked = false;
                this.updateRequired = false;
                this.PrimaryBtn.Content = "Продолжить";
                this.PrimaryBtn.IsEnabled = true;
                try {
                    Core.Logging.Logger.Error(
                        new InvalidOperationException($"Loop guard integrity check failed for {remote}: {string.Join("; ", bad.Take(20))}"),
                        "UpdateWindow.LoopGuardIntegrity");
                }
                catch {
                }
            }
            catch (Exception ex) {
                this.Progress.IsIndeterminate = false;
                this.StatusText.Text =
                    $"Не удалось проверить целостность: {ex.Message}\n" +
                    "Счётчик попыток не сброшен. Попробуйте позже или переустановите лаунчер вручную.";
                this.PrimaryBtn.Content = "Продолжить";
                this.loopBlocked = false;
                this.updateRequired = false;
                this.PrimaryBtn.IsEnabled = true;
                try {
                    Core.Logging.Logger.Error(ex, "UpdateWindow.LoopGuardIntegrity");
                }
                catch {
                }
            }
        }

        private async void PrimaryBtn_Click(object sender, RoutedEventArgs e) {
            // A4. В состоянии «остановлено защитой от петли» кнопка означает
            // «проверить целостность», а не «обновить»: это единственный выход,
            // не требующий переустановки вслепую.
            if (this.loopBlocked) {
                await this.VerifyIntegrityAndUnblockAsync();
                return;
            }

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

                // A6. Повторная проверка перед использованием: версия попадает в путь
                // временного каталога и в URL, а между проверкой в Window_Loaded и этим
                // местом поле могло быть переприсвоено.
                if (!IsValidVersion(this.remoteVersion)) {
                    this.StatusText.Text = "Недопустимый номер версии — обновление отменено.";
                    this.PrimaryBtn.IsEnabled = false;
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

                    // A12. План считаем против ПАПКИ УСТАНОВКИ, а не против пустого temp,
                    // иначе «недостающими» окажутся все файлы манифеста и лаунчер качается целиком.
                    var plan = this.BuildSelfUpdatePlan(manifest, tempRoot, contentBase);

                    // Список удалений — всё, чего нет в манифесте.
                    // A10: пути манифеста приводим к путям относительно папки установки (strip-prefix),
                    // иначе при упакованной корневой папке в список попадёт ВСЯ папка установки.
                    var toDelete = new System.Collections.Generic.List<string>();
                    try {
                        var targetDirForDel = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                        var manifestSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        foreach (var f in manifest.Files) {
                            manifestSet.Add(this.StripLocal(f.Path ?? string.Empty));
                        }

                        if (manifestSet.Count > 0) {
                            foreach (var diskFile in System.IO.Directory.EnumerateFiles(targetDirForDel, "*", System.IO.SearchOption.AllDirectories)) {
                                var rel = diskFile.Substring(targetDirForDel.Length).TrimStart(System.IO.Path.DirectorySeparatorChar).Replace(System.IO.Path.DirectorySeparatorChar, '/');
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
                        }

                        // Пустой манифест — удалять нечего; страхуемся от сноса установки.
                    }
                    catch {
                        // Не смогли посчитать удаления — обновление это не отменяет, список остаётся пустым.
                        toDelete.Clear();
                    }

                    // A12. Нечего копировать и нечего удалять — обновление вообще не запускаем.
                    // Иначе получаем полный цикл «останов лаунчера → апдейтер → перезапуск» впустую.
                    if (plan.Downloads.Count == 0 && toDelete.Count == 0) {
                        this.MarkAlreadyUpToDate(manifest);
                        return;
                    }

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

                    try {
                        System.IO.File.WriteAllLines(deleteListPath, toDelete, Utf8NoBom);
                    }
                    catch {
                    }

                    try {
                        Core.Logging.Logger.Info(
                            $"SelfUpdate diff: download={plan.Downloads.Count} files, {plan.TotalDownloadBytes} bytes; delete={toDelete.Count}; manifest files={manifest.Files.Count}");
                    }
                    catch {
                    }

                    this.StatusText.Text = $"Скачивание из: {contentBase}\nВременная папка: {tempRoot}";

                    this.StatusText.Text = plan.Downloads.Count > 0
                        ? $"Скачивание обновления: {plan.Downloads.Count} файл(ов) из {manifest.Files.Count}..."
                        : "Изменившихся файлов нет, применяем удаления...";
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
                catch (Core.Sync.ManifestValidationException ex) {
                    // Манифест самообновления отклонён. Ни одного байта ещё не
                    // скачано, и скачано не будет: манифест определяет, что именно
                    // ляжет на диск вместо ChillHub.exe.
                    this.StatusText.Text = $"Обновление отменено: {ex.Message}";
                    this.PrimaryBtn.IsEnabled = false;
                    this.downloaded = false;
                    try {
                        Core.Logging.Logger.Error(ex, "UpdateWindow.ManifestValidation");
                    }
                    catch {
                    }
                    return;
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

                // A10. Проверяется ВЕСЬ комплект апдейтера, а не только .exe.
                // Апдейтер — обычное framework-dependent приложение: без .dll и
                // .runtimeconfig.json его apphost падает мгновенно. Раньше проверялось
                // наличие одного YourLauncher.Updater.exe — если остальное не скопировалось
                // (антивирус, нет места, залоченный файл), лаунчер всё равно делал Shutdown,
                // апдейтер тут же умирал, и пользователь оставался вообще без приложения.
                var updaterPath = System.IO.Path.Combine(tempUpdaterDir, "YourLauncher.Updater.exe");
                var missing = new System.Collections.Generic.List<string>();
                try {
                    var sources = System.IO.Directory.EnumerateFiles(targetDir, "YourLauncher.Updater*", System.IO.SearchOption.TopDirectoryOnly).ToList();
                    if (sources.Count == 0) {
                        missing.Add("YourLauncher.Updater.* (в папке установки нет ни одного файла модуля обновления)");
                    }

                    foreach (var f in sources) {
                        var name = System.IO.Path.GetFileName(f);
                        var dstF = System.IO.Path.Combine(tempUpdaterDir, name);
                        try {
                            System.IO.File.Copy(f, dstF, true);

                            // Копия обязана совпадать по размеру: усечённая копия — это
                            // тот же мгновенный крах, только без внятного сообщения.
                            var srcLen = new System.IO.FileInfo(f).Length;
                            var dstLen = new System.IO.FileInfo(dstF).Length;
                            if (srcLen != dstLen) {
                                missing.Add($"{name} (скопировано {dstLen} из {srcLen} байт)");
                            }
                        }
                        catch (Exception ex) {
                            missing.Add($"{name} ({ex.Message})");
                        }
                    }
                }
                catch (Exception ex) {
                    missing.Add($"перечисление файлов модуля обновления: {ex.Message}");
                }

                if (!System.IO.File.Exists(updaterPath)) {
                    missing.Add("YourLauncher.Updater.exe");
                }

                if (missing.Count > 0) {
                    // A8. Без полного комплекта апдейтера гасить приложение нельзя —
                    // пользователь просто потеряет лаунчер.
                    this.StatusText.Text =
                        "Модуль обновления подготовлен не полностью, обновление не применено:\n" +
                        string.Join("\n", missing.Take(5)) + "\n" +
                        $"Каталог: {tempUpdaterDir}\n" +
                        "Попробуйте ещё раз или переустановите лаунчер вручную.";
                    this.PrimaryBtn.IsEnabled = true;
                    try {
                        Core.Logging.Logger.Error(
                            new FileNotFoundException("Updater payload incomplete: " + string.Join("; ", missing), updaterPath),
                            "UpdateWindow.ApplyUpdate");
                    }
                    catch {
                    }

                    return;
                }

                // A9. Исходные аргументы командной строки лаунчера — в файл (по строке
                // на аргумент). Раньше они просто терялись: апдейтер поднимал лаунчер
                // «голым», и запуск с параметром (например, автозапуск игры) молча
                // превращался в обычный старт. Файл вместо строки — чтобы ничего не
                // экранировать и не разбирать заново.
                var exeArgsPath = System.IO.Path.Combine(selfUpdateDir, "exeargs.txt");
                try {
                    var original = Environment.GetCommandLineArgs();
                    var carry = new System.Collections.Generic.List<string>();
                    for (var i = 1; i < original.Length; i++) {
                        var a = original[i] ?? string.Empty;

                        // Перевод строки в аргументе разрушил бы построчный формат.
                        if (a.Contains('\n') || a.Contains('\r')) {
                            continue;
                        }

                        carry.Add(a);
                    }

                    System.IO.File.WriteAllLines(exeArgsPath, carry, Utf8NoBom);
                }
                catch {
                    exeArgsPath = string.Empty;
                }

                var psi = new System.Diagnostics.ProcessStartInfo {
                    FileName = updaterPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempUpdaterDir,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };

                // A6. ArgumentList вместо ручной сборки строки. Прежний Q() экранировал
                // только кавычку и не удваивал бэкслеши, поэтому путь, заканчивающийся
                // на '\' (а каталог установки — ровно такой случай), съедал закрывающую
                // кавычку и склеивал соседние аргументы. ArgumentList делает это по
                // правилам Windows и не требует от нас ничего угадывать.
                void A(string key, string value) {
                    psi.ArgumentList.Add(key);
                    psi.ArgumentList.Add(value);
                }

                A("--src", this.pendingTempRoot!);
                A("--dst", targetDir);
                A("--exe", currentExe);
                A("--parent", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                A("--log", logPath);
                A("--files", System.IO.Path.Combine(selfUpdateDir, "filelist.txt"));
                A("--dirs", System.IO.Path.Combine(selfUpdateDir, "emptydirs.txt"));
                A("--del", System.IO.Path.Combine(selfUpdateDir, "deletelist.txt"));
                if (!string.IsNullOrWhiteSpace(exeArgsPath)) {
                    A("--exe-args-file", exeArgsPath);
                }

                if (!string.IsNullOrWhiteSpace(this.remoteVersion)) {
                    A("--version", this.remoteVersion!);
                }

                // A10. Strip-prefix считаем на стороне лаунчера (по манифесту) и запрещаем автодетект,
                // чтобы обе стороны одинаково понимали пути.
                A("--auto-strip", "false");
                if (this.stripPrefix.Length > 0) {
                    A("--strip-prefix", this.stripPrefix);
                }

                // A2. Preserve-правила берём из общего PreserveMatcher, а не из строкового литерала.
                A("--preserve", PreserveMatcher.DefaultRulesArg);

                // A3. Замок на каталог установки держит работающий апдейтер. Если он
                // занят — обновление уже применяется (второй экземпляр лаунчера, двойной
                // клик, зависший прошлый прогон). Запускать второй апдейтер в ту же
                // папку нельзя: два процесса перемешают файлы и бэкапы, и откат любого
                // из них оставит смесь версий.
                if (UpdateLock.IsBusy(targetDir)) {
                    this.StatusText.Text =
                        "Обновление уже применяется другим процессом.\n" +
                        "Дождитесь его завершения и запустите лаунчер снова.";
                    this.PrimaryBtn.IsEnabled = true;
                    try {
                        Core.Logging.Logger.Warn($"Self-update skipped: install lock is busy ({targetDir})");
                    }
                    catch {
                    }

                    return;
                }

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
                this.updaterStarted = true;
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
