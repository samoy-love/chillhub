// <copyright file="ShortcutOpen.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>Что лаунчер делает с запросом ярлыка.</summary>
    internal enum ShortcutOpenAction {
        /// <summary>Запроса нет — обычный запуск лаунчера.</summary>
        None,

        /// <summary>Выделить игру в каталоге на главной: она там есть.</summary>
        SelectGame,

        /// <summary>Игра в каталоге есть, а файлов на диске нет — предложить скачать заново.</summary>
        OfferInstall,

        /// <summary>Игры в каталоге нет, но файлы на месте — предложить просто запустить.</summary>
        OfferLaunch,

        /// <summary>Игры нет ни в каталоге, ни на диске — предложить нечего.</summary>
        ReportMissing,
    }

    /// <summary>
    /// Решение о том, куда ведёт ярлык игры.
    /// <para>
    /// Игра из ярлыка может пропасть из лаунчера: её снимают с публикации, ей меняют
    /// идентификатор, а сервер бывает недоступен. Открыть в этом случае главную с чужой
    /// выделенной игрой — значит не ответить на нажатие вовсе, поэтому лаунчер предлагает
    /// то единственное, что ещё может сделать: запустить установленные файлы как есть.
    /// </para>
    /// </summary>
    internal static class ShortcutOpen {
        /// <summary>
        /// Решает, что делать с запросом ярлыка.
        /// </summary>
        /// <param name="request">Запрос из командной строки ярлыка.</param>
        /// <param name="games">Каталог игр, показанный на главной странице.</param>
        /// <param name="exeExists">Проверка наличия exe на диске; по умолчанию — настоящая.</param>
        /// <returns>Что делать.</returns>
        internal static ShortcutOpenAction Decide(
            ShortcutRequest? request, IEnumerable<GameInfo>? games, Func<string, bool>? exeExists = null) {
            if (request == null || string.IsNullOrWhiteSpace(request.GameId)) {
                return ShortcutOpenAction.None;
            }

            var exists = exeExists ?? File.Exists;
            var known = games?.Any(g =>
                g != null && string.Equals(g.GameId, request.GameId, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (known) {
                // Игра в каталоге есть, а папки со сборкой на диске уже нет: ярлык ведёт
                // в пустоту. Открыть каталог и промолчать — значит оставить человека с
                // вопросом «я же нажал на игру, почему я смотрю на список». Поэтому
                // отдельное окно с предложением скачать заново.
                //
                // Путь известен не у всех ярлыков: старые его не писали. Без пути судить
                // о файлах не по чему, и остаётся прежнее поведение — выделить в каталоге.
                return !string.IsNullOrWhiteSpace(request.ExePath) && !exists(request.ExePath)
                    ? ShortcutOpenAction.OfferInstall
                    : ShortcutOpenAction.SelectGame;
            }

            return !string.IsNullOrWhiteSpace(request.ExePath) && exists(request.ExePath)
                ? ShortcutOpenAction.OfferLaunch
                : ShortcutOpenAction.ReportMissing;
        }

        /// <summary>
        /// Название игры для окна: то, что было в ярлыке, иначе идентификатор. Безымянное
        /// окно оставило бы человека с разговором об игре, которую оно не называет.
        /// </summary>
        /// <param name="request">Запрос ярлыка.</param>
        /// <returns>Название для показа.</returns>
        internal static string DisplayName(ShortcutRequest? request)
            => string.IsNullOrWhiteSpace(request?.Title) ? request?.GameId ?? string.Empty : request.Title;

        /// <summary>Заголовок окна для игры, которой нет в каталоге.</summary>
        /// <param name="request">Запрос ярлыка.</param>
        /// <param name="action">Решение.</param>
        /// <returns>Строка заголовка.</returns>
        internal static string Heading(ShortcutRequest? request, ShortcutOpenAction action)
            => action switch {
                ShortcutOpenAction.OfferLaunch => $"«{DisplayName(request)}» больше нет в Chill Hub",
                ShortcutOpenAction.OfferInstall => $"«{DisplayName(request)}» не найдена на диске",
                _ => $"«{DisplayName(request)}» запустить нечем",
            };

        /// <summary>
        /// Текст окна. Он обязан объяснить не только «игры нет», но и чем это грозит:
        /// запуск в обход лаунчера — это запуск без обновления, модпака и проверки файлов.
        /// </summary>
        /// <param name="request">Запрос ярлыка.</param>
        /// <param name="action">Решение.</param>
        /// <returns>Текст для окна.</returns>
        internal static string Message(ShortcutRequest? request, ShortcutOpenAction action)
            => action switch {
                ShortcutOpenAction.OfferLaunch =>
                    "Игру не удалось найти в каталоге: её могли снять с публикации или сервер сейчас "
                    + "недоступен. Обновить её и проверить файлы лаунчеру не с чем, но установленную "
                    + "копию можно запустить как есть."
                    + Environment.NewLine + Environment.NewLine + (request?.ExePath ?? string.Empty),
                ShortcutOpenAction.OfferInstall =>
                    "Папку со сборкой переместили или удалили. Лаунчер может скачать её заново — "
                    + "ярлык после этого снова заработает."
                    + Environment.NewLine + Environment.NewLine + (request?.ExePath ?? string.Empty),
                _ =>
                    "Игры нет ни в каталоге Chill Hub, ни на диске: её файлы удалены или перенесены. "
                    + "Ярлык можно убрать с рабочего стола.",
            };

        /// <summary>
        /// Подпись главной кнопки окна. «Скачать и запустить», а не «Скачать»: человек
        /// нажимал на ярлык ИГРЫ, и остановка на середине была бы обманом ожидания.
        /// </summary>
        /// <param name="action">Решение.</param>
        /// <returns>Подпись кнопки.</returns>
        internal static string PrimaryButton(ShortcutOpenAction action)
            => action == ShortcutOpenAction.OfferInstall ? "Скачать и запустить" : "Запустить игру";
    }
}
