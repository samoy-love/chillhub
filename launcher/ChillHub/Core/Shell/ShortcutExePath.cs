// <copyright file="ShortcutExePath.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.IO;

    /// <summary>
    /// Что лаунчеру позволено запустить по пути из ярлыка.
    /// <para>
    /// ПУТЬ ПРИХОДИТ ИЗ КОМАНДНОЙ СТРОКИ, И ЭТО НЕ НАШ ПУТЬ. Ключ <c>--exe</c> пишет
    /// ярлык, который создаёт лаунчер, но ярлык — обычный файл на рабочем столе, а
    /// аргументы лаунчеру может передать кто угодно. Пока путь не проверялся, Chill Hub
    /// работал переходником «запусти вот этот exe»: человек видел окно от знакомого
    /// лаунчера, нажимал одну кнопку — и стартовало что угодно с любого диска.
    /// </para>
    /// <para>
    /// Правило одно: запасной запуск ведёт только внутрь папки игр. Больше ему и некуда —
    /// свои ярлыки лаунчер делает на exe внутри неё (см. <see cref="Home.GameLocalState"/>).
    /// Папку игр можно переносить, и ярлык, сделанный до переноса, под правило не
    /// подойдёт; это честно: файлов по старому пути уже нет, а окно в таком случае и
    /// говорит, что запускать нечем.
    /// </para>
    /// </summary>
    internal static class ShortcutExePath {
        /// <summary>
        /// Gets or sets корень папки игр. Шов: настоящий читает конфиг, и без подмены
        /// проверка зависела бы от того, куда игрок положил игры на этой машине.
        /// </summary>
        internal static Func<string> GamesRoot { get; set; } = DefaultGamesRoot;

        /// <summary>Возвращает чтение настоящего конфига.</summary>
        internal static void ResetForTests() => GamesRoot = DefaultGamesRoot;

        /// <summary>
        /// Можно ли запускать этот путь.
        /// </summary>
        /// <param name="exePath">Путь из ярлыка.</param>
        /// <returns>true, если путь ведёт на exe внутри папки игр.</returns>
        internal static bool IsAllowed(string? exePath) => IsAllowed(exePath, GamesRoot());

        /// <summary>
        /// То же с явно заданным корнем — для тестов и для вызывающих, которые корень
        /// уже знают.
        /// </summary>
        /// <param name="exePath">Путь из ярлыка.</param>
        /// <param name="gamesRoot">Корень папки игр.</param>
        /// <returns>true, если путь ведёт на exe внутри папки игр.</returns>
        internal static bool IsAllowed(string? exePath, string? gamesRoot) {
            if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(gamesRoot)) {
                return false;
            }

            try {
                var full = Path.GetFullPath(exePath);

                // Только exe. Ярлык на .bat или .cmd внутри папки игр — это уже не
                // «запустить установленную копию», а выполнение чужого сценария.
                if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }

                // GetFullPath разворачивает и «..», поэтому путь вида
                // «D:\Games\ChillHub\..\..\Windows\System32\cmd.exe» до сравнения уже
                // перестаёт выглядеть лежащим внутри папки игр.
                var root = Path.GetFullPath(gamesRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var prefix = root + Path.DirectorySeparatorChar;
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) {
                // Путь, который не разворачивается, запускать тем более незачем.
                Logging.Logger.Warn($"ShortcutExePath.IsAllowed('{exePath}'): {ex.Message}");
                return false;
            }
        }

        private static string DefaultGamesRoot() => ConfigService.Current.GamesPath ?? string.Empty;
    }
}
