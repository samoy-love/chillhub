// <copyright file="SettingsDialogs.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Settings {
    using System;
    using System.Windows;

    /// <summary>
    /// Показ окон со страницы настроек: сообщение об ошибке, вопрос перед восстановлением,
    /// выбор папки для игр и открытие папки в проводнике.
    /// <para>
    /// Каждый показ уведён за шов ровно по той же причине, что и в
    /// <see cref="ChillHub.Core.Home.HomeDialogs"/>: модальное окно в прогоне тестов —
    /// это повисший CI, а без подмены проверить «сказали ли пользователю правду»
    /// нечем. По умолчанию швы ведут к настоящим окнам.
    /// </para>
    /// </summary>
    internal static class SettingsDialogs {
        /// <summary>Сообщает пользователю об ошибке.</summary>
        internal static Action<string, string> ShowError { get; set; } = DefaultShowError;

        /// <summary>Задаёт вопрос «продолжить / отмена»; false — пользователь отказался.</summary>
        internal static Func<string, string, bool> Confirm { get; set; } = DefaultConfirm;

        /// <summary>
        /// Просит выбрать папку для игр, начиная с указанной; null — пользователь отказался.
        /// </summary>
        internal static Func<string, string?> PickFolder { get; set; } = DefaultPickFolder;

        /// <summary>Открывает папку в проводнике.</summary>
        internal static Action<string> OpenFolder { get; set; } = DefaultOpenFolder;

        /// <summary>Возвращает показ диалогов к настоящим окнам.</summary>
        internal static void ResetDialogsForTests() {
            ShowError = DefaultShowError;
            Confirm = DefaultConfirm;
            PickFolder = DefaultPickFolder;
            OpenFolder = DefaultOpenFolder;
        }

        private static void DefaultShowError(string message, string caption)
            => MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);

        private static bool DefaultConfirm(string message, string caption)
            => MessageBox.Show(message, caption, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        private static string? DefaultPickFolder(string initialPath) {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = "Выберите папку для игр";
            dlg.ShowNewFolderButton = true;
            dlg.SelectedPath = initialPath;
            var res = dlg.ShowDialog();
            return res == System.Windows.Forms.DialogResult.OK ? (dlg.SelectedPath ?? string.Empty) : null;
        }

        private static void DefaultOpenFolder(string path)
            => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = path,
                UseShellExecute = true,
            });
    }
}
