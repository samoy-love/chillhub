// <copyright file="RunningGameLook.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Как запущенная игра называется в каждом месте экрана.
    /// <para>
    /// ОДНО СОСТОЯНИЕ — ОДНО ИМЯ. Про идущую игру говорят четыре места: кнопки
    /// запуска, кнопка действия, бейдж витрины и строка списка, а пятое — отказ на
    /// повторное нажатие. Разложенные по своим файлам, они разошлись бы словами
    /// («Играет» на витрине против «Запущена» в списке) при первой же правке —
    /// как когда-то разошлись «Требуется обновление» и «Доступно обновление».
    /// </para>
    /// <para>
    /// Разные слова здесь не прихоть, а разное место: на кнопке подпись идёт
    /// строчной второй строкой под названием копии, в списке под названием игры
    /// нужно одно слово, а бейдж и кнопка действия — самостоятельные фразы.
    /// </para>
    /// </summary>
    internal static class RunningGameLook {
        /// <summary>Мелкая строка кнопки запуска: «игра запущена» / «запускается…».</summary>
        /// <param name="run">Состояние запуска.</param>
        /// <returns>Подпись; пусто, если игра не запущена.</returns>
        internal static string ButtonNote(GameRunState run) => run switch {
            GameRunState.Running => "игра запущена",
            GameRunState.Starting => "запускается…",
            _ => string.Empty,
        };

        /// <summary>
        /// Кнопка действия и бейдж витрины — одна фраза на двоих: они стоят в
        /// сантиметре друг от друга, и разные слова читались бы как разные состояния.
        /// </summary>
        /// <param name="run">Состояние запуска.</param>
        /// <returns>Фраза; пусто, если игра не запущена.</returns>
        internal static string Headline(GameRunState run) => run switch {
            GameRunState.Running => "Игра запущена",
            GameRunState.Starting => "Запускается…",
            _ => string.Empty,
        };

        /// <summary>Подпись строки списка игр: «Играет» / «Запускается…».</summary>
        /// <param name="run">Состояние запуска.</param>
        /// <returns>Подпись; пусто, если игра не запущена.</returns>
        internal static string RowLabel(GameRunState run) => run switch {
            GameRunState.Running => "Играет",
            GameRunState.Starting => "Запускается…",
            _ => string.Empty,
        };

        /// <summary>
        /// Ответ на повторное нажатие. Молчаливый отказ здесь хуже второй копии игры:
        /// человек уже решил, что кнопка не работает, — иначе он бы не нажимал.
        /// «До минуты» названо словами: через Steam это правда, и ожидание с
        /// названным сроком переносится не так, как ожидание без него.
        /// </summary>
        /// <param name="run">Состояние запуска.</param>
        /// <returns>Текст всплывашки; пусто, если отказывать не в чем.</returns>
        internal static string Refusal(GameRunState run) => run switch {
            GameRunState.Running => "Игра уже запущена.",
            GameRunState.Starting => "Игра уже запускается. Подождите — это может занять до минуты.",
            _ => string.Empty,
        };

        /// <summary>
        /// Расставляет подписи «Играет» по строкам списка.
        /// <para>
        /// В списке, а не только на витрине: свернуть лаунчер на время партии —
        /// обычное дело, и, вернувшись, игрок видел список, ничем не отличающийся от
        /// вчерашнего. Подпись живёт в самой строке (свойство с уведомлением),
        /// поэтому вызывать это надо и после пересборки списка — строки в ней новые.
        /// </para>
        /// </summary>
        /// <param name="games">Показанные игры; null — расставлять нечего.</param>
        internal static void ApplyLabels(IEnumerable<GameInfo>? games) {
            if (games == null) {
                return;
            }

            foreach (var game in games) {
                try {
                    game.RunLabel = RowLabel(RunningGames.StateOf(game.GameId));
                }
                catch (Exception ex) {
                    // Подпись — не повод бросить остальные строки без неё.
                    Logging.Logger.Warn($"RunningGameLook.ApplyLabels({game?.GameId}): {ex.Message}");
                }
            }
        }
    }
}
