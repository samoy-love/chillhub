// <copyright file="StartupSequence.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;

    using ChillHub.Core.Logging;

    /// <summary>
    /// Порядок шагов запуска лаунчера.
    /// <para>
    /// Порядок здесь — это и есть поведение, а не оформление. Замок единственного
    /// экземпляра берётся ПЕРВЫМ, раньше вообще любого шага: две копии лаунчера
    /// синхронизируют одну папку игры независимо друг от друга — один качает файл,
    /// второй считает его лишним и удаляет (см. <see cref="SingleInstance"/>). Любой
    /// шаг, уехавший выше замка, начинает выполняться и во второй копии тоже — включая
    /// применение темы, которое на первом запуске пишет config.json и создаёт корень
    /// каталога игр.
    /// </para>
    /// <para>
    /// Каждый шаг — отдельный шов: без них проверить порядок можно только запустив
    /// настоящий лаунчер, а именно запуск здесь и проверяется.
    /// </para>
    /// </summary>
    internal sealed class StartupSequence {
        /// <summary>Раннее применение темы: чтение конфига разворачивает ресурсы темы.</summary>
        internal Action ApplyTheme { get; set; } = () => { _ = ConfigService.Current; };

        /// <summary>Занимает замок единственного экземпляра; false — запускаться нельзя.</summary>
        internal Func<bool> AcquireSingleInstance { get; set; } = SingleInstance.TryAcquire;

        /// <summary>Ставит глобальные обработчики исключений и подключает консоль родителя.</summary>
        internal Action InstallGlobalHandlers { get; set; } = () => { };

        /// <summary>
        /// Разовая уборка каталога данных WebView2, оставшегося в папке установки
        /// от версий без явного UserDataFolder. Делаем на старте, а не при первом
        /// открытии новости: иначе у тех, кто новости не читает, он остаётся навсегда.
        /// </summary>
        internal Action CleanupLegacyWebViewFolder { get; set; } = () => { };

        /// <summary>Обезличенная статистика: «выстрелил и забыл», сеть не ждём.</summary>
        internal Action SendStartMetric { get; set; } = ChillHub.Core.Metrics.MetricsService.LauncherStart;

        /// <summary>
        /// Проходит шаги запуска по порядку.
        /// </summary>
        /// <returns>false, если лаунчер обязан немедленно завершиться (замок занят).</returns>
        internal bool Run() {
            // Второй экземпляр не запускаем: две копии синхронизируют одну папку игры
            // независимо друг от друга — один качает файл, другой считает его лишним и
            // удаляет. Выходим ДО base.OnStartup, иначе поднимется окно обновления.
            if (!this.AcquireSingleInstance()) {
                return false;
            }

            // Тема применяется уже под замком: чтение конфига на первом запуске
            // разворачивает умолчания, а это запись config.json и создание корня каталога
            // игр. Пока шаг стоял выше замка, копия, которой запускаться не разрешат,
            // успевала оставить след на диске и столкнуться на записи config.json
            // с той копией, которая замок взяла.
            this.ApplyTheme();

            // Глобальные обработчики исключений и лог.
            // BootLog, Logger и ErrorReporter гасят собственные ошибки, поэтому
            // оборачивать каждый их вызов в try не нужно — раньше это давало десяток пустых catch.
            try {
                this.InstallGlobalHandlers();
            }
            catch (Exception ex) {
                // Без обработчиков лаунчер работоспособен, просто ошибки не попадут в отчёты.
                // Пишем в boot.log: обычный лог на этом этапе может быть ещё недоступен.
                BootLog.Append("Не удалось установить глобальные обработчики ошибок: " + ex);
                Logger.Warn("Не удалось установить глобальные обработчики ошибок: " + ex.Message);
            }

            try {
                this.CleanupLegacyWebViewFolder();
            }
            catch (Exception ex) {
                Logger.Warn("Cleanup legacy WebView2 folder failed: " + ex.Message);
            }

            try {
                this.SendStartMetric();
            }
            catch (Exception ex) {
                Logger.Warn("Не удалось отправить метрику запуска: " + ex.Message);
            }

            return true;
        }

        /// <summary>
        /// Пускать ли лаунчер дальше окна обновления.
        /// <para>
        /// Диалог закрывают крестиком (DialogResult null) и кнопкой; отдельно от результата
        /// окно выставляет <c>Proceed</c>, когда обновление не требуется. Перепутать эти два
        /// признака — значит либо не пустить пользователя в лаунчер, либо запустить его
        /// в обход обязательного обновления.
        /// </para>
        /// </summary>
        /// <param name="dialogResult">Результат <c>ShowDialog</c> окна обновления.</param>
        /// <param name="proceed">Признак «можно продолжать» от самого окна.</param>
        /// <returns>true, если пора показывать главное окно.</returns>
        internal static bool ShouldShowMainWindow(bool? dialogResult, bool proceed)
            => dialogResult == true || proceed;
    }
}
