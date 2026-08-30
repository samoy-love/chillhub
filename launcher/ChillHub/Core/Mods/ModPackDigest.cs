// <copyright file="ModPackDigest.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using ChillHub.Core.Sync;

    /// <summary>
    /// Отпечаток СОДЕРЖИМОГО модпака — ответ на вопрос «то же самое дерево или другое».
    /// <para>
    /// ИМЕНИ ВЕРСИИ ДЛЯ ЭТОГО НЕ ХВАТАЕТ. Версия модпака — это имя пакета на
    /// Thunderstore («Автор-Пак-9.5.0»), а не номер нашей сборки. Админка умеет
    /// пересобрать тот же пакет изменившимся конвейером, и тогда под ТЕМ ЖЕ именем
    /// на сервере лежит ДРУГОЕ дерево. Лаунчер сравнивал только имена — и починенная
    /// раскладка так и осталась бы на сервере, а у игрока лежала бы прежняя.
    /// </para>
    /// <para>
    /// СЧИТАЕТСЯ ТАК ЖЕ, КАК НА СЕРВЕРЕ, и это не совпадение, а условие работы:
    /// сервер кладёт свой отпечаток в <c>/api/games</c>, лаунчер считает свой по
    /// установленному манифесту, и сравниваются они напрямую. Порядок — по байтам
    /// пути в UTF-8, ровно как сортирует Go; время сборки в отпечаток не входит,
    /// иначе любая пересборка звала бы обновляться впустую.
    /// </para>
    /// </summary>
    internal static class ModPackDigest {
        /// <summary>
        /// Считает отпечаток дерева по манифесту.
        /// </summary>
        /// <param name="manifest">Манифест модпака; null — отпечатка нет.</param>
        /// <returns>32 шестнадцатеричных знака или пустая строка.</returns>
        internal static string Of(Manifest? manifest) {
            if (manifest?.Files == null || manifest.Files.Count == 0) {
                return string.Empty;
            }

            var lines = new List<byte[]>(manifest.Files.Count);
            foreach (var f in manifest.Files) {
                lines.Add(Encoding.UTF8.GetBytes(
                    (f.Path ?? string.Empty) + "\n" + (f.Blake3 ?? string.Empty) + "\n"));
            }

            var keys = new List<byte[]>(manifest.Files.Count);
            foreach (var f in manifest.Files) {
                keys.Add(Encoding.UTF8.GetBytes(f.Path ?? string.Empty));
            }

            var order = new int[manifest.Files.Count];
            for (var i = 0; i < order.Length; i++) {
                order[i] = i;
            }

            Array.Sort(order, (a, b) => CompareBytes(keys[a], keys[b]));

            using var sha = SHA256.Create();
            foreach (var i in order) {
                sha.TransformBlock(lines[i], 0, lines[i].Length, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = sha.Hash ?? Array.Empty<byte>();

            // Половина хеша: это метка «то же или другое», а не защита от подбора.
            var sb = new StringBuilder(32);
            for (var i = 0; i < 16 && i < hash.Length; i++) {
                sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        /// <summary>Побайтовое сравнение, как сортирует строки Go.</summary>
        /// <param name="a">Первая последовательность.</param>
        /// <param name="b">Вторая последовательность.</param>
        /// <returns>Отрицательное, ноль или положительное.</returns>
        private static int CompareBytes(byte[] a, byte[] b) {
            var n = Math.Min(a.Length, b.Length);
            for (var i = 0; i < n; i++) {
                if (a[i] != b[i]) {
                    return a[i] < b[i] ? -1 : 1;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
