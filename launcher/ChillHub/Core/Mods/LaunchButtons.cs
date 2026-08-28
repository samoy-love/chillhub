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
    internal sealed record LaunchButtonView(
        LaunchTarget Target,
        string Title,
        string Subtitle,
        string Tooltip,
        bool Accent);

    /// <summary>
    /// Что показать в строке действий витрины.
    /// </summary>
    /// <param name="Buttons">Кнопки запуска с модами — ноль, одна или две.</param>
    /// <param name="ActionVisible">Показывать ли обычную кнопку действия («Играть», «Обновить»…).</param>
    /// <param name="MenuVisible">Показывать ли стрелку с остальными вариантами.</param>
    /// <param name="MenuTooltip">Подсказка стрелки.</param>
    internal sealed record LaunchBarView(
        IReadOnlyList<LaunchButtonView> Buttons,
        bool ActionVisible,
        bool MenuVisible,
        string MenuTooltip);

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
        /// <returns>Что показать в строке действий.</returns>
        internal static LaunchBarView Compute(
            ModsInfo? mods,
            bool playMode,
            bool steamAllowed,
            IReadOnlyList<LaunchOption>? options,
            LaunchTarget? remembered) {
            var modded = (playMode || steamAllowed) && mods != null && !string.IsNullOrWhiteSpace(mods.SteamAppId);
            if (!modded || options == null || options.Count == 0) {
                return new LaunchBarView(Array.Empty<LaunchButtonView>(), true, false, string.Empty);
            }

            // Вне режима «Играть» сборки с сервера на витрине нет: её кнопка — это
            // «Установить»/«Обновить» слева, и второй такой же рядом быть не должно.
            var wanted = playMode ? Primary : SteamOnly;
            var buttons = wanted
                .Select(t => options.FirstOrDefault(o => o.Target == t && o.Available))
                .Where(o => o != null)
                .Select(o => Button(o!, mods, remembered))
                .ToList();

            // «Играть» остаётся, пока кнопки запуска её не заменили: вне режима «Играть»
            // она вообще про другое — про установку и обновление сборки.
            var actionVisible = !playMode || buttons.Count == 0;
            var rest = MenuOptions(options, buttons);
            var tooltip = buttons.Count == 0 ? "Выбрать, что запускать" : MenuTooltip(rest);

            return new LaunchBarView(buttons, actionVisible, rest.Count > 0, tooltip);
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

        /// <summary>Собирает одну кнопку витрины из варианта запуска.</summary>
        /// <param name="option">Вариант.</param>
        /// <param name="mods">Настройки модов игры.</param>
        /// <param name="remembered">Запомненный вариант или null.</param>
        /// <returns>Кнопка.</returns>
        private static LaunchButtonView Button(LaunchOption option, ModsInfo? mods, LaunchTarget? remembered) {
            // Подпись говорит о ДЕЙСТВИИ, а не о состоянии: «установить моды» вместо
            // «моды не установлены». Кнопка на то и кнопка, что называет своё нажатие.
            var subtitle = option.ReadyToPlay ? "с модами" : option.Note;
            var full = ModsLaunch.TitleOf(option.Target, mods);
            var accent = remembered == option.Target;

            var tooltip = option.ReadyToPlay ? full : $"{full} — {option.Note}";
            if (accent) {
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
