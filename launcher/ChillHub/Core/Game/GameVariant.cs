// <copyright file="GameVariant.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;

    using ChillHub.Core.Mods;

    /// <summary>
    /// Одна конкретная копия игры: какая игра и откуда запущена.
    /// <para>
    /// У ИГРЫ ЧЕТЫРЕ ВЕРСИИ, А НЕ ОДНА. Копия из Steam и сборка с сервера, каждая с
    /// модами и без, — это четыре разные папки и четыре разных процесса. Пока и
    /// «запущена», и наигранное время считались на одну игру целиком, запуск копии
    /// из Steam подписывал «запускается…» обе кнопки сразу: и Steam, и Пиратку.
    /// </para>
    /// <para>
    /// Ключ хранения — <c>repo#SteamModded</c>. Строкой, а не парой полей: он уезжает
    /// в JSON, где ключ объекта может быть только строкой, и читается там глазами.
    /// </para>
    /// </summary>
    /// <param name="GameId">Игра.</param>
    /// <param name="Target">Откуда её запускают.</param>
    internal readonly record struct GameVariant(string GameId, LaunchTarget Target) {
        /// <summary>Разделитель игры и варианта в ключе.</summary>
        private const char Separator = '#';

        /// <summary>Ключ для хранения и сравнения.</summary>
        internal string Key => this.GameId + Separator + this.Target;

        /// <summary>Ключ этой игры и этого варианта.</summary>
        /// <param name="gameId">Игра.</param>
        /// <param name="target">Вариант запуска.</param>
        /// <returns>Ключ.</returns>
        internal static string KeyOf(string gameId, LaunchTarget target) =>
            new GameVariant(gameId, target).Key;

        /// <summary>
        /// Относится ли ключ хранения к этой игре — включая ключи БЕЗ варианта.
        /// <para>
        /// Записи, сделанные до разделения по вариантам, лежат под голым
        /// идентификатором игры. Они остаются в сумме: наигранное за месяцы время не
        /// должно исчезнуть из-за того, что мы научились считать его точнее.
        /// </para>
        /// </summary>
        /// <param name="key">Ключ из файла.</param>
        /// <param name="gameId">Игра.</param>
        /// <returns>true, если запись про эту игру.</returns>
        internal static bool BelongsTo(string? key, string? gameId) {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(gameId)) {
                return false;
            }

            var cut = key!.IndexOf(Separator);
            var game = cut < 0 ? key : key.Substring(0, cut);
            return string.Equals(game, gameId, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc/>
        public override string ToString() => this.Key;
    }
}
