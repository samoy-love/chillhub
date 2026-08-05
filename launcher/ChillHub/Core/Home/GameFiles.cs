// <copyright file="GameFiles.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>
    /// Удаление локальных файлов игры и рассказ о том, что удалить не удалось.
    /// Только файловая система, никакого UI.
    /// </summary>
    internal static class GameFiles {
        /// <summary>Сколько занятых файлов называем пользователю поимённо.</summary>
        private const int NamedBlockedFiles = 3;

        /// <summary>
        /// Удаляет содержимое папки игры, доводя проход до конца.
        /// <para>
        /// <see cref="Directory.Delete(string, bool)"/> обрывается на ПЕРВОМ занятом файле,
        /// когда остальное уже удалено. Пользователь видел «не удалось удалить», игра
        /// оставалась помеченной установленной, а на диске лежали её остатки, неспособные
        /// запуститься. Здесь занятый файл не прерывает работу: он попадает в список,
        /// который вызывающий код показывает пользователю.
        /// </para>
        /// </summary>
        /// <param name="root">Корень папки игры.</param>
        /// <returns>Файлы, которые удалить не удалось (пустой список — всё снесено).</returns>
        internal static List<string> DeleteGameFiles(string root) {
            var blocked = new List<string>();
            if (!Directory.Exists(root)) {
                return blocked;
            }

            // Списки материализуем заранее: удалять во время ленивого обхода нельзя.
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            foreach (var file in files) {
                try {
                    var info = new FileInfo(file);
                    if (info.IsReadOnly) {
                        info.IsReadOnly = false;
                    }

                    File.Delete(file);
                }
                catch (Exception ex) {
                    blocked.Add(file);
                    Logging.Logger.Warn($"DeleteGameFiles: файл занят и не удалён: '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            // Каталоги — от самых глубоких к корню. Непустые (из-за занятых файлов)
            // просто не удалятся, и это ожидаемо.
            var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                .OrderByDescending(d => d.Length).ToList();
            foreach (var dir in dirs) {
                try {
                    Directory.Delete(dir, false);
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"DeleteGameFiles: каталог не удалён: '{dir}': {ex.Message}");
                }
            }

            if (blocked.Count == 0) {
                try {
                    Directory.Delete(root, true);
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"DeleteGameFiles: корень игры не удалён: {ex.Message}");
                }
            }

            return blocked;
        }

        /// <summary>
        /// Сообщение о частичном удалении. Занятые файлы называем поимённо (первые три):
        /// без имён пользователю нечего закрывать, а игра до этого работать не будет.
        /// </summary>
        /// <param name="blocked">Файлы, которые удалить не удалось.</param>
        /// <returns>Текст для показа пользователю.</returns>
        internal static string BuildBlockedFilesMessage(IReadOnlyList<string> blocked) {
            var names = string.Join(", ", blocked.Take(NamedBlockedFiles).Select(Path.GetFileName));
            var tail = blocked.Count > NamedBlockedFiles ? $" и ещё {blocked.Count - NamedBlockedFiles}" : string.Empty;
            return $"Файлы игры удалены частично: {blocked.Count} шт. заняты другой программой ({names}{tail}). "
                + "Закройте игру, лаунчеры модов и антивирусную проверку, затем удалите ещё раз. "
                + "До этого игра работать не будет.";
        }
    }
}
