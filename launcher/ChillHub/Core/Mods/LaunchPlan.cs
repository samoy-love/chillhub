// <copyright file="LaunchPlan.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using ChillHub.Core.Maintenance;

    /// <summary>Что экран должен сделать по нажатию на строку меню запуска.</summary>
    internal enum LaunchStep {
        /// <summary>Ничего: строка недоступна.</summary>
        Nothing,

        /// <summary>Запустить игру прямо сейчас.</summary>
        Play,

        /// <summary>Поставить модпак в эту папку и следом запустить.</summary>
        InstallModsThenPlay,

        /// <summary>Поставить сборку игры в очередь загрузок.</summary>
        Enqueue,

        /// <summary>Запрещено режимом технических работ.</summary>
        Blocked,
    }

    /// <summary>Решение вместе с тем, что сказать пользователю.</summary>
    /// <param name="Step">Что делать.</param>
    /// <param name="Message">Текст в строку состояния; пусто — говорить нечего.</param>
    internal readonly record struct LaunchDecision(LaunchStep Step, string Message);

    /// <summary>
    /// Как узнать, что сейчас лежит на диске и в Steam.
    /// <para>
    /// Настоящие реализации ходят в реестр Windows и по файловой системе, поэтому
    /// без этого шва «какие варианты предложить» проверялось бы только руками на
    /// машине, где стоит и Steam, и сама игра.
    /// </para>
    /// </summary>
    /// <param name="LocalRoot">Папка сборки Chill Hub по идентификатору игры.</param>
    /// <param name="HasLocalFiles">Есть ли в папке хоть что-то полезное.</param>
    /// <param name="LocateSteam">Поиск копии игры в Steam по AppID и имени папки.</param>
    /// <param name="ReadModsVersion">Версия модпака, установленного в указанную папку.</param>
    /// <param name="LogLine">Куда писать ход поиска Steam; null — не писать.</param>
    /// <param name="ModsBroken">
    /// Недосчитались ли файлов установленного в папку модпака. null — не спрашивать:
    /// так ведут себя тесты, которым важны другие ветки, и ответ по умолчанию —
    /// «претензий нет».
    /// </param>
    internal sealed record LaunchProbes(
        Func<string?, string> LocalRoot,
        Func<string, bool> HasLocalFiles,
        Func<string, string, SteamGame> LocateSteam,
        Func<string, string> ReadModsVersion,
        Action<string>? LogLine = null,
        Func<string, bool>? ModsBroken = null);

    /// <summary>
    /// Решения меню запуска, отделённые от самого меню.
    /// <para>
    /// Ровно по той же причине, что и <see cref="Home.ActionButtonState"/>: внутри
    /// страницы WPF это код, который проверяется только руками, а ошибка здесь — не
    /// исключение, а тихо не то действие. «Установить моды» вместо «играть» стоит
    /// полутора гигабайт трафика, «играть» вместо «установить» — запуска игры без
    /// модов, которых игрок ждал.
    /// </para>
    /// </summary>
    internal static class LaunchPlan {
        /// <summary>
        /// Считает варианты запуска игры на текущий момент.
        /// <para>
        /// Поиск копии в Steam делается здесь, а не заранее: игру могли поставить или
        /// удалить, пока лаунчер был открыт.
        /// </para>
        /// </summary>
        /// <param name="game">Игра из каталога; без настроек модов вариантов нет.</param>
        /// <param name="probes">Чем узнавать состояние копий.</param>
        /// <returns>Варианты запуска.</returns>
        internal static IReadOnlyList<LaunchOption> OptionsFor(GameInfo? game, LaunchProbes probes) {
            if (game?.Mods is not { } mods) {
                return Array.Empty<LaunchOption>();
            }

            var localRoot = probes.LocalRoot(game.GameId);
            var steam = probes.LocateSteam(mods.SteamAppId ?? string.Empty, mods.SteamFolder ?? string.Empty);

            if (probes.LogLine != null) {
                foreach (var line in steam.Trace) {
                    probes.LogLine($"[mods] поиск Steam: {line}");
                }
            }

            var steamModsVersion = probes.ReadModsVersion(steam.GameDir);

            return ModsLaunch.Options(new LaunchContext(
                mods,
                localRoot,
                probes.HasLocalFiles(localRoot),
                game.NeedsUpdate,
                !string.IsNullOrWhiteSpace(game.LatestVersion),
                steam,
                steamModsVersion,
                game.Title ?? string.Empty,
                SteamModsBroken: Broken(probes, steam.GameDir, steamModsVersion),
                LocalModsBroken: Broken(probes, localRoot, probes.ReadModsVersion(localRoot))));
        }

        /// <summary>
        /// Недосчитались ли файлов модпака в этой папке.
        /// <para>
        /// Спрашиваем только там, где модпак числится установленным: без маркера версии
        /// сверять не с чем, а обход папок ради заведомого «нечего сказать» этот метод
        /// зовут слишком часто, чтобы себе позволить.
        /// </para>
        /// </summary>
        /// <param name="probes">Чем узнавать состояние копий.</param>
        /// <param name="root">Папка игры.</param>
        /// <param name="modsVersion">Версия модпака, записанная в этой папке.</param>
        /// <returns>true, если модпак заявлен, но неполон.</returns>
        private static bool Broken(LaunchProbes probes, string root, string modsVersion)
            => probes.ModsBroken != null
                && !string.IsNullOrWhiteSpace(modsVersion)
                && probes.ModsBroken(root);

        /// <summary>
        /// Тот же вариант после установки — уже готовый к запуску.
        /// <para>
        /// Пересчёт обязателен: строка была «установить моды», а стать должна «играть».
        /// Запускать по старому объекту нельзя — в нём записано, что моды не стоят.
        /// </para>
        /// </summary>
        /// <param name="options">Пересчитанные варианты.</param>
        /// <param name="target">Что игрок выбирал.</param>
        /// <returns>Готовый вариант или null, если он так и не готов.</returns>
        internal static LaunchOption? ReadyAfterInstall(IReadOnlyList<LaunchOption>? options, LaunchTarget target)
            => options?.FirstOrDefault(o => o.Target == target && o.ReadyToPlay);

        /// <summary>
        /// Что произойдёт по нажатию на строку меню.
        /// <para>
        /// Режим технических работ проверяется ПО ДЕЙСТВИЮ, а не по одному
        /// «запуск запрещён»: строка «установить игру с модами» — это закачка, и
        /// запрещать её должен запрет установки, а не запрет запуска. Раньше здесь
        /// стояла одна проверка BlocksPlay, и во время работ, закрывших только
        /// установку, лаунчер спокойно ставил игру в очередь.
        /// </para>
        /// </summary>
        /// <param name="option">Выбранная строка меню.</param>
        /// <param name="state">Состояние режима технических работ.</param>
        /// <returns>Что делать и что сказать.</returns>
        internal static LaunchDecision Decide(LaunchOption? option, MaintenanceState state) {
            if (option == null) {
                return new LaunchDecision(LaunchStep.Nothing, string.Empty);
            }

            var banner = state?.BuildBannerText() ?? string.Empty;
            switch (option.Action) {
                case LaunchAction.Unavailable:
                    return new LaunchDecision(LaunchStep.Nothing, option.Note);

                case LaunchAction.InstallMods:
                    // Модпак пишется в папку игры — это обновление файлов, а не запуск.
                    return state?.BlocksUpdate == true
                        ? new LaunchDecision(LaunchStep.Blocked, banner)
                        : new LaunchDecision(LaunchStep.InstallModsThenPlay, string.Empty);

                case LaunchAction.InstallGame:
                    return state?.BlocksInstall == true
                        ? new LaunchDecision(LaunchStep.Blocked, banner)
                        : new LaunchDecision(LaunchStep.Enqueue, string.Empty);

                case LaunchAction.Update:
                    return state?.BlocksUpdate == true
                        ? new LaunchDecision(LaunchStep.Blocked, banner)
                        : new LaunchDecision(LaunchStep.Enqueue, string.Empty);

                default:
                    return state?.BlocksPlay == true
                        ? new LaunchDecision(LaunchStep.Blocked, banner)
                        : new LaunchDecision(LaunchStep.Play, string.Empty);
            }
        }
    }
}
