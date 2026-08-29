// <copyright file="ExceptionText.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.IO;

    /// <summary>
    /// Как назвать исключение, у которого нечего сказать.
    /// <para>
    /// У <see cref="FileNotFoundException"/> по СБОРКЕ свойство Message пусто. Всюду,
    /// где отказ собирался строкой «что-то: {ex.Message}», в журнал и в обращение
    /// уезжало двоеточие с пустотой за ним — «Ошибка загрузки Accessibility.dll: » и
    /// «reason=io_error ». Такая строка не называет ни причины, ни хотя бы места, где
    /// смотреть.
    /// </para>
    /// <para>
    /// Название типа — не подарок, но оно хотя бы называет случившееся, а для
    /// пропавшей сборки к нему добавляется её имя: по нему сразу видно, что не хватает
    /// файлов самого лаунчера, а не файлов игры.
    /// </para>
    /// </summary>
    internal static class ExceptionText {
        /// <summary>Непустое описание исключения.</summary>
        /// <param name="ex">Исключение; null — пустая строка.</param>
        /// <returns>Текст для журнала и для игрока.</returns>
        internal static string Describe(Exception? ex) {
            if (ex == null) {
                return string.Empty;
            }

            var message = (ex.Message ?? string.Empty).Trim();
            if (message.Length > 0) {
                return message;
            }

            return ex is FileNotFoundException { FileName.Length: > 0 } f
                ? $"{ex.GetType().Name}: {f.FileName}"
                : ex.GetType().Name;
        }
    }
}
