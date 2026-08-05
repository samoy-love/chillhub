// <copyright file="PreviousUpdateOutcome.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;

    using ChillHub.Update;

    /// <summary>
    /// A12. Исход ПРОШЛОГО запуска апдейтера.
    /// <para>
    /// Апдейтер возвращает 2 («скопировалось не всё») и 3 («фатально»), но читать
    /// эти коды некому: лаунчер к тому моменту уже завершился, а сам апдейтер
    /// умирает последним. Поэтому исход он пишет в файл состояния рядом с маркером
    /// версии, а лаунчер при следующем старте показывает его один раз — иначе
    /// неудавшееся обновление выглядит как «ничего не произошло», и пользователь
    /// снова жмёт «Обновить», не понимая, почему предыдущий раз не сработал.
    /// </para>
    /// </summary>
    internal static class PreviousUpdateOutcome {
        /// <summary>
        /// Читает файл состояния и возвращает текст для показа пользователю.
        /// Файл при этом снимается: сообщение показывается один раз, следующий
        /// запуск апдейтера перезапишет его заново.
        /// </summary>
        /// <param name="baseDir">Папка установки лаунчера.</param>
        /// <returns>Текст об ошибке прошлого обновления либо null (успех, нет файла, сбой чтения).</returns>
        internal static string? Describe(string baseDir) {
            try {
                var status = UpdateStatus.TryRead(baseDir);
                if (status == null) {
                    return null;
                }

                // Показываем один раз: файл перезапишет следующий запуск апдейтера.
                UpdateStatus.Clear(baseDir);

                if (status.IsSuccess) {
                    try {
                        Logging.Logger.Info($"Previous self-update: ok, version={status.Version}");
                    }
                    catch {
                    }

                    return null;
                }

                try {
                    Logging.Logger.Error(
                        new InvalidOperationException($"Previous self-update failed: outcome={status.Outcome} exit={status.ExitCode} message={status.Message} log={status.LogPath}"),
                        "UpdateWindow.PreviousUpdateOutcome");
                }
                catch {
                }

                var text = "Предыдущее обновление не было применено: " +
                    (string.IsNullOrWhiteSpace(status.Message) ? status.Outcome : status.Message);
                if (!string.IsNullOrWhiteSpace(status.LogPath)) {
                    text += $"\nЖурнал: {status.LogPath}";
                }

                return text;
            }
            catch {
                // Диагностика не должна мешать запуску.
                return null;
            }
        }
    }
}
