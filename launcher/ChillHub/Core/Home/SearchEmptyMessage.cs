// <copyright file="SearchEmptyMessage.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    /// <summary>
    /// Что показать, когда поиск не нашёл ни одной игры.
    /// <para>
    /// Раньше список в этом случае просто пустел, и экран не отличался от «каталог не
    /// приехал»: игрок видел ничто и не знал, виноват ли запрос, связь или сервер.
    /// Поэтому подсказка называет размер каталога — она отвечает на вопрос «а игры-то
    /// вообще есть?» — и предлагает два выхода: другое слово или сброс.
    /// </para>
    /// </summary>
    public static class SearchEmptyMessage {
        /// <summary>Заголовок пустой выдачи.</summary>
        public const string Title = "Ничего не нашлось";

        /// <summary>Подсказка под заголовком: сколько игр в каталоге и что делать.</summary>
        /// <param name="catalogSize">Сколько игр в каталоге всего.</param>
        /// <returns>Готовая строка для показа.</returns>
        public static string Hint(int catalogSize) {
            if (catalogSize <= 0) {
                return "Попробуйте другое слово или сбросьте поиск.";
            }

            return $"В каталоге {catalogSize} {PluralizeGameRu(catalogSize)} — попробуйте другое слово или сбросьте поиск.";
        }

        /// <summary>Русское склонение слова «игра» по числу.</summary>
        /// <param name="n">Количество игр.</param>
        /// <returns>«игра», «игры» или «игр».</returns>
        internal static string PluralizeGameRu(int n) {
            var n10 = n % 10;
            var n100 = n % 100;
            if (n10 == 1 && n100 != 11) {
                return "игра";
            }

            if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) {
                return "игры";
            }

            return "игр";
        }
    }
}
