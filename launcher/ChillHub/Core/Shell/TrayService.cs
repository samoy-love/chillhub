// <copyright file="TrayService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.IO;

    using Drawing = System.Drawing;
    using Forms = System.Windows.Forms;

    /// <summary>
    /// Значок лаунчера в трее. Показывается, когда окно свёрнуто «в трей» вместо закрытия
    /// (см. <see cref="Core.AppConfig.MinimizeToTray"/>), и живёт до настоящего выхода из
    /// приложения — событие <see cref="ExitRequested"/> сигнализирует об этом моменте.
    /// <para>
    /// Библиотека стороннего трея не подключалась: проект уже ссылается на
    /// <c>UseWindowsForms</c> (WebView2/диалоги), поэтому обычный
    /// <see cref="Forms.NotifyIcon"/> закрывает задачу без новой зависимости.
    /// </para>
    /// </summary>
    internal sealed class TrayService : IDisposable {
        /// <summary>Подпись пункта запуска, когда играть не во что.</summary>
        internal const string NoGamePlayText = "Игра не выбрана";

        private readonly Forms.NotifyIcon icon;
        private readonly Forms.ToolStripMenuItem playItem;
        private bool disposed;

        internal TrayService() {
            this.icon = new Forms.NotifyIcon {
                Text = "ChillHub",
                Icon = LoadIcon(),
                Visible = false,
            };

            var menu = new Forms.ContextMenuStrip();

            var openItem = new Forms.ToolStripMenuItem("Открыть лаунчер");
            openItem.Click += (s, e) => this.OpenRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(openItem);

            // Название подставляет окно через SetCurrentGame: пункт «Играть в текущую
            // игру» не говорил, в какую именно, и одинаково выглядел даже когда играть
            // было не во что.
            this.playItem = new Forms.ToolStripMenuItem(NoGamePlayText) { Enabled = false };
            this.playItem.Click += (s, e) => this.PlayRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(this.playItem);

            var updatesItem = new Forms.ToolStripMenuItem("Проверить обновления");
            updatesItem.Click += (s, e) => this.CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(updatesItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            var exitItem = new Forms.ToolStripMenuItem("Выйти полностью");
            exitItem.Click += (s, e) => this.ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exitItem);

            this.icon.ContextMenuStrip = menu;
            this.icon.DoubleClick += (s, e) => this.OpenRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Пункт «Открыть лаунчер» либо двойной клик по значку.</summary>
        internal event EventHandler? OpenRequested;

        /// <summary>Пункт запуска выбранной игры.</summary>
        internal event EventHandler? PlayRequested;

        /// <summary>Пункт «Проверить обновления».</summary>
        internal event EventHandler? CheckUpdatesRequested;

        /// <summary>Пункт «Выйти полностью» — единственный способ завершить процесс из трея.</summary>
        internal event EventHandler? ExitRequested;

        /// <summary>Подпись пункта запуска — шов для теста: само меню в прогоне не открыть.</summary>
        internal string PlayItemText => this.playItem.Text ?? string.Empty;

        /// <summary>Доступность пункта запуска — тот же шов.</summary>
        internal bool PlayItemEnabled => this.playItem.Enabled;

        /// <summary>
        /// Подписывает пункт запуска именем выбранной игры. Пустое имя выключает пункт:
        /// нажатие, которое ничего не делает, читается как сломанное меню.
        /// </summary>
        /// <param name="title">Название выбранной игры или <c>null</c>, если её нет.</param>
        internal void SetCurrentGame(string? title) {
            var name = (title ?? string.Empty).Trim();
            this.playItem.Enabled = name.Length > 0;
            this.playItem.Text = name.Length > 0 ? $"Играть: {name}" : NoGamePlayText;
        }

        /// <summary>Показывает значок в трее.</summary>
        internal void Show() => this.icon.Visible = true;

        /// <summary>Убирает значок из трея (окно вернулось на экран).</summary>
        internal void Hide() => this.icon.Visible = false;

        /// <inheritdoc/>
        public void Dispose() {
            if (this.disposed) {
                return;
            }

            this.disposed = true;
            this.icon.Visible = false;
            this.icon.Dispose();
        }

        /// <summary>
        /// Иконка значка в трее — та же, что у окна. Отсутствие файла не повод не показывать
        /// трей: тогда берём системную иконку приложения.
        /// </summary>
        private static Drawing.Icon LoadIcon() {
            try {
                string[] candidates = new[] {
                    Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
                    Path.Combine(Environment.CurrentDirectory, "Assets", "app.ico"),
                };
                foreach (var p in candidates) {
                    if (File.Exists(p)) {
                        return new Drawing.Icon(p);
                    }
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn("TrayService: не удалось загрузить app.ico: " + ex.Message);
            }

            try {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath)) {
                    var extracted = Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (extracted != null) {
                        return extracted;
                    }
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn("TrayService: не удалось извлечь иконку процесса: " + ex.Message);
            }

            return Drawing.SystemIcons.Application;
        }
    }
}
