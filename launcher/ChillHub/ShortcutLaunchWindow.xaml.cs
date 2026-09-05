// <copyright file="ShortcutLaunchWindow.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub {
    using System;
    using System.Windows;

    using ChillHub.Core.Shell;

    /// <summary>
    /// Окно на случай, когда ярлык ведёт в игру, которой в лаунчере уже нет.
    /// <para>
    /// Ярлык открывает главную с выделенной игрой (см. <see cref="Core.Home.GameLocalState"/>),
    /// но игру могли снять с публикации, а сервер может быть недоступен. Открывать в ответ
    /// каталог с выделением на чужой игре — значит не ответить на нажатие вовсе, поэтому
    /// лаунчер предлагает единственное, что ещё может: запустить установленные файлы как есть.
    /// </para>
    /// <para>
    /// Оформление берётся из темы лаунчера, а не из системного MessageBox: диалог оболочки
    /// Windows посреди запуска игры выглядит как чужая ошибка, а не как разговор с
    /// лаунчером. Тот же довод, по которому своя палитра ушла из окна самообновления.
    /// </para>
    /// </summary>
    public partial class ShortcutLaunchWindow : Window {
        private readonly ShortcutRequest request;
        private readonly ShortcutOpenAction action;

        /// <summary>Initializes a new instance of the <see cref="ShortcutLaunchWindow"/> class.</summary>
        /// <param name="request">Запрос ярлыка: название и путь к exe на момент установки.</param>
        /// <param name="action">Решение о запросе — см. <see cref="ShortcutOpen.Decide"/>.</param>
        internal ShortcutLaunchWindow(ShortcutRequest request, ShortcutOpenAction action) {
            this.InitializeComponent();
            this.request = request;

            this.action = action;
            this.HeadingText.Text = ShortcutOpen.Heading(request, action);
            this.MessageText.Text = ShortcutOpen.Message(request, action);
            this.LaunchBtn.Content = ShortcutOpen.PrimaryButton(action);

            // Запускать нечего — кнопки нет вовсе: погашенная кнопка выглядела бы как
            // «лаунчер что-то может, просто не сейчас», а он не может ничего.
            if (action != ShortcutOpenAction.OfferLaunch && action != ShortcutOpenAction.OfferInstall) {
                this.ShowNothingToLaunch();
            }

            this.SourceInitialized += (s, e) => {
                try {
                    Core.UI.AcrylicHelper.ApplyTitleBarTheme(this, true);
                }
                catch (Exception ex) {
                    // Тёмный заголовок — украшение: окно обязано открыться и без него.
                    Core.Logging.Logger.Warn($"ShortcutLaunchWindow: оформление заголовка не применено: {ex.Message}");
                }
            };
        }

        /// <summary>Игра запущена в обход лаунчера. Нужно вызывающему для лога.</summary>
        internal bool Launched { get; private set; }

        /// <summary>Человек согласился скачать игру заново — качать и запускать вызывающему.</summary>
        internal bool InstallRequested { get; private set; }

        private void LaunchBtn_Click(object sender, RoutedEventArgs e) {
            // Скачать заново окно не умеет и не должно: очередь и запуск живут на главной.
            // Отсюда уходит только согласие.
            if (this.action == ShortcutOpenAction.OfferInstall) {
                this.InstallRequested = true;
                this.DialogResult = true;
                this.Close();
                return;
            }

            this.Launched = ShortcutFallbackLaunch.TryStart(this.request.ExePath);
            if (!this.Launched) {
                // Файл исчез между показом окна и нажатием: говорим об этом прямо в окне.
                // Закрывать его нельзя — иначе нажатие снова выглядит как «ничего».
                this.MessageText.Text = ShortcutOpen.Message(this.request, ShortcutOpenAction.ReportMissing);
                this.ShowNothingToLaunch();
                return;
            }

            this.DialogResult = true;
            this.Close();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) {
            this.DialogResult = false;
            this.Close();
        }

        /// <summary>Оставляет в окне одну кнопку: предлагать больше нечего.</summary>
        private void ShowNothingToLaunch() {
            this.HeadingText.Text = ShortcutOpen.Heading(this.request, ShortcutOpenAction.ReportMissing);
            this.LaunchBtn.Visibility = Visibility.Collapsed;
            this.CloseBtn.Content = "Понятно";
        }
    }
}
