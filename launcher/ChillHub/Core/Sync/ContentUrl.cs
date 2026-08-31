// <copyright file="ContentUrl.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;

    /// <summary>
    /// Сборка адреса файла из базового адреса раздачи и пути внутри манифеста.
    /// </summary>
    internal static class ContentUrl {
        /// <summary>
        /// Адрес файла: база плюс путь манифеста, где каждый сегмент закодирован.
        /// <para>
        /// Путь подставлялся в адрес как есть, и имя файла с '#' обрывало запрос: всё
        /// после решётки — это фрагмент, на провод он не уходит, поэтому сервер получал
        /// запрос на «Mod» вместо «Mod#2.dll», отвечал 404, а три попытки подряд роняли
        /// весь план. Знак '%' давал вторую разновидность той же беды: он разбирался как
        /// начало escape-последовательности, и запрос уезжал по другому пути. Обе стороны
        /// такие имена в манифест пропускают, значит собирать адрес нужно так, чтобы
        /// любое допустимое в файловой системе имя доехало до сервера целиком.
        /// </para>
        /// </summary>
        /// <param name="baseUrl">База раздачи, с завершающим слешем или без.</param>
        /// <param name="relativePath">Путь файла внутри сборки.</param>
        /// <returns>Готовый адрес для запроса.</returns>
        internal static string Combine(string baseUrl, string relativePath) {
            var prefix = (baseUrl ?? string.Empty).TrimEnd('/') + "/";
            var rel = (relativePath ?? string.Empty).Replace("\\", "/");

            // Кодируем посегментно: разделители путей обязаны остаться разделителями,
            // иначе сервер увидит одно длинное имя файла вместо дерева каталогов.
            var segments = rel.Split('/');
            for (var i = 0; i < segments.Length; i++) {
                segments[i] = Uri.EscapeDataString(segments[i]);
            }

            return prefix + string.Join("/", segments);
        }
    }
}
