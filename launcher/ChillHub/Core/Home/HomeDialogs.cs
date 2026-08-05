// <copyright file="HomeDialogs.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.IO;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    /// <summary>
    /// Модальные диалоги главной страницы: подтверждение удаления локальных файлов
    /// в стиле темы и проверка доступности папки для игр с выбором новой.
    /// </summary>
    internal static class HomeDialogs {
        /// <summary>Имя временного файла для проверки прав на запись в папку игр.</summary>
        private const string WriteTestFileName = ".write_test.tmp";

        /// <summary>
        /// Признак того, что HRESULT собран из кода Win32: старший байт 0x80, средние два — FACILITY_WIN32.
        /// </summary>
        private const int Win32FacilityMask = unchecked((int)0xFFFF0000);

        /// <summary>Значение старших разрядов HRESULT для ошибок Win32.</summary>
        private const int Win32FacilityBits = unchecked((int)0x80070000);

        /// <summary>
        /// Коды Win32, означающие «писать сюда нельзя»: отказ в доступе, защита от записи,
        /// занятый или заблокированный файл, нехватка привилегий.
        /// </summary>
        private static readonly int[] DeniedWin32Codes = { 5, 19, 32, 33, 1314 };

        /// <summary>
        /// Спрашивает подтверждение на удаление папки игры. При любом сбое построения
        /// оформленного окна откатывается на системный MessageBox — вопрос пользователь увидит в любом случае.
        /// </summary>
        /// <param name="owner">Элемент, из которого берутся ресурсы темы (обычно страница).</param>
        internal static bool ConfirmDeleteGameFiles(FrameworkElement owner, string title, string folderPath) {
            try {
                var wnd = new Window {
                    Title = "Удаление локальных файлов",
                    Owner = Application.Current?.MainWindow,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ShowInTaskbar = false,
                    Background = Resource(owner, "Brush.Surface") ?? new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                    BorderBrush = Resource(owner, "Brush.Border"),
                    BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(16),
                    Foreground = Resource(owner, "Brush.Text") ?? Brushes.White,
                };

                // Тёмный заголовок окна — как у остальных окон приложения
                wnd.SourceInitialized += (_, __) => {
                    try {
                        UI.AcrylicHelper.ApplyTitleBarTheme(wnd, true);
                    }
                    catch (Exception ex) {
                        // Косметика: на старых сборках Windows API может отсутствовать.
                        Logging.Logger.Warn($"HomeDialogs: тема заголовка не применена: {ex.Message}");
                    }
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var tb1 = new TextBlock {
                    Text = $"Удалить локальные файлы игры \"{title}\"?",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Resource(owner, "Brush.Title") ?? Brushes.White,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                Grid.SetRow(tb1, 0);

                var tb2 = new TextBlock {
                    Text = $"Будет удалена папка: {HomeFormat.NormalizeDisplayPath(folderPath)}",
                    Foreground = Resource(owner, "Brush.TextSecondary") ?? new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                    Margin = new Thickness(0, 0, 0, 16),
                };
                Grid.SetRow(tb2, 1);

                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var cancelBtn = new Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
                var deleteBtn = new Button { Content = "Удалить", MinWidth = 120 };
                ApplyStyle(owner, deleteBtn, "Style.Button.Primary");
                ApplyStyle(owner, cancelBtn, "Style.Button.GhostNeutral");
                panel.Children.Add(cancelBtn);
                panel.Children.Add(deleteBtn);
                Grid.SetRow(panel, 2);

                grid.Children.Add(tb1);
                grid.Children.Add(tb2);
                grid.Children.Add(panel);
                wnd.Content = new Border {
                    CornerRadius = new CornerRadius(8),
                    Background = Resource(owner, "Brush.Surface") ?? new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                    BorderBrush = Resource(owner, "Brush.Border"),
                    BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(12),
                    Child = grid,
                };

                bool result = false;
                cancelBtn.Click += (s, e) => { result = false; wnd.DialogResult = false; };
                deleteBtn.Click += (s, e) => { result = true; wnd.DialogResult = true; };

                wnd.ShowDialog();
                return result;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, "HomeDialogs.ConfirmDeleteGameFiles");
                var norm = HomeFormat.NormalizeDisplayPath(folderPath);
                var res = MessageBox.Show(
                    $"Удалить локальные файлы игры \"{title}\"?\nБудет удалена папка: {norm}",
                    "Удаление локальных файлов",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                return res == MessageBoxResult.Yes;
            }
        }

        /// <summary>
        /// Проверяет, что в папку для игр можно писать. Если прав нет — предлагает выбрать другую
        /// и сохраняет её в конфиг. Возвращает false, если пользователь отказался или новая папка тоже недоступна.
        /// </summary>
        internal static bool EnsureGamesPathAccessibleOrPrompt() {
            try {
                var cfg = ConfigService.Current;
                var path = cfg.GamesPath;
                if (string.IsNullOrWhiteSpace(path)) {
                    path = AppConfig.DefaultGamesPath();
                }

                switch (ProbeWritable(path)) {
                    case WriteProbe.Ok:
                        return true;
                    case WriteProbe.UnknownIoError:
                        // Не похоже на отказ в доступе — не беспокоим пользователя
                        return true;
                }

                var question = $"Нет доступа к папке для игр:\n{path}\n\nВыбрать другую папку сейчас?";
                var res = MessageBox.Show(question, "Нет доступа", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) {
                    return false;
                }

                return TryPickNewGamesPath(cfg);
            }
            catch (Exception ex) {
                // Сама проверка сломалась — не блокируем пользователю установку из-за диагностики.
                Logging.Logger.Error(ex, "HomeDialogs.EnsureGamesPathAccessibleOrPrompt");
                return true;
            }
        }

        private static bool TryPickNewGamesPath(AppConfig cfg) {
            try {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog {
                    Description = "Выберите папку для игр",
                    ShowNewFolderButton = true,
                    SelectedPath = AppConfig.DefaultGamesPath(),
                };

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) {
                    return false;
                }

                var newPath = dlg.SelectedPath;
                if (ProbeWritable(newPath) == WriteProbe.Ok) {
                    cfg.GamesPath = newPath;
                    if (!ConfigService.TrySave(cfg, out var saveError)) {
                        // Иначе выбранная папка «примется» только до перезапуска
                        MessageBox.Show(
                            "Папка выбрана, но настройки сохранить не удалось: " + saveError,
                            "Ошибка",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }

                    return true;
                }

                MessageBox.Show($"Нет доступа к выбранной папке: {newPath}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, "HomeDialogs.TryPickNewGamesPath");
                return false;
            }
        }

        private enum WriteProbe {
            /// <summary>В папку можно писать.</summary>
            Ok,

            /// <summary>Доступ запрещён — есть смысл предложить другую папку.</summary>
            Denied,

            /// <summary>Иная IO-ошибка: на отказ в доступе не похоже, лучше не мешать пользователю.</summary>
            UnknownIoError,
        }

        private static WriteProbe ProbeWritable(string path) {
            try {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) {
                // Папку могло не получиться создать — точный вердикт вынесет попытка записи ниже.
                Logging.Logger.Warn($"ProbeWritable: не удалось создать '{path}': {ex.Message}");
            }

            var testFile = Path.Combine(path, WriteTestFileName);
            try {
                using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    fs.WriteByte(0);
                }

                try {
                    File.Delete(testFile);
                }
                catch (Exception ex) {
                    // Временный файл остался — не страшно, права на запись мы уже подтвердили.
                    Logging.Logger.Warn($"ProbeWritable: не удалось удалить '{testFile}': {ex.Message}");
                }

                return WriteProbe.Ok;
            }
            catch (UnauthorizedAccessException) {
                Logging.Logger.Warn($"ProbeWritable: нет прав на запись в '{path}'");
                return WriteProbe.Denied;
            }
            catch (IOException ioex) {
                Logging.Logger.Warn($"ProbeWritable: IO-ошибка для '{path}': {ioex.Message}");
                return ClassifyIoFailure(ioex);
            }
        }

        /// <summary>
        /// Решает, похожа ли IO-ошибка на отказ в доступе — от этого зависит,
        /// предложат ли пользователю выбрать другую папку.
        /// <para>
        /// Вердикт выносится по коду Win32 внутри HRESULT, а не по тексту сообщения.
        /// Текст исключения локализован: на немецкой Windows там будет «Zugriff», на
        /// французской — «accès», и проверка на «доступ»/«access» отказ бы не узнала.
        /// Пользователю не предложили бы другую папку, и установка упиралась бы
        /// в непонятный сбой.
        /// </para>
        /// <para>
        /// Текст остаётся запасным признаком на случай, когда HRESULT не из Win32
        /// (исключение собрано вручную или пришло не от файловой системы): без него
        /// русская и английская локаль потеряли бы часть уже работающих распознаваний.
        /// </para>
        /// </summary>
        private static WriteProbe ClassifyIoFailure(IOException ioex) {
            if ((ioex.HResult & Win32FacilityMask) == Win32FacilityBits) {
                var code = ioex.HResult & 0xFFFF;
                return Array.IndexOf(DeniedWin32Codes, code) >= 0 ? WriteProbe.Denied : WriteProbe.UnknownIoError;
            }

            var msg = ioex.Message ?? string.Empty;
            bool looksLikeDenied = msg.Contains("доступ", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("access", StringComparison.OrdinalIgnoreCase);
            return looksLikeDenied ? WriteProbe.Denied : WriteProbe.UnknownIoError;
        }

        private static Brush? Resource(FrameworkElement owner, string key) => owner.TryFindResource(key) as Brush;

        private static void ApplyStyle(FrameworkElement owner, FrameworkElement target, string key) {
            if (owner.TryFindResource(key) is Style style) {
                target.Style = style;
            }
            else {
                Logging.Logger.Warn($"HomeDialogs: стиль '{key}' не найден, используется оформление по умолчанию");
            }
        }
    }
}
