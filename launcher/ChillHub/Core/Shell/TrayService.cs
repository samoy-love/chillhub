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
    /// Значок лаунчера в трее. Живёт всё время работы приложения и остаётся единственным
    /// способом выйти, пока окно спрятано (см. <see cref="Core.AppConfig.MinimizeToTray"/>) —
    /// событие <see cref="ExitRequested"/> сигнализирует об этом моменте.
    /// <para>
    /// Меню нарочно короткое: «Открыть», «Играть: имя», «Выход». Пункт «Проверить
    /// обновления» убран — он только поднимал окно, а окно и так проверяет версию при
    /// каждом появлении на экране; заодно было непонятно, чьи обновления имеются в виду.
    /// Название игры подставляется в момент открытия меню (<see cref="MenuOpening"/>), а
    /// не при уходе в трей: значок виден всегда, и выбор мог смениться, пока окно на экране.
    /// </para>
    /// <para>
    /// Библиотека стороннего трея не подключалась: проект уже ссылается на
    /// <c>UseWindowsForms</c> (WebView2/диалоги), поэтому обычный
    /// <see cref="Forms.NotifyIcon"/> закрывает задачу без новой зависимости.
    /// </para>
    /// </summary>
    internal sealed class TrayService : IDisposable {
        /// <summary>Подпись пункта запуска, когда играть не во что.</summary>
        internal const string NoGamePlayText = "Игра не выбрана";

        /// <summary>Имя приложения — начало подсказки над значком.</summary>
        internal const string AppTitle = "Chill Hub";

        /// <summary>
        /// Потолок подсказки NotifyIcon: у Windows он 63 символа, при превышении
        /// <see cref="Forms.NotifyIcon.Text"/> бросает исключение.
        /// </summary>
        private const int MaxTipLength = 63;

        private readonly Forms.NotifyIcon icon;
        private readonly Forms.ToolStripMenuItem playItem;
        private bool disposed;

        internal TrayService() {
            this.icon = new Forms.NotifyIcon {
                Text = AppTitle,
                Icon = LoadIcon(),
                Visible = false,
            };

            var menu = new Forms.ContextMenuStrip();

            // Жирный — пункт по умолчанию: то же, что делает двойной клик по значку.
            var openItem = new Forms.ToolStripMenuItem("Открыть") {
                Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold),
            };
            openItem.Click += (s, e) => this.OpenRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(openItem);

            this.playItem = new Forms.ToolStripMenuItem(NoGamePlayText) { Enabled = false };
            this.playItem.Click += (s, e) => this.PlayRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(this.playItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            var exitItem = new Forms.ToolStripMenuItem("Выход");
            exitItem.Click += (s, e) => this.ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exitItem);

            menu.Opening += (s, e) => this.MenuOpening?.Invoke(this, EventArgs.Empty);

            this.icon.ContextMenuStrip = menu;

            // Одиночный левый клик тоже открывает окно: так ведут себя Steam, Discord и
            // мессенджеры, и именно этого ждут от значка. Двойной клик оставлен для тех,
            // кто привык к нему — оба жеста делают одно и то же.
            this.icon.MouseClick += (s, e) => {
                if (e.Button == Forms.MouseButtons.Left) {
                    this.OpenRequested?.Invoke(this, EventArgs.Empty);
                }
            };
            this.icon.DoubleClick += (s, e) => this.OpenRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Пункт «Открыть» либо клик по значку.</summary>
        internal event EventHandler? OpenRequested;

        /// <summary>Пункт запуска выбранной игры.</summary>
        internal event EventHandler? PlayRequested;

        /// <summary>Пункт «Выход» — единственный способ завершить процесс из трея.</summary>
        internal event EventHandler? ExitRequested;

        /// <summary>
        /// Меню вот-вот покажется — момент обновить подпись игры через
        /// <see cref="SetCurrentGame"/>, чтобы в меню было то, что выбрано сейчас.
        /// </summary>
        internal event EventHandler? MenuOpening;

        /// <summary>Подпись пункта запуска — шов для теста: само меню в прогоне не открыть.</summary>
        internal string PlayItemText => this.playItem.Text ?? string.Empty;

        /// <summary>Доступность пункта запуска — тот же шов.</summary>
        internal bool PlayItemEnabled => this.playItem.Enabled;

        /// <summary>Подсказка над значком — тот же шов.</summary>
        internal string TipText => this.icon.Text;

        /// <summary>
        /// Подсказка над значком: «ChillHub» или «ChillHub — 38% · ещё 2». Пустая строка
        /// возвращает голое имя. Собрана здесь, а не у вызывающего: у подсказки жёсткий
        /// потолок длины (см. <see cref="MaxTipLength"/>), и обрезать её должен тот, кто
        /// про этот потолок знает.
        /// </summary>
        /// <param name="status">Строка состояния или пустая строка.</param>
        /// <returns>Итоговый текст подсказки.</returns>
        internal static string BuildTip(string? status) {
            var s = (status ?? string.Empty).Trim();
            var tip = s.Length > 0 ? $"{AppTitle} — {s}" : AppTitle;
            return tip.Length > MaxTipLength ? tip.Substring(0, MaxTipLength - 1) + "…" : tip;
        }

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

        /// <summary>
        /// Показывает в подсказке над значком ход загрузок (см. <see cref="Core.UI.DownloadsChip"/>):
        /// у спрятанного окна нет другого места сказать, что закачка идёт и докуда дошла.
        /// </summary>
        /// <param name="status">Подпись чипа загрузок или пустая строка, если очередь пуста.</param>
        internal void SetStatus(string? status) {
            var tip = BuildTip(status);

            // Присваивание Text у значка — обращение к оболочке Windows (Shell_NotifyIcon),
            // а отчёты о ходе закачки приходят десять раз в секунду. Подсказка при этом
            // меняется в лучшем случае раз в секунду: процент округлён до целых. Сверяем
            // строку и не тревожим оболочку впустую.
            if (string.Equals(tip, this.icon.Text, StringComparison.Ordinal)) {
                return;
            }

            this.icon.Text = tip;
        }

        /// <summary>
        /// Всплывающее уведомление у значка. Используется, когда окно спрятано и сообщить
        /// иначе некуда — например, что игра докачалась.
        /// </summary>
        /// <param name="title">Заголовок.</param>
        /// <param name="text">Текст.</param>
        internal void Notify(string title, string text) {
            if (this.disposed || !this.icon.Visible) {
                return;
            }

            try {
                this.icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
            }
            catch (Exception ex) {
                Logging.Logger.Warn("TrayService: не удалось показать уведомление: " + ex.Message);
            }
        }

        /// <summary>Показывает значок в трее.</summary>
        internal void Show() => this.icon.Visible = true;

        /// <summary>Убирает значок из трея (перед выходом из приложения).</summary>
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
