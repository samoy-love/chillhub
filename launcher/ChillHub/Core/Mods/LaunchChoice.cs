// <copyright file="LaunchChoice.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Чем игрок в последний раз запускал игру с модами.
    /// <para>
    /// БЕЗ ПАМЯТИ ВЫБОР ПРЕВРАЩАЕТСЯ В ПОШЛИНУ. Вариантов запуска четыре, но человек
    /// почти всегда играет одним и тем же: «моя копия из Steam с модами». Меню на
    /// кнопке «Играть» брало с него два клика КАЖДЫЙ раз — и, что хуже, ничем не
    /// показывало, что оно вообще откроется вместо запуска.
    /// </para>
    /// <para>
    /// Поэтому выбор запоминается: «Играть» стартует запомненное сразу, а стрелка
    /// рядом открывает все четыре. Пока ничего не выбрано, «Играть» открывает меню —
    /// первый запуск и есть тот момент, когда выбор осмыслен.
    /// </para>
    /// </summary>
    internal static class LaunchChoice {
        /// <summary>Что игрок выбирал для этой игры, или null.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Запомненный вариант или null.</returns>
        internal static LaunchTarget? Remembered(string? gameId) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return null;
            }

            var map = ConfigService.Current.LaunchTargets;
            if (map == null || !map.TryGetValue(gameId, out var raw)) {
                return null;
            }

            return Enum.TryParse<LaunchTarget>(raw, ignoreCase: true, out var target) ? target : null;
        }

        /// <summary>Запоминает выбор игрока.</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="target">Что он запустил.</param>
        internal static void Remember(string? gameId, LaunchTarget target) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return;
            }

            try {
                var cfg = ConfigService.Current;
                cfg.LaunchTargets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                cfg.LaunchTargets[gameId] = target.ToString();
                ConfigService.Save(cfg);
            }
            catch (Exception ex) {
                // Не сохранился выбор — игра всё равно запускается. Ронять запуск
                // из-за настройки, которая лишь экономит клик, нельзя.
                Logging.Logger.Warn($"[mods] выбор запуска не сохранён: {ex.Message}");
            }
        }

        /// <summary>
        /// Что запустить по кнопке «Играть»: запомненное, если оно доступно, иначе
        /// null — тогда вызывающий показывает меню.
        /// <para>
        /// Проверка доступности здесь обязательна: игрок мог выбрать «Steam · с модами»
        /// и удалить игру из Steam. Молча запустить вместо неё что-то другое — худший
        /// исход из возможных, поэтому в таком случае снова спрашиваем.
        /// </para>
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="options">Варианты, посчитанные на этот момент.</param>
        /// <returns>Доступный запомненный вариант или null.</returns>
        internal static LaunchOption? Preferred(string? gameId, IReadOnlyList<LaunchOption> options) {
            if (options == null || options.Count == 0) {
                return null;
            }

            var remembered = Remembered(gameId);
            if (remembered is not { } target) {
                return null;
            }

            return options.FirstOrDefault(o => o.Target == target && o.Available);
        }
    }
}
