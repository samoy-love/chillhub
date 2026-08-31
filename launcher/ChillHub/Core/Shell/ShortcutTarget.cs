// <copyright file="ShortcutTarget.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Что просит открыть ярлык с рабочего стола.
    /// </summary>
    /// <param name="GameId">Идентификатор игры — по нему она ищется в каталоге.</param>
    /// <param name="Title">Название игры на момент создания ярлыка: нужно для текста окна,
    /// когда игры в каталоге уже нет и назвать её больше нечем.</param>
    /// <param name="ExePath">Путь к исполняемому файлу игры на момент создания ярлыка:
    /// запасной путь запуска, когда игры в каталоге нет.</param>
    internal sealed record ShortcutRequest(string GameId, string Title, string ExePath);

    /// <summary>
    /// Командная строка ярлыка игры: как она собирается при установке и как разбирается
    /// при запуске.
    /// <para>
    /// Ярлык ведёт не в игру, а в лаунчер — на главную, с выделенной в списке игрой.
    /// Игра из ярлыка обычно требует внимания: у неё вышло обновление, к ней есть модпак,
    /// её файлы могли разъехаться. Прямой запуск exe всё это обходил молча, и человек
    /// попадал в игру старой версии, ничего об этом не узнав. Главная — потому что запуск,
    /// обновление и моды живут там же, где список: ярлык приводит туда, откуда играют.
    /// </para>
    /// <para>
    /// Путь к exe и название кладутся в ту же строку не для запуска, а на случай, когда
    /// игры в каталоге больше нет (снята с публикации, сервер недоступен): без них лаунчеру
    /// нечего было бы ни показать, ни предложить, и ярлык превращался бы в кнопку
    /// «ничего не произошло».
    /// </para>
    /// </summary>
    internal static class ShortcutTarget {
        /// <summary>Ключ идентификатора игры.</summary>
        internal const string GameOption = "--game";

        /// <summary>Ключ названия игры.</summary>
        internal const string TitleOption = "--title";

        /// <summary>Ключ пути к исполняемому файлу игры.</summary>
        internal const string ExeOption = "--exe";

        /// <summary>
        /// Собирает строку аргументов ярлыка.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="title">Название игры; пустое допустимо.</param>
        /// <param name="exePath">Полный путь к exe игры; пустой допустим.</param>
        /// <returns>Строка аргументов или пустая строка, если игра не задана.</returns>
        internal static string BuildArguments(string? gameId, string? title, string? exePath) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return string.Empty;
            }

            var sb = new StringBuilder();
            Append(sb, GameOption, gameId);
            Append(sb, TitleOption, title);
            Append(sb, ExeOption, exePath);
            return sb.ToString();
        }

        /// <summary>
        /// Разбирает аргументы запуска лаунчера.
        /// <para>
        /// Всё незнакомое молча пропускается: лаунчер запускают и установщик, и апдейтер,
        /// и сам пользователь из консоли — падать на чужом ключе ярлыку незачем.
        /// </para>
        /// </summary>
        /// <param name="args">Аргументы командной строки без имени программы.</param>
        /// <returns>Запрос ярлыка либо null, если игра в аргументах не названа.</returns>
        internal static ShortcutRequest? Parse(IReadOnlyList<string>? args) {
            if (args == null || args.Count == 0) {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Count; i++) {
                var arg = args[i]?.Trim() ?? string.Empty;
                if (!IsOption(arg)) {
                    continue;
                }

                // Значение может быть и отдельным аргументом (--game gid), и приклеенным
                // через '=' (--game=gid): ярлык пишет первое, руками из консоли пишут оба.
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0) {
                    values[arg[..eq]] = Unquote(arg[(eq + 1)..]);
                    continue;
                }

                if (i + 1 < args.Count && !IsOption(args[i + 1] ?? string.Empty)) {
                    values[arg] = Unquote(args[++i] ?? string.Empty);
                }
            }

            if (!values.TryGetValue(GameOption, out var id) || string.IsNullOrWhiteSpace(id)) {
                return null;
            }

            values.TryGetValue(TitleOption, out var title);
            values.TryGetValue(ExeOption, out var exe);
            return new ShortcutRequest(id.Trim(), title?.Trim() ?? string.Empty, exe?.Trim() ?? string.Empty);
        }

        /// <summary>Ключ ли это, а не значение. Пути и названия с '--' не начинаются.</summary>
        /// <param name="arg">Аргумент.</param>
        /// <returns>true, если аргумент — ключ.</returns>
        private static bool IsOption(string arg) => arg.StartsWith("--", StringComparison.Ordinal);

        /// <summary>
        /// Снимает кавычки со значения. Оболочка обычно снимает их сама, но лаунчер
        /// запускают и в обход неё — тогда кавычки доезжают до нас как есть.
        /// </summary>
        /// <param name="value">Значение.</param>
        /// <returns>Значение без обрамляющих кавычек.</returns>
        private static string Unquote(string value) {
            var v = value.Trim();
            return v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;
        }

        /// <summary>
        /// Значение, пригодное для одной строки: без переводов строки и прочих
        /// управляющих знаков.
        /// <para>
        /// Название игры приходит с сервера, а хранится и передаётся оно построчно
        /// (см. <see cref="ShortcutRequestFile"/>). Перевод строки внутри названия
        /// сдвинул бы все строки записи, и путь к exe лаунчер прочитал бы из середины
        /// названия. Проще не пустить такой знак дальше сервера, чем потом гадать,
        /// какая строка чему принадлежит.
        /// </para>
        /// </summary>
        /// <param name="value">Значение.</param>
        /// <returns>То же значение в одну строку.</returns>
        internal static string OneLine(string? value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            foreach (var c in value) {
                if (!char.IsControl(c)) {
                    sb.Append(c);
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>Дописывает пару «ключ значение», пропуская пустое значение.</summary>
        /// <param name="sb">Куда дописываем.</param>
        /// <param name="option">Ключ.</param>
        /// <param name="value">Значение.</param>
        private static void Append(StringBuilder sb, string option, string? value) {
            var text = Escaped(value);
            if (text.Length == 0) {
                return;
            }

            if (sb.Length > 0) {
                sb.Append(' ');
            }

            sb.Append(option).Append(" \"").Append(text).Append('"');
        }

        /// <summary>
        /// Готовит значение к жизни внутри кавычек командной строки.
        /// <para>
        /// Кавычки внутри значения выбрасываем: название игры приходит с сервера, а
        /// экранировать их в строке аргументов ярлыка нечем — оболочка разобрала бы
        /// такую строку не так, как мы её собирали, и путь к exe уехал бы по частям.
        /// </para>
        /// <para>
        /// Хвостовая обратная косая черта уходит по той же причине, хотя выглядит
        /// безобидно: разбор командной строки Windows читает <c>\"</c> как саму кавычку,
        /// то есть значение, кончающееся на <c>\</c>, съедает закрывающую кавычку и
        /// склеивается со следующим ключом. У пути к exe такого хвоста не бывает, а
        /// название игры пишет человек в админке.
        /// </para>
        /// </summary>
        /// <param name="value">Значение.</param>
        /// <returns>Значение, которое кавычки не сломает.</returns>
        private static string Escaped(string? value)
            => OneLine(value).Replace("\"", string.Empty, StringComparison.Ordinal).TrimEnd('\\');
    }
}
