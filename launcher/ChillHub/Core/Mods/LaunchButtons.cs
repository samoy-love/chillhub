// <copyright file="LaunchButtons.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Одна кнопка запуска на витрине.
    /// </summary>
    /// <param name="Target">Что она запускает.</param>
    /// <param name="Title">Крупная строка: откуда копия.</param>
    /// <param name="Subtitle">Мелкая строка: что произойдёт по нажатию.</param>
    /// <param name="Tooltip">Подсказка целиком — с названием модпака и пояснением.</param>
    /// <param name="Accent">Красить ли акцентом: это последний запущенный вариант.</param>
    /// <param name="Enabled">
    /// Можно ли нажать. Ложь бывает у единственного состояния — игра уже запущена или
    /// запускается: кнопка остаётся на месте и подписью объясняет, что происходит,
    /// вместо того чтобы исчезнуть или поднять вторую копию игры.
    /// </param>
    internal sealed record LaunchButtonView(
        LaunchTarget Target,
        string Title,
        string Subtitle,
        string Tooltip,
        bool Accent,
        bool Enabled = true) {
        /// <summary>
        /// Ключ стиля кнопки в ресурсах темы. Акцент носит вариант, которым играли в
        /// прошлый раз, и он может оказаться как первой кнопкой, так и второй — поэтому
        /// стиль считается здесь, а не задаётся в разметке по месту.
        /// </summary>
        internal string StyleKey => this.Accent ? "Style.LaunchButton.Accent" : "Style.LaunchButton.Ghost";
    }

    /// <summary>
    /// Что показать в строке действий витрины.
    /// </summary>
    /// <param name="Buttons">Кнопки запуска с модами — ноль, одна или две.</param>
    /// <param name="ActionVisible">Показывать ли обычную кнопку действия («Играть», «Обновить»…).</param>
    /// <param name="MenuVisible">Показывать ли стрелку с остальными вариантами.</param>
    /// <param name="MenuTooltip">Подсказка стрелки.</param>
    /// <param name="Run">
    /// Что сейчас с игрой: запущена, запускается или ни то ни другое. От этого зависит
    /// не только вид кнопок запуска, но и стрелка рядом — под ней лежат такие же
    /// запуски, и оставить её живой значило бы оставить обход только что закрытой двери.
    /// </param>
    internal sealed record LaunchBarView(
        IReadOnlyList<LaunchButtonView> Buttons,
        bool ActionVisible,
        bool MenuVisible,
        string MenuTooltip,
        Game.GameRunState Run = Game.GameRunState.None);

    /// <summary>
    /// Строка действий витрины для игры с модами: два способа играть — кнопками,
    /// остальное — под стрелкой.
    /// <para>
    /// ВЫБОР, СПРЯТАННЫЙ В МЕНЮ, НЕ ВЫБОР. Раньше на витрине стояла одна кнопка
    /// «Играть» и стрелка рядом: чем именно она запустит — своей копией из Steam или
    /// сборкой с сервера, с модами или без — можно было узнать, только открыв меню
    /// или наведя мышь на подсказку. А запускают почти всегда одно из двух: «Steam ·
    /// с модами» или «Пиратка · с модами».
    /// </para>
    /// <para>
    /// Поэтому эти два варианта вынесены отдельными кнопками, а «без модов» остались
    /// под стрелкой: они нужны редко, и место на витрине им не по чину.
    /// </para>
    /// <para>
    /// Кнопка появляется, только если её вариант действительно можно нажать.
    /// Недоступный (нет Steam, нет сборки на сервере) не превращается в серый
    /// прямоугольник без объяснений — он уходит в меню, где рядом с ним стоит
    /// причина: «Steam не установлен» объясняет, а выключенная кнопка — нет.
    /// </para>
    /// </summary>
    internal static class LaunchButtons {
        /// <summary>Варианты, которым место на витрине, в порядке показа.</summary>
        private static readonly LaunchTarget[] Primary = {
            LaunchTarget.SteamModded,
            LaunchTarget.LocalModded,
        };

        /// <summary>То же, когда сборки с сервера на витрине быть не может.</summary>
        private static readonly LaunchTarget[] SteamOnly = {
            LaunchTarget.SteamModded,
        };

        /// <summary>
        /// Короткая подпись копии — на кнопку, где длинной строке не поместиться.
        /// Название модпака при этом не теряется: оно уходит в подсказку.
        /// </summary>
        /// <param name="target">Вариант запуска.</param>
        /// <returns>Откуда копия.</returns>
        internal static string SourceOf(LaunchTarget target) => target switch {
            LaunchTarget.SteamModded or LaunchTarget.SteamVanilla => "Steam",
            _ => "Пиратка",
        };

        /// <summary>
        /// Считает строку действий витрины.
        /// <para>
        /// Пока игра качается, удаляется или проверяется, кнопок запуска нет вовсе:
        /// запускать нечего, и предложение выбрать копию в этот момент — обещание,
        /// которого лаунчер не выполнит.
        /// </para>
        /// <para>
        /// А вот «сборка с сервера ещё не скачана» запуску копии из Steam не помеха:
        /// это разные папки и разные файлы. Поэтому «Steam · с модами» стоит рядом с
        /// «Установить» и ставит моды в копию Steam, не трогая сборку.
        /// </para>
        /// </summary>
        /// <param name="mods">Настройки модов игры; null — игра без модов.</param>
        /// <param name="playMode">Кнопка действия сейчас в режиме «Играть».</param>
        /// <param name="steamAllowed">
        /// Можно ли предлагать запуск копии из Steam, когда «Играть» на витрине нет.
        /// Копия из Steam НЕ ЗАВИСИТ от сборки с сервера: моды ставятся в чужую папку,
        /// и требовать ради них скачать десять гигабайт сборки, которую игрок не
        /// просил, — плата ни за что. Отсюда «Установить» и «Steam · с модами» стоят
        /// рядом: первая ставит сборку, вторая ставит моды в Steam и запускает.
        /// </param>
        /// <param name="options">Варианты запуска, посчитанные на этот момент.</param>
        /// <param name="remembered">Запомненный вариант запуска или null.</param>
        /// <param name="run">Запущена ли игра прямо сейчас.</param>
        /// <returns>Что показать в строке действий.</returns>
        internal static LaunchBarView Compute(
            ModsInfo? mods,
            bool playMode,
            bool steamAllowed,
            IReadOnlyList<LaunchOption>? options,
            LaunchTarget? remembered,
            Game.GameRunState run = Game.GameRunState.None) {
            var modded = (playMode || steamAllowed) && mods != null && !string.IsNullOrWhiteSpace(mods.SteamAppId);
            if (!modded || options == null || options.Count == 0) {
                return new LaunchBarView(Array.Empty<LaunchButtonView>(), true, false, string.Empty, run);
            }

            // Вне режима «Играть» сборки с сервера на витрине нет: её кнопка — это
            // «Установить»/«Обновить» слева, и второй такой же рядом быть не должно.
            var wanted = playMode ? Primary : SteamOnly;
            var picked = wanted
                .Select(t => options.FirstOrDefault(o => o.Target == t && o.Available))
                .Where(o => o != null)
                .ToList();

            // «Играть» остаётся, пока кнопки запуска её не заменили: вне режима «Играть»
            // она вообще про другое — про установку и обновление сборки.
            // Кнопка действия остаётся, пока ей есть что делать: поставить или обновить
            // сборку с сервера. У игры, которой на сервере нет вовсе, она умеет сказать
            // только «Нужна копия в Steam» — и рядом с работающей кнопкой запуска это
            // выключенный прямоугольник, объясняющий то, что уже решено соседом.
            var hasServerBuild = options.Any(o => o.Target is LaunchTarget.LocalModded or LaunchTarget.LocalVanilla);
            var actionVisible = picked.Count == 0 || (!playMode && hasServerBuild);

            // ЗАЛИТАЯ КНОПКА В РЯДУ РОВНО ОДНА. Пока акцент носил просто «запускали в
            // прошлый раз», у неустановленной игры их выходило две: «Установить» слева и
            // «Steam · с модами» рядом — два фиолетовых прямоугольника, и ни один не
            // читался как главный. Когда на витрине стоит «Установить», главная — она:
            // запуск чужой копии из Steam здесь запасной путь, а не основной.
            // ЕДИНСТВЕННОЕ ДЕЙСТВИЕ НА ЭКРАНЕ — ГЛАВНОЕ. Пока акцент значил только
            // «запускали в прошлый раз», у нового игрока витрина оставалась вовсе без
            // залитой кнопки: одна «стеклянная» кнопка запуска посреди пустого ряда
            // читается как запасной путь, а идти больше некуда.
            var soleAction = !actionVisible && picked.Count == 1;
            var buttons = picked
                .Select(o => Button(o!, mods, actionVisible ? null : remembered, soleAction))
                .ToList();
            var rest = MenuOptions(options, buttons);
            var tooltip = buttons.Count == 0 ? "Выбрать, что запускать" : MenuTooltip(rest);

            // ИГРА УЖЕ ИДЁТ — НАЖИМАТЬ НЕЧЕГО. Кнопки не исчезают и не меняют
            // назначения: они остаются на своих местах и говорят, что происходит.
            // Пропади они — витрина запущенной игры выглядела бы сломанной, а
            // останься живыми — второе нажатие подняло бы вторую копию игры.
            if (run != Game.GameRunState.None) {
                var note = Game.RunningGameLook.ButtonNote(run);
                buttons = buttons
                    .Select(b => b with { Subtitle = note, Tooltip = $"{b.Tooltip} · {note}", Enabled = false })
                    .ToList();
                tooltip = note;
            }

            return new LaunchBarView(buttons, actionVisible, rest.Count > 0, tooltip, run);
        }


        /// <summary>
        /// Что положить под стрелку: всё, что не попало на витрину.
        /// <para>
        /// Именно всё, а не только «без модов»: недоступный вариант с модами тоже
        /// уходит сюда — вместе с причиной, по которой его нет на витрине.
        /// </para>
        /// </summary>
        /// <param name="options">Все варианты запуска.</param>
        /// <param name="shown">Что уже стоит кнопками на витрине.</param>
        /// <returns>Строки меню в порядке показа.</returns>
        internal static IReadOnlyList<LaunchOption> MenuOptions(
            IReadOnlyList<LaunchOption>? options, IReadOnlyList<LaunchButtonView>? shown) {
            if (options == null) {
                return Array.Empty<LaunchOption>();
            }

            var onScreen = shown?.Select(b => b.Target).ToHashSet() ?? new HashSet<LaunchTarget>();
            return options.Where(o => !onScreen.Contains(o.Target)).ToList();
        }

        /// <summary>
        /// Что делать по нажатию на кнопку витрины.
        /// <para>
        /// Варианты пересчитываются между отрисовкой кнопки и щелчком по ней, и это не
        /// перестраховка: игру могли удалить из Steam, а запустить не то, что написано на
        /// кнопке, — худший из возможных исходов. Пропавший вариант объясняется словами,
        /// а не превращается в ничего не делающее нажатие.
        /// </para>
        /// </summary>
        /// <param name="options">Варианты, посчитанные заново.</param>
        /// <param name="target">Что было написано на кнопке.</param>
        /// <returns>Вариант к запуску либо причина отказа.</returns>
        internal static (LaunchOption? Option, string Message) Chosen(
            IReadOnlyList<LaunchOption>? options, LaunchTarget target) {
            var option = options?.FirstOrDefault(o => o.Target == target);
            if (option is { Available: true }) {
                return (option, string.Empty);
            }

            var note = option?.Note ?? string.Empty;
            return (null, note.Length > 0 ? note : "Этот вариант запуска сейчас недоступен.");
        }

        /// <summary>Собирает одну кнопку витрины из варианта запуска.</summary>
        /// <param name="option">Вариант.</param>
        /// <param name="mods">Настройки модов игры.</param>
        /// <param name="remembered">Запомненный вариант или null.</param>
        /// <param name="soleAction">
        /// Эта кнопка — единственное действие витрины: рядом нет ни второй кнопки
        /// запуска, ни «Установить». Тогда она главная просто потому, что других нет.
        /// </param>
        /// <returns>Кнопка.</returns>
        private static LaunchButtonView Button(
            LaunchOption option, ModsInfo? mods, LaunchTarget? remembered, bool soleAction = false) {
            // Подпись говорит о ДЕЙСТВИИ, а не о состоянии: «установить моды» вместо
            // «моды не установлены». Кнопка на то и кнопка, что называет своё нажатие.
            var subtitle = option.ReadyToPlay ? "с модами" : option.Note;
            var full = ModsLaunch.TitleOf(option.Target, mods);
            var wasLast = remembered == option.Target;
            var accent = wasLast || soleAction;

            var tooltip = option.ReadyToPlay ? full : $"{full} — {option.Note}";
            if (wasLast) {
                // Приписка идёт за ПАМЯТЬЮ, а не за цветом: единственная кнопка красится
                // акцентом и тогда, когда игрок ещё ничего не запускал, и обещать ему
                // прошлый раз, которого не было, нельзя.
                tooltip += " · запускали в прошлый раз";
            }

            return new LaunchButtonView(option.Target, SourceOf(option.Target), subtitle, tooltip, accent);
        }

        /// <summary>
        /// Подсказка стрелки: она называет то, что под ней лежит. «Ещё варианты» без
        /// перечисления не говорит ничего — открывать меню, чтобы узнать, что в меню,
        /// игрок не обязан.
        /// </summary>
        /// <param name="rest">Варианты, оставшиеся под стрелкой.</param>
        /// <returns>Текст подсказки.</returns>
        private static string MenuTooltip(IReadOnlyList<LaunchOption> rest) {
            return rest.Count == 0
                ? "Другие варианты запуска"
                : "Ещё: " + string.Join(", ", rest.Select(o => o.Title));
        }
    }
}
