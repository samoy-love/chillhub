// <copyright file="LaunchOptionsCache.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Снимок вариантов запуска на короткое время.
    /// <para>
    /// Считать варианты — значит сходить в реестр Windows и прочитать несколько файлов, а
    /// строка действий витрины пересобирается на каждое событие очереди и проверки
    /// статусов, то есть десятки раз подряд. Снимок живёт секунду и снимает эту нагрузку,
    /// ничего при этом не обещая: щелчок по кнопке всё равно пересчитывает варианты
    /// заново, потому что за секунду игру могли удалить из Steam.
    /// </para>
    /// <para>
    /// В ключе — всё, от чего варианты зависят на нашей стороне: другая игра или
    /// сменившееся «установлена / требует обновления» считаются заново, не дожидаясь,
    /// пока истечёт срок. Иначе кнопка «установить моды» ещё секунду обещала бы установку
    /// того, что уже установлено.
    /// </para>
    /// </summary>
    internal sealed class LaunchOptionsCache {
        /// <summary>Сколько миллисекунд снимок считается свежим.</summary>
        internal const long LifetimeMs = 1000;

        private readonly Func<long> now;
        private string key = string.Empty;
        private long stamp;
        private IReadOnlyList<LaunchOption>? options;

        /// <summary>Initializes a new instance of the <see cref="LaunchOptionsCache"/> class.</summary>
        /// <param name="now">
        /// Часы в миллисекундах; по умолчанию — время работы системы. Шов нужен тестам:
        /// проверять срок жизни снимка, ожидая настоящую секунду, значит замедлять прогон
        /// ради того, что и так можно посчитать.
        /// </param>
        internal LaunchOptionsCache(Func<long>? now = null)
            => this.now = now ?? (() => Environment.TickCount64);

        /// <summary>
        /// Ключ снимка для этой игры: идентификатор и то, что известно про её файлы.
        /// </summary>
        /// <param name="game">Игра из каталога.</param>
        /// <returns>Ключ.</returns>
        internal static string KeyFor(GameInfo? game)
            => $"{game?.GameId}|{game?.IsInstalled == true}|{game?.NeedsUpdate == true}";

        /// <summary>Отдаёт снимок, если он ещё годится.</summary>
        /// <param name="game">Игра, для которой нужны варианты.</param>
        /// <returns>Варианты или null, если считать придётся заново.</returns>
        internal IReadOnlyList<LaunchOption>? Get(GameInfo? game) {
            if (this.options == null) {
                return null;
            }

            if (!string.Equals(this.key, KeyFor(game), StringComparison.OrdinalIgnoreCase)) {
                return null;
            }

            return this.now() - this.stamp < LifetimeMs ? this.options : null;
        }

        /// <summary>Запоминает посчитанные варианты.</summary>
        /// <param name="game">Игра, для которой они посчитаны.</param>
        /// <param name="value">Сами варианты.</param>
        internal void Put(GameInfo? game, IReadOnlyList<LaunchOption> value) {
            this.key = KeyFor(game);
            this.stamp = this.now();
            this.options = value;
        }

        /// <summary>
        /// Забывает снимок: состояние копий только что менялось, и держаться за
        /// посчитанное до установки модов — значит показывать вчерашний день.
        /// </summary>
        internal void Invalidate() => this.options = null;
    }
}
