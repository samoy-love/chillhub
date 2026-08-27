// <copyright file="SettingsActions.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Settings {
    using System;
    using System.IO;

    using ChillHub.Core.Home;

    /// <summary>
    /// Значения, введённые на странице настроек.
    /// <para>
    /// null у тумблера означает «контрола на странице нет», а не «не отмечен»: страница
    /// проверяет каждый контрол на null перед чтением, и молча записать в конфиг false
    /// вместо отсутствующего тумблера — это отключить пользователю телеметрию без его
    /// ведома.
    /// </para>
    /// </summary>
    internal sealed class SettingsInput {
        /// <summary>Текст поля с папкой игр как есть.</summary>
        internal string? GamesPathText { get; init; }

        /// <summary>Положение ползунка числа потоков.</summary>
        internal double DownloadThreads { get; init; }

        /// <summary>Положение ползунка ограничения скорости, МБ/с (0 — без лимита).</summary>
        internal double SpeedLimitMbps { get; init; }

        /// <summary>Автоматическая отправка отчётов об ошибках.</summary>

        /// <summary>Обезличенная статистика использования.</summary>

        /// <summary>Сворачивать окно в трей вместо закрытия.</summary>
        internal bool? MinimizeToTray { get; init; }
    }

    /// <summary>
    /// Действия страницы настроек: сохранение, выбор папки для игр, открытие логов.
    /// Про контролы не знает — на вход получает значения, наружу говорит через
    /// <see cref="SettingsDialogs"/>.
    /// </summary>
    internal static class SettingsActions {
        /// <summary>
        /// Сохраняет настройки. Ошибку записи не глушит: пользователь, ушедший со страницы
        /// уверенным, что настройки сохранены, обнаружит их пропажу только после перезапуска.
        /// </summary>
        /// <param name="input">Значения со страницы.</param>
        /// <returns>true, если конфиг записан на диск.</returns>
        internal static bool Save(SettingsInput input) {
            try {
                var cfg = ConfigService.Current;
                var newPath = input.GamesPathText?.Trim();
                if (string.IsNullOrWhiteSpace(newPath)) {
                    newPath = AppConfig.DefaultGamesPath();
                }

                // Для файловой системы и конфигурации используем нормальную форму с одинарными
                // слешами. Сетевой путь вида \\nas\games при этом не превращаем в \nas\games.
                newPath = HomeFormat.NormalizeWindowsPath(newPath);
                try {
                    Directory.CreateDirectory(newPath);
                }
                catch (Exception ex) {
                    // Каталог мог быть недоступен (сетевая шара оффлайн, нет прав) — настройку
                    // всё равно сохраняем: путь может стать доступным позже.
                    ChillHub.Core.Logging.Logger.Warn($"SettingsPage.SaveBtn: не удалось создать папку игр '{newPath}': {ex.Message}");
                }

                cfg.GamesPath = newPath;
                cfg.DownloadThreads = (int)input.DownloadThreads;
                cfg.SpeedLimitMbps = (int)input.SpeedLimitMbps;
                if (input.MinimizeToTray != null) {
                    cfg.MinimizeToTray = input.MinimizeToTray == true;
                }

                // Запись может не удаться (нет прав на %APPDATA%, диск заполнен, файл занят).
                // Молчать нельзя: пользователь уйдёт со страницы уверенный, что настройки сохранены.
                if (!ConfigService.TrySave(cfg, out var saveError)) {
                    SettingsDialogs.ShowError("Не удалось сохранить настройки: " + saveError, "Ошибка");
                    return false;
                }

                return true;
            }
            catch (Exception ex) {
                SettingsDialogs.ShowError($"Не удалось сохранить настройки: {ex.Message}", "Ошибка");
                return false;
            }
        }

        /// <summary>
        /// Предлагает выбрать папку для игр. Возвращает null, если менять поле не нужно:
        /// пользователь отказался или диалог не открылся.
        /// </summary>
        /// <param name="currentText">Что сейчас в поле пути.</param>
        /// <returns>Новое содержимое поля либо null.</returns>
        internal static string? ChooseGamesFolder(string? currentText) {
            try {
                var initial = string.IsNullOrWhiteSpace(currentText)
                    ? AppConfig.DefaultGamesPath()
                    : currentText;
                var selected = SettingsDialogs.PickFolder(initial);
                if (selected == null) {
                    return null;
                }

                // Нормализуем отображение: одинарные обратные слеши (кроме префикса UNC)
                return HomeFormat.NormalizeWindowsPath(selected);
            }
            catch (Exception ex) {
                // Диалог выбора папки может не открыться (нет прав, сбой оболочки) —
                // путь всегда можно ввести руками, поэтому не мешаем пользователю
                ChillHub.Core.Logging.Logger.Error(ex, "SettingsPage.ChooseBtn_Click");
                return null;
            }
        }

        /// <summary>
        /// Открывает папку с логами. Путь берём у Logger: логи переехали из %TEMP% (его чистит
        /// система вместе с отчётами) в %APPDATA%\ChillHub, к остальному состоянию.
        /// </summary>
        internal static void OpenLogsFolder() {
            try {
                var dir = ChillHub.Core.Logging.Logger.LogDirectory;
                Directory.CreateDirectory(dir);
                SettingsDialogs.OpenFolder(dir);
            }
            catch (Exception ex) {
                SettingsDialogs.ShowError($"Не удалось открыть папку с логами: {ex.Message}", "Ошибка");
            }
        }
    }
}
