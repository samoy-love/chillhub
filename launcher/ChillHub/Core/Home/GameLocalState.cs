// <copyright file="GameLocalState.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    using ChillHub.Core.Sync;

    /// <summary>
    /// Локальное состояние установленной игры на диске: путь к папке, маркер версии `.version`,
    /// маркер незавершённого обновления, наличие полезных файлов, ярлык на рабочем столе.
    /// Никакого UI — только файловая система.
    /// </summary>
    internal static class GameLocalState {
        /// <summary>Имя файла-маркера с установленной версией.</summary>
        internal const string VersionMarkerFileName = Sync.IntegrityChecker.VersionMarkerFileName;

        /// <summary>Имя файла-маркера с версией установленного модпака.</summary>
        internal const string ModsVersionMarkerFileName = Sync.IntegrityChecker.ModsVersionMarkerFileName;

        /// <summary>Имя файла с копией установленного манифеста модпака.</summary>
        internal const string ModsManifestFileName = Sync.IntegrityChecker.ModsManifestFileName;

        /// <summary>ProgID оболочки Windows, через которую создаётся файл ярлыка.</summary>
        private const string ShellProgId = "WScript.Shell";

        /// <summary>Имя exe лаунчера: на него ведёт ярлык игры.</summary>
        private const string LauncherFileName = "ChillHub.exe";

        /// <summary>Потолок размера `.lnk`, который мы вообще разбираем (обычный ярлык — единицы килобайт).</summary>
        private const long MaxLinkBytes = 512 * 1024;

        /// <summary>
        /// Настройки JSON для копии манифеста модпака.
        /// <para>
        /// Файл читает только лаунчер, поэтому он компактный — но пишется он в папку
        /// игры, куда пользователь заглядывает руками, и при разборе жалоб его открывают
        /// глазами. Отступы стоят пары килобайт на полторы тысячи файлов.
        /// </para>
        /// </summary>
        private static readonly JsonSerializerOptions ModsManifestJson = new JsonSerializerOptions {
            WriteIndented = true,
        };

        /// <summary>
        /// Подмена окружения ярлыка на время теста; null — работает настоящее окружение.
        /// <para>
        /// AsyncLocal, а не обычное статическое поле, по той же причине, что и у конфига:
        /// подмена видна только тому потоку выполнения, где её выставили, и не уводит
        /// соседний тест в чужой каталог.
        /// </para>
        /// </summary>
        private static readonly AsyncLocal<ShortcutEnvironment?> ScopedShortcutEnv
            = new AsyncLocal<ShortcutEnvironment?>();

        /// <summary>
        /// Путь к локальной папке игры. Тонкая обёртка над <see cref="Sync.IntegrityChecker.GameLocalRoot"/>:
        /// здесь только подстановка папки игр из конфига.
        /// </summary>
        internal static string GameLocalRoot(string? gameId)
            => Sync.IntegrityChecker.GameLocalRoot(ConfigService.Current.GamesPath, gameId);

        /// <summary>Осталось ли от прерванного обновления полусобранное состояние игры (C2).</summary>
        internal static bool HasUnfinishedUpdate(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return false;
            }

            return Sync.SimpleSyncService.HasUpdateMarker(GameLocalRoot(gameId));
        }

        /// <summary>
        /// Есть ли в папке игры хотя бы один «полезный» файл (служебные `.staging/`, `.version`
        /// и маркер обновления не считаются). Реализация одна на весь клиент — в
        /// <see cref="Sync.IntegrityChecker.HasAnyLocalGameFiles"/>.
        /// </summary>
        internal static bool HasAnyLocalGameFiles(string localRoot)
            => Sync.IntegrityChecker.HasAnyLocalGameFiles(localRoot);

        /// <summary>Читает установленную версию из маркера. Пустая строка = игра не установлена.</summary>
        internal static string ReadLocalVersion(string? gameId) {
            try {
                if (string.IsNullOrWhiteSpace(gameId)) {
                    return string.Empty;
                }

                var marker = Path.Combine(GameLocalRoot(gameId), VersionMarkerFileName);
                if (File.Exists(marker)) {
                    var text = File.ReadAllText(marker).Trim();
                    Logging.Logger.Info($"ReadLocalVersion gid={gameId} value='{text}'");
                    return text;
                }
            }
            catch (Exception ex) {
                // Нечитаемый маркер трактуем как «не установлено» — это безопасный дефолт,
                // пользователь просто увидит кнопку «Установить».
                Logging.Logger.Warn($"ReadLocalVersion gid={gameId}: не удалось прочитать маркер версии: {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>Пишет маркер установленной версии. Возвращает false, если записать не удалось.</summary>
        internal static bool WriteLocalVersion(string? gameId, string? version) {
            try {
                if (string.IsNullOrWhiteSpace(gameId)) {
                    return false;
                }

                var root = GameLocalRoot(gameId);
                Directory.CreateDirectory(root);
                var marker = Path.Combine(root, VersionMarkerFileName);
                var toWrite = (version ?? string.Empty).Trim();
                File.WriteAllText(marker, toWrite);
                Logging.Logger.Info($"WriteLocalVersion gid={gameId} value='{toWrite}'");
                return true;
            }
            catch (Exception ex) {
                // Без маркера игра при следующем запуске будет считаться неустановленной —
                // это заметно пользователю, поэтому уровень Error.
                Logging.Logger.Error(ex, $"WriteLocalVersion gid={gameId}");
                return false;
            }
        }

        /// <summary>
        /// Читает версию установленного модпака. Пустая строка = модов нет.
        /// <para>
        /// Отдельный маркер рядом с <see cref="VersionMarkerFileName"/>, а не поле в нём:
        /// сборка игры и модпак обновляются независимо, и одна общая строка версии
        /// заставляла бы переустанавливать моды при каждом обновлении игры.
        /// </para>
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Версия модпака или пустая строка.</returns>
        internal static string ReadLocalModsVersion(string? gameId)
            => string.IsNullOrWhiteSpace(gameId) ? string.Empty : ReadModsVersionAt(GameLocalRoot(gameId));

        /// <summary>Пишет маркер версии модпака. Возвращает false, если записать не удалось.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="version">Версия модпака.</param>
        /// <returns>true, если маркер записан.</returns>
        internal static bool WriteLocalModsVersion(string? gameId, string? version)
            => !string.IsNullOrWhiteSpace(gameId) && WriteModsVersionAt(GameLocalRoot(gameId), version);

        /// <summary>
        /// То же по корню, а не по идентификатору игры.
        /// <para>
        /// Второй вход нужен потому, что модпак ставится не только в папку лаунчера:
        /// его кладут и в найденную Steam-копию, у которой никакого gameId в пути нет.
        /// Старые методы с gameId остаются как есть — их зовут отовсюду.
        /// </para>
        /// </summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <returns>Версия модпака или пустая строка.</returns>
        internal static string ReadModsVersionAt(string? localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot)) {
                    return string.Empty;
                }

                var marker = Path.Combine(localRoot, ModsVersionMarkerFileName);
                if (File.Exists(marker)) {
                    return File.ReadAllText(marker).Trim();
                }
            }
            catch (Exception ex) {
                // Нечитаемый маркер трактуем как «модов нет»: безопасный дефолт, при
                // котором лаунчер предложит поставить модпак заново.
                Logging.Logger.Warn($"ReadModsVersionAt('{localRoot}'): {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>Пишет маркер версии модпака в указанный корень.</summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <param name="version">Версия модпака.</param>
        /// <returns>true, если маркер записан.</returns>
        internal static bool WriteModsVersionAt(string? localRoot, string? version) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot)) {
                    return false;
                }

                Directory.CreateDirectory(localRoot);
                var toWrite = (version ?? string.Empty).Trim();
                Update.AtomicFile.WriteAllText(
                    Path.Combine(localRoot, ModsVersionMarkerFileName), toWrite, SelfUpdate.SelfUpdateRules.Utf8NoBom);
                Logging.Logger.Info($"WriteModsVersion root='{localRoot}' value='{toWrite}'");
                return true;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, $"WriteModsVersionAt({localRoot})");
                return false;
            }
        }

        /// <summary>
        /// Сохраняет копию установленного манифеста модпака рядом с маркером версии.
        /// <para>
        /// Это единственная память о том, какими путями в общей папке владеет модпак.
        /// Пишется атомарно и ПОСЛЕ успешной синхронизации: оборванная запись оставила бы
        /// список файлов, которых на диске нет, и следующее обновление модов сочло бы
        /// удалёнными файлы, которые никто не устанавливал.
        /// </para>
        /// </summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <param name="manifest">Установленный манифест модпака.</param>
        /// <returns>true, если копия записана.</returns>
        internal static bool WriteInstalledModPackManifest(string? localRoot, Manifest? manifest) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot) || manifest == null) {
                    return false;
                }

                Directory.CreateDirectory(localRoot);
                var json = JsonSerializer.Serialize(manifest, ModsManifestJson);
                Update.AtomicFile.WriteAllText(
                    Path.Combine(localRoot, ModsManifestFileName), json, SelfUpdate.SelfUpdateRules.Utf8NoBom);
                Logging.Logger.Info(
                    $"WriteInstalledModPackManifest root='{localRoot}' ver='{manifest.Version}' files={manifest.Files?.Count ?? 0}");
                return true;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, $"WriteInstalledModPackManifest({localRoot})");
                return false;
            }
        }

        /// <summary>Читает сохранённую копию манифеста модпака. null — модпак не установлен.</summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <returns>Манифест модпака или null.</returns>
        internal static Manifest? ReadInstalledModPackManifest(string? localRoot) {
            try {
                if (string.IsNullOrWhiteSpace(localRoot)) {
                    return null;
                }

                var path = Path.Combine(localRoot, ModsManifestFileName);
                if (!File.Exists(path)) {
                    return null;
                }

                return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), ModsManifestJson);
            }
            catch (Exception ex) {
                // Битую копию трактуем как «модпака нет». Для синхронизации ИГРЫ это
                // означает, что моды перестанут считаться чужими файлами, поэтому
                // уровень Error: тихо потерять этот файл нельзя.
                Logging.Logger.Error(ex, $"ReadInstalledModPackManifest({localRoot})");
                return null;
            }
        }

        /// <summary>
        /// Пути, которыми в этой папке владеет установленный модпак — в форме манифеста
        /// ('/', без ведущего разделителя). Пустой список, если модпака нет.
        /// <para>
        /// Именно этот список едет в <see cref="PlanOptions.ForeignPaths"/> синхронизации
        /// игры и в <see cref="PlanOptions.PreviousOwnedPaths"/> синхронизации модов.
        /// </para>
        /// </summary>
        /// <param name="localRoot">Корень папки игры.</param>
        /// <returns>Относительные пути файлов модпака.</returns>
        internal static IReadOnlyList<string> ReadInstalledModPackPaths(string? localRoot) {
            var manifest = ReadInstalledModPackManifest(localRoot);
            if (manifest?.Files == null || manifest.Files.Count == 0) {
                return Array.Empty<string>();
            }

            var list = new List<string>(manifest.Files.Count);
            foreach (var f in manifest.Files) {
                if (f == null || string.IsNullOrWhiteSpace(f.Path)) {
                    continue;
                }

                // Нормализуем ровно тем же кодом, что и планировщик: списки владения
                // сходятся только если "BepInEx\core\x.dll" и "BepInEx/core/x.dll" —
                // это одна и та же строка.
                var rel = SimpleSyncService.NormalizeRel(f.Path);
                if (rel.Length > 0) {
                    list.Add(rel);
                }
            }

            return list;
        }

        /// <summary>
        /// Свободное место на диске, где лежит папка игры. 0, если определить не удалось
        /// (сетевой путь, отсутствующий диск) — вызывающий код просто не покажет цифру.
        /// </summary>
        internal static long GetAvailableFreeSpaceFor(string? gameId) {
            try {
                var localRoot = GameLocalRoot(gameId);
                var root = Path.GetPathRoot(Path.GetFullPath(localRoot)) ?? localRoot;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"GetAvailableFreeSpaceFor gid={gameId}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Уводит создание ярлыка в подставной каталог на время теста.
        /// <para>
        /// Без этого шва проверить <see cref="TryCreateDesktopShortcut"/> нечем: он кладёт
        /// файл на НАСТОЯЩИЙ рабочий стол пользователя, и прогон тестов засорял бы его
        /// ярлыками несуществующих игр. Подменяемый ProgID нужен для второй ветки: на
        /// машине без WScript.Shell (урезанная Windows, политика на скриптовый хост)
        /// ярлык не создаётся, и установка обязана это пережить.
        /// </para>
        /// </summary>
        /// <param name="desktopDirectory">Каталог, играющий роль рабочего стола.</param>
        /// <param name="shellProgId">ProgID оболочки; несуществующий имитирует её отсутствие.</param>
        /// <param name="launcherPath">Путь к exe лаунчера, на который ссылается ярлык.</param>
        /// <returns>Объект, возвращающий настоящее окружение.</returns>
        internal static IDisposable OverrideShortcutEnvironmentForTests(
            string desktopDirectory, string? shellProgId = null, string? launcherPath = null)
            => new ShortcutEnvironmentOverride(desktopDirectory, shellProgId ?? ShellProgId, launcherPath);

        /// <summary>
        /// Полный путь к exe игры внутри её папки. Пустая строка — путь к exe не задан
        /// (в карточке игры его может не быть) и запускать нечего.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="exeRelativePath">Путь к exe относительно папки игры.</param>
        /// <returns>Полный путь к exe или пустая строка.</returns>
        internal static string GameExePath(string? gameId, string? exeRelativePath) {
            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(exeRelativePath)) {
                return string.Empty;
            }

            // Разделитель в карточке приходит с сервера любой: и '/', и '\\'. Ведущий
            // разделитель снимаем: Path.Combine считает такой путь абсолютным и выкинул бы
            // папку игры целиком, уведя ярлык в корень диска.
            var rel = exeRelativePath.Replace('/', Path.DirectorySeparatorChar)
                                     .Replace('\\', Path.DirectorySeparatorChar)
                                     .TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(GameLocalRoot(gameId), rel);
        }

        /// <summary>
        /// Заводит создание ярлыка установленной игры в отдельном потоке.
        /// <para>
        /// Оболочка Windows — COM, и создание ярлыка требует STA-потока: из потока пула
        /// (а установка заканчивается именно там) вызов падал бы. Поток фоновый: ярлык
        /// не должен держать закрытие лаунчера.
        /// </para>
        /// </summary>
        /// <param name="title">Название игры для имени ярлыка.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="exeRelativePath">Путь к exe относительно папки игры.</param>
        internal static void StartDesktopShortcutCreation(string? title, string? gameId, string? exeRelativePath) {
            try {
                var exePath = GameExePath(gameId, exeRelativePath);
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) {
                    return;
                }

                var name = string.IsNullOrWhiteSpace(title) ? gameId! : title!;
                var thread = new Thread(() => TryCreateDesktopShortcut(name, gameId!, exePath)) { IsBackground = true };
                try {
                    thread.SetApartmentState(ApartmentState.STA);
                }
                catch (Exception ex) {
                    // Состояние потока занять не удалось — пробуем создать ярлык как есть:
                    // хуже, чем сейчас, уже не будет, а ошибку внутри гасит сам вызов.
                    Logging.Logger.Warn($"StartDesktopShortcutCreation: STA не выставлен: {ex.Message}");
                }

                thread.Start();
            }
            catch (Exception ex) {
                // Ярлык — приятная мелочь в конце установки, а не её часть: игра уже установлена.
                Logging.Logger.Warn($"StartDesktopShortcutCreation('{title}'): {ex.Message}");
            }
        }

        /// <summary>
        /// Создаёт ярлык игры на рабочем столе. Ошибки не критичны для сценария установки.
        /// <para>
        /// ЯРЛЫК ВЕДЁТ В ЛАУНЧЕР, А НЕ В ИГРУ: цель — ChillHub.exe с аргументами этой игры
        /// (см. <see cref="Shell.ShortcutTarget"/>), и лаунчер открывает главную с выделенной
        /// игрой. Прямой запуск exe обходил и вышедшее обновление, и модпак, и проверку
        /// целостности — человек попадал в игру старой версии, ничего об этом не узнав.
        /// </para>
        /// <para>
        /// Значок берётся у exe игры: ярлык обязан выглядеть как игра, а не как ещё одна
        /// копия лаунчера — их на рабочем столе может оказаться десяток.
        /// </para>
        /// <para>
        /// Путь к лаунчеру определяется не всегда (запуск из-под отладчика, необычная
        /// установка) — тогда ярлык всё равно создаётся, но по-старому, прямо на exe игры:
        /// работающий ярлык мимо лаунчера лучше, чем ярлык в никуда.
        /// </para>
        /// </summary>
        /// <param name="title">Название игры для имени ярлыка.</param>
        /// <param name="gameId">Идентификатор игры — по нему лаунчер её и выделит.</param>
        /// <param name="exePath">Полный путь к exe игры.</param>
        internal static void TryCreateDesktopShortcut(string title, string gameId, string exePath) {
            try {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) {
                    return;
                }

                var env = ScopedShortcutEnv.Value;
                var desktop = env?.DesktopDirectory
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var name = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(exePath) : title;
                var linkPath = Path.Combine(desktop, HomeFormat.SanitizeFileName(name) + ".lnk");

                var shellType = Type.GetTypeFromProgID(env?.ShellProgId ?? ShellProgId);
                if (shellType == null) {
                    Logging.Logger.Warn("TryCreateDesktopShortcut: WScript.Shell недоступен, ярлык не создан");
                    return;
                }

                var launcher = env?.LauncherPath ?? LauncherPath();
                var arguments = string.IsNullOrWhiteSpace(launcher)
                    ? string.Empty
                    : Shell.ShortcutTarget.BuildArguments(gameId, name, exePath);
                var target = string.IsNullOrWhiteSpace(arguments) ? exePath : launcher;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(linkPath);
                shortcut.TargetPath = target;
                shortcut.Arguments = arguments;
                shortcut.WorkingDirectory = Path.GetDirectoryName(target);
                shortcut.Description = name;
                shortcut.IconLocation = exePath + ",0";
                shortcut.Save();
            }
            catch (Exception ex) {
                // Ярлык — приятная мелочь, а не часть установки: молча не падаем, но пишем в лог.
                Logging.Logger.Warn($"TryCreateDesktopShortcut('{title}'): {ex.Message}");
            }
        }

        /// <summary>
        /// Путь к exe лаунчера, на который ссылается ярлык. Пустая строка — путь определить
        /// не удалось, и ярлык придётся вести прямо на игру.
        /// </summary>
        /// <returns>Полный путь к ChillHub.exe или пустая строка.</returns>
        private static string LauncherPath() {
            try {
                // ProcessPath — это ровно тот файл, которым запущен текущий процесс. Под
                // отладчиком и в прогоне тестов это не лаунчер, поэтому имя проверяется:
                // ярлыка, ведущего в testhost.exe, не должно существовать даже теоретически.
                var self = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(self)
                    && string.Equals(Path.GetFileName(self), LauncherFileName, StringComparison.OrdinalIgnoreCase)) {
                    return self;
                }

                var beside = Path.Combine(AppContext.BaseDirectory, LauncherFileName);
                return File.Exists(beside) ? beside : string.Empty;
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"GameLocalState.LauncherPath: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Убирает с рабочего стола ярлыки удалённой игры.
        /// <para>
        /// Ярлык переживал удаление игры: пользователь сносил файлы, а на рабочем столе
        /// оставалась иконка, которая по клику ругалась «не найден элемент». Поэтому
        /// удаление игры уносит и её ярлыки.
        /// </para>
        /// <para>
        /// Ярлык опознаётся по пути к игре внутри файла, а не по названию: название игры на
        /// сервере могло поменяться после установки, а на рабочем столе у пользователя вполне
        /// может лежать чужой ярлык с таким же именем. Путь лежит в аргументах ярлыка (цель
        /// у него — лаунчер) и читается из самого файла `.lnk`,
        /// без обращения к оболочке Windows: удаление идёт в фоновом потоке, где COM
        /// недоступен так же свободно, как при установке, а не опознанный ярлык мы
        /// оставляем на месте — лишний ярлык лучше стёртого чужого.
        /// </para>
        /// </summary>
        /// <param name="localRoot">Корень папки удаляемой игры.</param>
        /// <returns>Сколько ярлыков удалено.</returns>
        internal static int TryRemoveDesktopShortcuts(string localRoot) {
            var removed = 0;
            try {
                if (string.IsNullOrWhiteSpace(localRoot)) {
                    return 0;
                }

                var desktop = ScopedShortcutEnv.Value?.DesktopDirectory
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)) {
                    return 0;
                }

                var target = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var link in Directory.EnumerateFiles(desktop, "*.lnk", SearchOption.TopDirectoryOnly)) {
                    try {
                        if (!PointsInto(link, target)) {
                            continue;
                        }

                        File.Delete(link);
                        removed++;
                        Logging.Logger.Info($"TryRemoveDesktopShortcuts: удалён ярлык '{Path.GetFileName(link)}'");
                    }
                    catch (Exception ex) {
                        // Занятый или защищённый ярлык не повод обрывать проход по остальным.
                        Logging.Logger.Warn($"TryRemoveDesktopShortcuts('{Path.GetFileName(link)}'): {ex.Message}");
                    }
                }
            }
            catch (Exception ex) {
                // Ярлык — приятная мелочь, а не часть удаления: файлы игры уже снесены.
                Logging.Logger.Warn($"TryRemoveDesktopShortcuts('{localRoot}'): {ex.Message}");
            }

            return removed;
        }

        /// <summary>
        /// Ведёт ли ярлык внутрь папки игры.
        /// <para>
        /// Путь к цели лежит в `.lnk` открытым текстом — и в однобайтовой кодировке
        /// (блок LinkInfo), и в UTF-16 (строки ярлыка), в зависимости от того, чем он
        /// создан. Ищем обе записи: достаточно одной, чтобы ярлык был опознан.
        /// </para>
        /// </summary>
        /// <param name="linkPath">Путь к файлу ярлыка.</param>
        /// <param name="gameRoot">Полный путь к папке игры без хвостового разделителя.</param>
        /// <returns>true, если ярлык указывает внутрь папки игры.</returns>
        private static bool PointsInto(string linkPath, string gameRoot) {
            var info = new FileInfo(linkPath);
            if (info.Length > MaxLinkBytes) {
                return false;
            }

            var bytes = File.ReadAllBytes(linkPath);
            var needle = gameRoot + Path.DirectorySeparatorChar;
            return Contains(bytes, Encoding.Unicode.GetBytes(needle))
                || Contains(bytes, Encoding.UTF8.GetBytes(needle));
        }

        /// <summary>Ищет последовательность байт в буфере без учёта регистра ASCII.</summary>
        /// <param name="haystack">Где ищем.</param>
        /// <param name="needle">Что ищем.</param>
        /// <returns>true, если последовательность найдена.</returns>
        private static bool Contains(byte[] haystack, byte[] needle) {
            if (needle.Length == 0 || haystack.Length < needle.Length) {
                return false;
            }

            for (var i = 0; i <= haystack.Length - needle.Length; i++) {
                var match = true;
                for (var j = 0; j < needle.Length; j++) {
                    if (ToLowerAscii(haystack[i + j]) != ToLowerAscii(needle[j])) {
                        match = false;
                        break;
                    }
                }

                if (match) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Приводит букву диска к нижнему регистру: `C:\` и `c:\` — один и тот же путь.</summary>
        /// <param name="b">Байт.</param>
        /// <returns>Байт в нижнем регистре, если это латинская буква.</returns>
        private static byte ToLowerAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

        /// <summary>Куда кладётся ярлык, через какую оболочку он создаётся и куда ведёт.</summary>
        /// <param name="DesktopDirectory">Каталог рабочего стола.</param>
        /// <param name="ShellProgId">ProgID оболочки Windows.</param>
        /// <param name="LauncherPath">Путь к exe лаунчера; null — определять самим.</param>
        private sealed record ShortcutEnvironment(string DesktopDirectory, string ShellProgId, string? LauncherPath);

        /// <summary>Возвращает настоящее окружение ярлыка после <see cref="OverrideShortcutEnvironmentForTests"/>.</summary>
        private sealed class ShortcutEnvironmentOverride : IDisposable {
            private readonly ShortcutEnvironment? previous;

            internal ShortcutEnvironmentOverride(string desktopDirectory, string shellProgId, string? launcherPath) {
                this.previous = ScopedShortcutEnv.Value;
                ScopedShortcutEnv.Value = new ShortcutEnvironment(desktopDirectory, shellProgId, launcherPath);
            }

            public void Dispose() => ScopedShortcutEnv.Value = this.previous;
        }
    }
}
