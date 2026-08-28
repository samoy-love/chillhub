// <copyright file="GameCatalog.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Порядок игр в списке главного экрана и поиск позиции игры в нём.
    /// Ничего не знает про список UI — работает с обычными коллекциями.
    /// </summary>
    internal sealed class GameCatalog {
        // Порядок игр, полученный от API. Нужен, чтобы список сортировался одинаково
        // во всех местах: раньше часть кода упорядочивала по API, часть — по названию,
        // и список прыгал после установки или удаления игры.
        private readonly Dictionary<string, int> apiOrder = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Запоминает порядок игр, пришедший от API: он задаёт положение игр внутри групп.
        /// </summary>
        /// <param name="games">Список в том виде, в котором его вернул сервер.</param>
        internal void RememberApiOrder(IEnumerable<GameInfo> games) {
            this.apiOrder.Clear();
            var i = 0;
            foreach (var g in games) {
                var id = g?.GameId ?? string.Empty;
                if (!this.apiOrder.ContainsKey(id)) {
                    this.apiOrder[id] = i;
                }

                i++;
            }
        }

        /// <summary>
        /// Единственное правило сортировки списка игр: установленные сверху, внутри группы —
        /// порядок из ответа API, при его отсутствии — по названию. Раньше правил было два
        /// (одно после загрузки, другое после установки и удаления), и список перетасовывался
        /// сам собой прямо под курсором.
        /// </summary>
        /// <param name="games">Исходный список.</param>
        /// <returns>Новый отсортированный список.</returns>
        internal List<GameInfo> Sort(IEnumerable<GameInfo> games) =>
            games.OrderBy(g => g.IsInstalled ? 0 : 1)
                 .ThenBy(g => this.apiOrder.TryGetValue(g.GameId ?? string.Empty, out var idx) ? idx : int.MaxValue)
                 .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                 .ToList();

        /// <summary>
        /// Вливает свежий ответ сервера в уже показанный список, сохраняя сами объекты игр.
        /// <para>
        /// ОБЪЕКТ ИГРЫ — ЭТО ЕЁ СТРОКА НА ЭКРАНЕ. Обновление списка целиком заменяло его
        /// новыми объектами, и для WPF это был другой список: строки пересоздавались,
        /// значки перезагружались, выделение слетало и восстанавливалось вручную — а
        /// вместе с ним заново грузилась вся правая половина экрана. Выглядело это как
        /// рывок на ровном месте, при том что в списке обычно ничего не менялось.
        /// </para>
        /// <para>
        /// Поэтому от сервера берутся только его поля. Состояние диска — установлена,
        /// какая версия лежит, нужно ли обновление, что с очередью — остаётся тем,
        /// которое лаунчер уже посчитал: сервер о нём ничего не знает и обнулил бы его.
        /// </para>
        /// </summary>
        /// <param name="current">Список, который сейчас на экране.</param>
        /// <param name="incoming">Что вернул сервер.</param>
        /// <returns>Список в порядке ответа сервера, из прежних объектов, где они были.</returns>
        internal static List<GameInfo> Merge(IEnumerable<GameInfo>? current, IEnumerable<GameInfo>? incoming) {
            var fresh = incoming?.Where(g => g != null).ToList() ?? new List<GameInfo>();
            if (current == null) {
                return fresh;
            }

            var known = new Dictionary<string, GameInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in current) {
                if (g != null && !string.IsNullOrWhiteSpace(g.GameId)) {
                    known[g.GameId] = g;
                }
            }

            var result = new List<GameInfo>(fresh.Count);
            foreach (var g in fresh) {
                if (string.IsNullOrWhiteSpace(g.GameId) || !known.TryGetValue(g.GameId, out var existing)) {
                    result.Add(g);
                    continue;
                }

                existing.Title = g.Title;
                existing.HasLatest = g.HasLatest;
                existing.LatestVersion = g.LatestVersion;
                existing.ManifestUrl = g.ManifestUrl;
                existing.ExeRelativePath = g.ExeRelativePath;
                existing.IconUrl = g.IconUrl;
                existing.Mods = g.Mods;
                result.Add(existing);
            }

            return result;
        }

        /// <summary>
        /// Идут ли игры в том же порядке. Нужно, чтобы не подменять источник списка
        /// впустую: смена источника пересоздаёт строки со всеми их значками, а порядок
        /// после обычной проверки статусов чаще всего прежний.
        /// </summary>
        /// <param name="left">Один список.</param>
        /// <param name="right">Другой.</param>
        /// <returns>true, если это те же игры в том же порядке.</returns>
        internal static bool SameOrder(IReadOnlyList<GameInfo>? left, IReadOnlyList<GameInfo>? right) {
            if (ReferenceEquals(left, right)) {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count) {
                return false;
            }

            for (var i = 0; i < left.Count; i++) {
                if (!string.Equals(left[i]?.GameId, right[i]?.GameId, StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Пора ли отдать списку новый источник.
        /// <para>
        /// Сравнивать нужно именно с тем, что СЕЙЧАС ПОКАЗАНО (<c>ItemsSource</c>), а не с
        /// полем страницы: слияние ответа сервера создаёт новый список, и поле уже
        /// указывает на него, пока на экране висит прежний. Сравнение поля с самим собой
        /// всегда говорило «порядок тот же», источник не менялся — и игра, удалённая в
        /// админке, оставалась в списке после обновления. Пропадали только её значки: их
        /// перезагружали отдельно, и сервер их больше не отдавал.
        /// </para>
        /// </summary>
        /// <param name="bound">Что сейчас привязано к списку (<c>ItemsSource</c>).</param>
        /// <param name="next">Что должно быть показано.</param>
        /// <returns>true, если источник нужно подменить.</returns>
        internal static bool NeedsRebind(object? bound, IReadOnlyList<GameInfo>? next)
            => !SameOrder(bound as IReadOnlyList<GameInfo>, next);

        /// <summary>
        /// Какую игру выделить при первом показе списка: последнюю запущенную, иначе первую
        /// установленную, иначе первую в списке. Возвращает -1 для пустого списка — выделять нечего.
        /// <para>
        /// Промах здесь стоит дорого не сам по себе, а последствиями: выделение задаёт игру,
        /// к которой относятся кнопка действия, оценка объёма загрузки и новости.
        /// </para>
        /// </summary>
        /// <param name="games">Уже отсортированный список.</param>
        /// <param name="lastGameId">Игра из конфига (<c>LastGameId</c>).</param>
        /// <returns>Индекс выделяемой игры или -1.</returns>
        internal static int SelectStartupIndex(List<GameInfo> games, string? lastGameId) {
            if (games == null || games.Count == 0) {
                return -1;
            }

            int idx = -1;
            if (!string.IsNullOrWhiteSpace(lastGameId)) {
                idx = games.FindIndex(g => string.Equals(g.GameId, lastGameId, StringComparison.OrdinalIgnoreCase));
            }

            if (idx < 0) {
                idx = games.FindIndex(g => g.IsInstalled);
            }

            if (idx < 0) {
                idx = 0;
            }

            return idx;
        }

        /// <summary>
        /// Позиция игры в списке по точному совпадению идентификатора. Используется там,
        /// где выделение восстанавливается после пересортировки уже полученного списка:
        /// идентификатор берётся из того же списка и совпадает посимвольно.
        /// </summary>
        /// <param name="games">Список игр.</param>
        /// <param name="gameId">Искомый идентификатор.</param>
        /// <returns>Индекс или -1.</returns>
        internal static int IndexOf(List<GameInfo> games, string? gameId) {
            if (games == null) {
                return -1;
            }

            return games.FindIndex(x => x.GameId == gameId);
        }

        /// <summary>
        /// Позиция игры в списке без учёта регистра. Используется после ПОВТОРНОЙ загрузки
        /// списка с сервера: там идентификатор сравнивается с пришедшим от API, а он может
        /// отличаться регистром.
        /// </summary>
        /// <param name="games">Список игр.</param>
        /// <param name="gameId">Искомый идентификатор.</param>
        /// <returns>Индекс или -1.</returns>
        internal static int IndexOfIgnoreCase(List<GameInfo> games, string? gameId) {
            if (games == null) {
                return -1;
            }

            return games.FindIndex(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Кого выделить после обновления списка: прежнюю игру, если она осталась, иначе
        /// первую. Пустой список выделять нечем.
        /// <para>
        /// Возврат к первой игре — не мелочь: игру могли удалить на сервере, и без
        /// выделения витрина пустеет, а кнопка действия остаётся от игры, которой уже нет.
        /// </para>
        /// </summary>
        /// <param name="games">Список после обновления.</param>
        /// <param name="previousId">Игра, выделенная до обновления.</param>
        /// <returns>Индекс выделяемой игры или -1, если список пуст.</returns>
        internal static int SelectionIndexAfterRefresh(List<GameInfo>? games, string? previousId) {
            if (games == null || games.Count == 0) {
                return -1;
            }

            var idx = string.IsNullOrWhiteSpace(previousId) ? -1 : IndexOfIgnoreCase(games, previousId);
            return idx >= 0 ? idx : 0;
        }
    }
}
