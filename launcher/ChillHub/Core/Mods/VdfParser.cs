// <copyright file="VdfParser.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Разбор текстового формата Valve (VDF) — на нём Steam хранит и список библиотек
    /// (<c>steamapps/libraryfolders.vdf</c>), и описание каждой установленной игры
    /// (<c>appmanifest_&lt;appid&gt;.acf</c>).
    /// <para>
    /// Свой парсер, а не зависимость: формат — это вложенные пары «ключ» «значение» в
    /// кавычках, читается тридцатью строками, а тесты на две реальные раскладки
    /// (обычная папка и вложенная, как у How to Fish) нужны в любом случае. Тянуть
    /// ради этого пакет в self-contained сборку смысла нет.
    /// </para>
    /// </summary>
    internal static class VdfParser {
        /// <summary>Потолок вложенности. Настоящие файлы не глубже четырёх уровней.</summary>
        private const int MaxDepth = 32;

        /// <summary>
        /// Узел VDF: либо значение-строка, либо словарь дочерних узлов.
        /// <para>
        /// Ключи сравниваются без учёта регистра: Steam пишет то <c>"path"</c>, то
        /// <c>"Path"</c>, а раньше — <c>"LibraryFolders"</c> против <c>"libraryfolders"</c>.
        /// </para>
        /// </summary>
        internal sealed class VdfNode {
            /// <summary>Значение, если узел — лист.</summary>
            internal string? Value { get; set; }

            /// <summary>Дочерние узлы, если узел — словарь.</summary>
            internal Dictionary<string, VdfNode> Children { get; } =
                new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Возвращает дочерний узел или null.</summary>
            /// <param name="key">Имя ключа.</param>
            /// <returns>Узел или null.</returns>
            internal VdfNode? Child(string key)
                => this.Children.TryGetValue(key, out var node) ? node : null;

            /// <summary>Возвращает строковое значение дочернего ключа или пустую строку.</summary>
            /// <param name="key">Имя ключа.</param>
            /// <returns>Значение или пустая строка.</returns>
            internal string String(string key)
                => this.Child(key)?.Value ?? string.Empty;
        }

        /// <summary>
        /// Разбирает документ VDF. Никогда не бросает: повреждённый файл даёт то, что
        /// удалось прочитать до места поломки.
        /// <para>
        /// Это осознанно. Единственный потребитель — поиск папки игры, и «нашли три
        /// библиотеки из четырёх» здесь полезнее, чем исключение: четвёртая, скорее
        /// всего, и не та, что нужна.
        /// </para>
        /// </summary>
        /// <param name="text">Содержимое файла.</param>
        /// <returns>Корневой узел.</returns>
        internal static VdfNode Parse(string? text) {
            var root = new VdfNode();
            if (string.IsNullOrEmpty(text)) {
                return root;
            }

            var stack = new Stack<VdfNode>();
            stack.Push(root);
            string? pendingKey = null;
            var i = 0;

            while (i < text.Length) {
                var c = text[i];

                if (char.IsWhiteSpace(c)) {
                    i++;
                    continue;
                }

                // Комментарий до конца строки: Steam пишет их в config.vdf.
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') {
                    while (i < text.Length && text[i] != '\n') {
                        i++;
                    }

                    continue;
                }

                if (c == '{') {
                    i++;
                    if (pendingKey == null || stack.Count >= MaxDepth) {
                        // Блок без ключа (или слишком глубокий) — читаем и выбрасываем,
                        // чтобы не сбить дальнейший разбор.
                        SkipBlock(text, ref i);
                        pendingKey = null;
                        continue;
                    }

                    var child = new VdfNode();
                    stack.Peek().Children[pendingKey] = child;
                    stack.Push(child);
                    pendingKey = null;
                    continue;
                }

                if (c == '}') {
                    i++;
                    if (stack.Count > 1) {
                        stack.Pop();
                    }

                    pendingKey = null;
                    continue;
                }

                var token = ReadToken(text, ref i);
                if (token == null) {
                    break;
                }

                if (pendingKey == null) {
                    pendingKey = token;
                }
                else {
                    stack.Peek().Children[pendingKey] = new VdfNode { Value = token };
                    pendingKey = null;
                }
            }

            return root;
        }

        /// <summary>
        /// Читает один токен: строку в кавычках либо слово без кавычек.
        /// Внутри кавычек понимает экранирование обратным слешем — в путях Windows
        /// Steam пишет <c>C:\\Games</c>, и без разэкранирования путь превращается в мусор.
        /// </summary>
        /// <param name="text">Документ.</param>
        /// <param name="i">Позиция; сдвигается за конец токена.</param>
        /// <returns>Токен или null, если дошли до конца.</returns>
        private static string? ReadToken(string text, ref int i) {
            if (i >= text.Length) {
                return null;
            }

            if (text[i] == '"') {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"') {
                    if (text[i] == '\\' && i + 1 < text.Length) {
                        i++;
                        sb.Append(text[i] switch {
                            'n' => '\n',
                            't' => '\t',
                            _ => text[i],
                        });
                    }
                    else {
                        sb.Append(text[i]);
                    }

                    i++;
                }

                if (i < text.Length) {
                    i++; // закрывающая кавычка
                }

                return sb.ToString();
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') {
                i++;
            }

            return i > start ? text[start..i] : null;
        }

        /// <summary>Пропускает блок в фигурных скобках вместе с вложенными.</summary>
        /// <param name="text">Документ.</param>
        /// <param name="i">Позиция сразу после открывающей скобки.</param>
        private static void SkipBlock(string text, ref int i) {
            var depth = 1;
            while (i < text.Length && depth > 0) {
                if (text[i] == '"') {
                    ReadToken(text, ref i);
                    continue;
                }

                if (text[i] == '{') {
                    depth++;
                }
                else if (text[i] == '}') {
                    depth--;
                }

                i++;
            }
        }
    }
}
