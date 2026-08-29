// <copyright file="ActionButtonState.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using ChillHub.Core.Maintenance;

    /// <summary>Режим единой кнопки действия на главном экране.</summary>
    internal enum ActionMode {
        /// <summary>Статус игры ещё проверяется — действие недоступно.</summary>
        Checking,

        /// <summary>Игры на диске нет.</summary>
        Install,

        /// <summary>Игра установлена, но отличается от эталона.</summary>
        Update,

        /// <summary>Игра готова к запуску.</summary>
        Play,

        /// <summary>Идёт установка или обновление.</summary>
        Cancel,

        /// <summary>
        /// Игра стоит в очереди и ждёт: закачки ещё нет, кнопка лишь снимает позицию.
        /// Отдельный режим, а не «Отмена»: красная «Отмена» под игрой, которая ничего не
        /// делает, читалась как «остановить процесс», которого нет.
        /// </summary>
        Dequeue,

        /// <summary>Предыдущая попытка сорвалась.</summary>
        Retry,

        /// <summary>
        /// Идёт удаление локальных файлов. Пока оно не кончилось, ни «Играть», ни
        /// «Обновить» предлагать нельзя: файлы вырываются из-под ног прямо сейчас.
        /// </summary>
        Deleting,

        /// <summary>Действие запрещено режимом технических работ на сервере (задача 25).</summary>
        Maintenance,

        /// <summary>
        /// У игры нет сборки на сервере: она живёт только копией из Steam.
        /// <para>
        /// Ставить нечего, и «Установить» здесь — обещание, которое некому выполнить:
        /// закачка такой игры кончается отказом «сервер не прислал манифест».
        /// </para>
        /// </summary>
        SteamOnly,
    }

    /// <summary>
    /// Что показать на единой кнопке действия. Решение отделено от самой кнопки:
    /// от него зависит, предложит ли лаунчер «Играть» поверх наполовину обновлённой
    /// игры и не начнёт ли качать сборку заново.
    /// </summary>
    internal static class ActionButtonState {
        /// <summary>
        /// Что вообще предложить пользователю — без учёта режима технических работ.
        /// Порядок ветвей значим: незавершённое обновление важнее «установлена и свежая»,
        /// иначе игру со смешанными файлами предложили бы запустить (C2).
        /// </summary>
        /// <param name="hasUpdateError">Предыдущая попытка обновления сорвалась.</param>
        /// <param name="unfinishedUpdate">На диске остался маркер незавершённого обновления.</param>
        /// <param name="isInstalled">Игра установлена.</param>
        /// <param name="needsUpdate">Игра отличается от эталона.</param>
        /// <param name="hasServerBuild">
        /// У игры есть сборка на сервере. Ложь — игра живёт только копией из Steam, и
        /// ставить с сервера нечего.
        /// </param>
        /// <returns>Задуманный режим кнопки.</returns>
        internal static ActionMode Decide(
            bool hasUpdateError, bool unfinishedUpdate, bool isInstalled, bool needsUpdate, bool hasServerBuild = true) {
            if (hasUpdateError) {
                return ActionMode.Retry;
            }

            // СБОРКИ НЕТ — ЗНАЧИТ, НЕТ И «УСТАНОВИТЬ». Такая игра есть только в Steam, а
            // кнопка предлагала её скачать: очередь принимала позицию, синхронизация шла
            // за манифестом, которого не существует, и всё кончалось отказом. Уже
            // установленную (осталась от прежних сборок) по-прежнему можно запустить.
            if (!hasServerBuild && !isInstalled) {
                return ActionMode.SteamOnly;
            }

            if (unfinishedUpdate) {
                // Осталось незавершённое обновление — «Играть» не предлагаем, нужно докатить (C2)
                return ActionMode.Update;
            }

            if (isInstalled && !needsUpdate) {
                return ActionMode.Play;
            }

            // Не установлена или требует обновления
            return isInstalled ? ActionMode.Update : ActionMode.Install;
        }

        /// <summary>
        /// Запрещено ли задуманное действие текущим режимом технических работ.
        /// «Повторить» приравниваем к обновлению: за ним стоит та же закачка.
        /// </summary>
        /// <param name="mode">Задуманный режим кнопки.</param>
        /// <param name="state">Состояние режима технических работ.</param>
        /// <returns>True, если действие запрещено.</returns>
        internal static bool IsBlockedByMaintenance(ActionMode mode, MaintenanceState state) {
            return mode switch {
                ActionMode.SteamOnly => false,
                ActionMode.Install => state.BlocksInstall,
                ActionMode.Update or ActionMode.Retry => state.BlocksUpdate,
                ActionMode.Play => state.BlocksPlay,
                _ => false,
            };
        }

        /// <summary>
        /// Надпись, доступность и ключ стиля для режима кнопки.
        /// <para>
        /// Запущенная игра перебивает «Играть»: пока она идёт, кнопка называет
        /// происходящее, а не предлагает поднять вторую копию. Остальные режимы —
        /// установку, обновление, отмену — она не трогает: эти действия к тому, что
        /// игра открыта, отношения не имеют, а запрет на них живёт отдельно.
        /// </para>
        /// </summary>
        /// <param name="mode">Режим кнопки.</param>
        /// <param name="run">Запущена ли выбранная игра прямо сейчас.</param>
        /// <returns>Оформление кнопки.</returns>
        internal static ActionButtonAppearance Appearance(
            ActionMode mode, Game.GameRunState run = Game.GameRunState.None) {
            if (mode == ActionMode.Play && run != Game.GameRunState.None) {
                return new ActionButtonAppearance(
                    run == Game.GameRunState.Running ? "Игра запущена" : "Запускается…",
                    false,
                    "Style.ActionButton.Checking");
            }

            return Look(mode);
        }

        /// <summary>Оформление режима как такового, без оглядки на запущенную игру.</summary>
        /// <param name="mode">Режим кнопки.</param>
        /// <returns>Оформление кнопки.</returns>
        private static ActionButtonAppearance Look(ActionMode mode) => mode switch {
            ActionMode.Cancel => new ActionButtonAppearance("Отмена", true, "Style.ActionButton.Cancel"),
            ActionMode.Dequeue => new ActionButtonAppearance("Убрать из очереди", true, "Style.ActionButton.Dequeue"),
            ActionMode.Checking => new ActionButtonAppearance("Проверка…", false, "Style.ActionButton.Checking"),
            ActionMode.Deleting => new ActionButtonAppearance("Удаление…", false, "Style.ActionButton.Checking"),
            ActionMode.Play => new ActionButtonAppearance("Играть", true, "Style.ActionButton.Play"),
            ActionMode.Retry => new ActionButtonAppearance("Повторить", true, "Style.ActionButton.Retry"),

            // Причина и сроки — в баннере шапки, на кнопке только суть запрета
            ActionMode.Maintenance => new ActionButtonAppearance("Технические работы", false, "Style.ActionButton.Checking"),
            ActionMode.Install => new ActionButtonAppearance("Установить", true, "Style.ActionButton.Install"),

            // Кнопка остаётся видимой, но выключенной, и говорит, чего не хватает.
            // Пропади она вовсе — витрина игры, в которую сейчас не сыграть, выглядела бы
            // сломанной, а не объяснённой.
            ActionMode.SteamOnly => new ActionButtonAppearance("Нужна копия в Steam", false, "Style.ActionButton.Checking"),
            _ => new ActionButtonAppearance("Обновить", true, "Style.ActionButton.Update"),
        };
    }

    /// <summary>Оформление кнопки действия для одного режима.</summary>
    /// <param name="Content">Надпись на кнопке.</param>
    /// <param name="IsEnabled">Можно ли нажать.</param>
    /// <param name="StyleKey">Ключ стиля в ресурсах темы.</param>
    internal sealed record ActionButtonAppearance(string Content, bool IsEnabled, string StyleKey);
}
