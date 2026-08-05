// <copyright file="GameLocalState.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// Локальное состояние установленной игры на диске: путь к папке, маркер версии `.version`,
    /// маркер незавершённого обновления, наличие полезных файлов, ярлык на рабочем столе.
    /// Никакого UI — только файловая система.
    /// </summary>
    internal static class GameLocalState {
        /// <summary>Имя файла-маркера с установленной версией.</summary>
        internal const string VersionMarkerFileName = Sync.IntegrityChecker.VersionMarkerFileName;

        /// <summary>ProgID оболочки Windows, через которую создаётся файл ярлыка.</summary>
        private const string ShellProgId = "WScript.Shell";

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
        /// <returns>Объект, возвращающий настоящее окружение.</returns>
        internal static IDisposable OverrideShortcutEnvironmentForTests(
            string desktopDirectory, string? shellProgId = null)
            => new ShortcutEnvironmentOverride(desktopDirectory, shellProgId ?? ShellProgId);

        /// <summary>Создаёт ярлык игры на рабочем столе. Ошибки не критичны для сценария установки.</summary>
        internal static void TryCreateDesktopShortcut(string title, string exePath) {
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

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(linkPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = name;
                shortcut.IconLocation = exePath + ",0";
                shortcut.Save();
            }
            catch (Exception ex) {
                // Ярлык — приятная мелочь, а не часть установки: молча не падаем, но пишем в лог.
                Logging.Logger.Warn($"TryCreateDesktopShortcut('{title}'): {ex.Message}");
            }
        }

        /// <summary>Куда кладётся ярлык и через какую оболочку он создаётся.</summary>
        /// <param name="DesktopDirectory">Каталог рабочего стола.</param>
        /// <param name="ShellProgId">ProgID оболочки Windows.</param>
        private sealed record ShortcutEnvironment(string DesktopDirectory, string ShellProgId);

        /// <summary>Возвращает настоящее окружение ярлыка после <see cref="OverrideShortcutEnvironmentForTests"/>.</summary>
        private sealed class ShortcutEnvironmentOverride : IDisposable {
            private readonly ShortcutEnvironment? previous;

            internal ShortcutEnvironmentOverride(string desktopDirectory, string shellProgId) {
                this.previous = ScopedShortcutEnv.Value;
                ScopedShortcutEnv.Value = new ShortcutEnvironment(desktopDirectory, shellProgId);
            }

            public void Dispose() => ScopedShortcutEnv.Value = this.previous;
        }
    }
}
