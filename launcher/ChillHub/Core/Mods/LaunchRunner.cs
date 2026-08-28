// <copyright file="LaunchRunner.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Mods {
    using System;
    using System.Threading.Tasks;

    using ChillHub.Core.Maintenance;

    /// <summary>
    /// Связь запуска с экраном: только колбэки, никаких контролов.
    /// <para>
    /// По образцу <see cref="Game.GameSyncUi"/> и ровно по той же причине: щелчок по
    /// строке меню — это цепочка «решить, запомнить, поставить моды, запустить», и
    /// внутри страницы WPF она проверяется только руками. По умолчанию всё молчит и
    /// ничего не делает, поэтому тест, забывший подставить колбэк, не полезет в
    /// модальное окно и не запустит игру.
    /// </para>
    /// </summary>
    internal sealed class LaunchUi {
        /// <summary>Gets or sets вывод строки состояния.</summary>
        internal Action<string> SetStatus { get; set; } = _ => { };

        /// <summary>Gets or sets всплывающее сообщение.</summary>
        internal Action<string> Toast { get; set; } = _ => { };

        /// <summary>Gets or sets вопрос «да/нет»: текст, заголовок.</summary>
        internal Func<string, string, bool> Confirm { get; set; } = (_, _) => false;

        /// <summary>Gets or sets постановку игры в очередь загрузок; false — уже стоит.</summary>
        internal Func<string, bool> Enqueue { get; set; } = _ => true;

        /// <summary>Gets or sets пересчёт подписи кнопки «Играть» после смены выбора.</summary>
        internal Action RefreshChoice { get; set; } = () => { };

        /// <summary>Gets or sets установку модпака в папку: игра, название, папка.</summary>
        internal Func<GameInfo, string, string, Task<bool>> InstallMods { get; set; } =
            (_, _, _) => Task.FromResult(false);

        /// <summary>Gets or sets сам запуск игры.</summary>
        internal Action<GameInfo, LaunchOption> Launch { get; set; } = (_, _) => { };
    }

    /// <summary>
    /// Что происходит по щелчку на строке меню запуска, от решения до игры.
    /// </summary>
    internal sealed class LaunchRunner {
        private readonly LaunchUi ui;

        /// <summary>Initializes a new instance of the <see cref="LaunchRunner"/> class.</summary>
        /// <param name="ui">Колбэки к экрану.</param>
        internal LaunchRunner(LaunchUi ui) => this.ui = ui;

        /// <summary>Gets or sets признак «модпак уже ставится прямо сейчас».</summary>
        internal Func<bool> ModsBusy { get; set; } = () => false;

        /// <summary>Gets or sets запоминание выбора игрока.</summary>
        internal Action<string?, LaunchTarget> Remember { get; set; } = LaunchChoice.Remember;

        /// <summary>
        /// Доводит выбранную строку до игры.
        /// <para>
        /// ПОРЯДОК ВАЖЕН. Выбор запоминается ДО действия: игра занимает экран целиком,
        /// а закачка тянется минутами — к концу и того и другого пользователя перед
        /// лаунчером обычно уже нет, и «запомню, когда вернётся» означает «не запомню».
        /// </para>
        /// </summary>
        /// <param name="game">Игра.</param>
        /// <param name="option">Выбранная строка меню.</param>
        /// <param name="state">Режим технических работ.</param>
        /// <param name="probes">Чем пересчитать варианты после установки модов.</param>
        /// <returns>Задача, завершающаяся вместе с действием.</returns>
        internal async Task RunAsync(
            GameInfo? game, LaunchOption? option, MaintenanceState state, LaunchProbes probes) {
            if (game?.Mods == null || option == null) {
                return;
            }

            var decision = LaunchPlan.Decide(option, state);
            if (decision.Step is LaunchStep.Nothing or LaunchStep.Blocked) {
                if (!string.IsNullOrEmpty(decision.Message)) {
                    this.ui.SetStatus(decision.Message);
                }

                return;
            }

            this.Remember(game.GameId, option.Target);
            this.ui.RefreshChoice();

            switch (decision.Step) {
                case LaunchStep.Enqueue:
                    if (!this.ui.Enqueue(game.GameId)) {
                        this.ui.SetStatus("Игра уже установлена или уже в очереди.");
                    }

                    return;

                case LaunchStep.InstallModsThenPlay:
                    await this.InstallThenPlayAsync(game, option, probes).ConfigureAwait(true);
                    return;

                default:
                    this.ui.Launch(game, option);
                    return;
            }
        }

        /// <summary>
        /// Ставит модпак в папку и запускает игру.
        /// <para>
        /// ОДИН ЩЕЛЧОК НА ВЕСЬ ПУТЬ: игрок выбрал «с модами», а не «выполнить
        /// установку». Установка модпака идёт минуты, а не часы — это единственный
        /// шаг, после которого запуск без отдельного спроса уместен.
        /// </para>
        /// <para>
        /// Вопрос перед записью остаётся: полтора гигабайта уезжают в ЧУЖУЮ установку
        /// Steam, и человек должен увидеть, в какую именно папку.
        /// </para>
        /// </summary>
        /// <param name="game">Игра.</param>
        /// <param name="option">Выбранная строка меню.</param>
        /// <param name="probes">Чем пересчитать варианты после установки.</param>
        /// <returns>Задача установки и запуска.</returns>
        private async Task InstallThenPlayAsync(GameInfo game, LaunchOption option, LaunchProbes probes) {
            if (this.ModsBusy()) {
                this.ui.Toast("Моды уже устанавливаются. Дождитесь завершения.");
                return;
            }

            var title = string.IsNullOrWhiteSpace(game.Title) ? game.GameId ?? string.Empty : game.Title;
            if (string.IsNullOrWhiteSpace(option.GameDir)) {
                this.ui.SetStatus("Для этой игры нет папки, куда поставить моды");
                return;
            }

            if (!this.ui.Confirm(
                    Home.SteamModsInstall.BuildConfirmText(title, game.Mods, option.GameDir),
                    Home.SteamModsInstall.ConfirmCaption)) {
                return;
            }

            if (!await this.ui.InstallMods(game, title, option.GameDir).ConfigureAwait(true)) {
                return;
            }

            // Пересчитываем варианты: тот, что был «установить моды», стал «играть».
            // Запускать по старому объекту нельзя — в нём записано, что модов нет.
            var ready = LaunchPlan.ReadyAfterInstall(LaunchPlan.OptionsFor(game, probes), option.Target);
            if (ready != null) {
                this.ui.Launch(game, ready);
            }
        }
    }
}
