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
        private readonly Forms.NotifyIcon icon;
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

            // TODO(Трек A/E): подключить реальное имя текущей выбранной игры и запуск,
            // когда появится единая точка доступа к «текущей игре» (см. Core/Home).
            var playItem = new Forms.ToolStripMenuItem("Играть в текущую игру");
            playItem.Click += (s, e) => this.PlayRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(playItem);

            // TODO: связать с реальной проверкой обновлений (UpdateWindow.PrecheckAsync),
            // когда появится безопасный способ вызвать её вне стартовой последовательности.
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

        /// <summary>Пункт «Играть в текущую игру» (заглушка, см. TODO в конструкторе).</summary>
        internal event EventHandler? PlayRequested;

        /// <summary>Пункт «Проверить обновления» (заглушка, см. TODO в конструкторе).</summary>
        internal event EventHandler? CheckUpdatesRequested;

        /// <summary>Пункт «Выйти полностью» — единственный способ завершить процесс из трея.</summary>
        internal event EventHandler? ExitRequested;

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
