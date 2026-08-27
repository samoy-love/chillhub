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

    using ChillHub.Core.Mods;

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
        /// Задаёт пользователю вопрос «да/нет». Отдельным швом — чтобы проверять логику
        /// выбора папки без живого <see cref="MessageBox"/>: модальное окно в тестовом
        /// прогоне повесило бы его насмерть.
        /// </summary>
        internal static Func<string, string, bool> AskYesNo { get; set; } = DefaultAskYesNo;

        /// <summary>Сообщает пользователю об ошибке. Шов того же назначения, что и <see cref="AskYesNo"/>.</summary>
        internal static Action<string, string> ShowError { get; set; } = DefaultShowError;

        /// <summary>
        /// Просит выбрать папку; null — пользователь отказался.
        /// Шов того же назначения, что и <see cref="AskYesNo"/>.
        /// </summary>
        internal static Func<string?> PickFolder { get; set; } = DefaultPickFolder;

        /// <summary>Возвращает показ диалогов к настоящим окнам.</summary>
        internal static void ResetDialogsForTests() {
            AskYesNo = DefaultAskYesNo;
            ShowError = DefaultShowError;
            PickFolder = DefaultPickFolder;
        }

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

                var content = BuildConfirmDeleteContent(owner, title, folderPath);
                wnd.Content = content.Root;

                bool result = false;
                content.CancelButton.Click += (s, e) => { result = false; wnd.DialogResult = false; };
                content.DeleteButton.Click += (s, e) => { result = true; wnd.DialogResult = true; };

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
        /// Собирает начинку окна вопроса. Отдельно от самого окна — потому что окно
        /// показывается модально: в прогоне тестов проверить содержимое вопроса можно,
        /// только не открывая его.
        /// </summary>
        internal static ConfirmDeleteContent BuildConfirmDeleteContent(FrameworkElement owner, string title, string folderPath) {
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

            return new ConfirmDeleteContent {
                Root = new Border {
                    CornerRadius = new CornerRadius(8),
                    Background = Resource(owner, "Brush.Surface") ?? new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                    BorderBrush = Resource(owner, "Brush.Border"),
                    BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(12),
                    Child = grid,
                },
                Question = tb1,
                FolderLine = tb2,
                CancelButton = cancelBtn,
                DeleteButton = deleteBtn,
            };
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
                if (!AskYesNo(question, "Нет доступа")) {
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
                var newPath = PickFolder();
                if (string.IsNullOrWhiteSpace(newPath)) {
                    return false;
                }

                if (ProbeWritable(newPath) == WriteProbe.Ok) {
                    cfg.GamesPath = newPath;
                    if (!ConfigService.TrySave(cfg, out var saveError)) {
                        // Иначе выбранная папка «примется» только до перезапуска
                        ShowError("Папка выбрана, но настройки сохранить не удалось: " + saveError, "Ошибка");
                    }

                    return true;
                }

                ShowError($"Нет доступа к выбранной папке: {newPath}", "Ошибка");
                return false;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, "HomeDialogs.TryPickNewGamesPath");
                return false;
            }
        }

        private static bool DefaultAskYesNo(string message, string caption)
            => MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        private static void DefaultShowError(string message, string caption)
            => MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);

        private static string? DefaultPickFolder() {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog {
                Description = "Выберите папку для игр",
                ShowNewFolderButton = true,
                SelectedPath = AppConfig.DefaultGamesPath(),
            };

            return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }

        internal enum WriteProbe {
            /// <summary>В папку можно писать.</summary>
            Ok,

            /// <summary>Доступ запрещён — есть смысл предложить другую папку.</summary>
            Denied,

            /// <summary>Иная IO-ошибка: на отказ в доступе не похоже, лучше не мешать пользователю.</summary>
            UnknownIoError,
        }

        /// <summary>
        /// Выясняет, можно ли писать в папку: создаёт её при необходимости и пробует
        /// записать туда временный файл.
        /// </summary>
        internal static WriteProbe ProbeWritable(string path) {
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
        internal static WriteProbe ClassifyIoFailure(IOException ioex) {
            if ((ioex.HResult & Win32FacilityMask) == Win32FacilityBits) {
                var code = ioex.HResult & 0xFFFF;
                return Array.IndexOf(DeniedWin32Codes, code) >= 0 ? WriteProbe.Denied : WriteProbe.UnknownIoError;
            }

            var msg = ioex.Message ?? string.Empty;
            bool looksLikeDenied = msg.Contains("доступ", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("access", StringComparison.OrdinalIgnoreCase);
            return looksLikeDenied ? WriteProbe.Denied : WriteProbe.UnknownIoError;
        }

        /// <summary>Начинка окна вопроса об удалении вместе с элементами, на которые вешаются обработчики.</summary>
        internal sealed class ConfirmDeleteContent {
            /// <summary>Корень разметки — то, что кладётся в окно.</summary>
            internal required Border Root { get; init; }

            /// <summary>Сам вопрос.</summary>
            internal required TextBlock Question { get; init; }

            /// <summary>Строка с папкой, которую собираются удалить.</summary>
            internal required TextBlock FolderLine { get; init; }

            /// <summary>Кнопка отказа.</summary>
            internal required Button CancelButton { get; init; }

            /// <summary>Кнопка подтверждения.</summary>
            internal required Button DeleteButton { get; init; }
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

    /// <summary>
    /// Каким показать пункт меню «Поставить моды в Steam-копию».
    /// </summary>
    /// <param name="Visible">Показывать ли пункт вообще.</param>
    /// <param name="Enabled">Можно ли по нему нажать.</param>
    /// <param name="Header">Готовая подпись пункта: с причиной, если он выключен.</param>
    /// <param name="Reason">Почему пункт выключен или скрыт; пусто, если он доступен.</param>
    internal sealed record SteamModsMenu(bool Visible, bool Enabled, string Header, string Reason);

    /// <summary>
    /// Тексты и решения для установки модпака в копию игры из Steam.
    /// <para>
    /// Отдельно от страницы, потому что проверять тут есть что: пункт меню, показанный
    /// не тем играм, ведёт в тупик, а подтверждение без имени найденной папки просит
    /// согласиться неизвестно на что — модпак пишется прямо в чужую установку Steam.
    /// Ни то ни другое не проверить через живое окно.
    /// </para>
    /// </summary>
    internal static class SteamModsInstall {
        /// <summary>Подпись пункта меню. Одна на разметку, обработчик и тесты.</summary>
        internal const string MenuTitle = "Поставить моды в Steam-копию";

        /// <summary>Заголовок окна подтверждения.</summary>
        internal const string ConfirmCaption = "Установка модов в Steam-копию";

        /// <summary>
        /// Значение <c>Tag</c> у пункта меню: по нему обработчик открытия меню находит
        /// пункт среди остальных. По подписи искать нельзя — она меняется вместе с причиной.
        /// </summary>
        internal const string MenuTag = "steam-mods";

        /// <summary>
        /// Решает, показывать ли пункт и активен ли он.
        /// <para>
        /// Игре без Steam AppID пункт не нужен вовсе: ставить моды некуда, и объяснять
        /// тут нечего. А вот игра со Steam-копией, но без опубликованного модпака —
        /// случай временный, и выключенный пункт с причиной честнее исчезнувшего:
        /// пользователь видит, что путь есть, просто ставить пока нечего.
        /// </para>
        /// </summary>
        /// <param name="mods">Настройки модов игры с сервера.</param>
        /// <returns>Что сделать с пунктом меню.</returns>
        internal static SteamModsMenu DecideMenuItem(ModsInfo? mods) {
            if (mods == null || string.IsNullOrWhiteSpace(mods.SteamAppId)) {
                return new SteamModsMenu(false, false, MenuTitle, "у игры нет модов для копии из Steam");
            }

            if (!mods.HasLatest) {
                const string reason = "модпак ещё не опубликован";
                return new SteamModsMenu(true, false, $"{MenuTitle} — {reason}", reason);
            }

            return new SteamModsMenu(true, true, MenuTitle, string.Empty);
        }

        /// <summary>
        /// Составляет вопрос перед установкой.
        /// <para>
        /// Папка называется полностью и намеренно: библиотек Steam бывает несколько, и
        /// человек должен увидеть, в какую именно копию сейчас запишут полтора гигабайта.
        /// Предупреждение про обновление игры в Steam — не формальность: Steam
        /// восстанавливает свои файлы поверх модов, и после его обновления игра с модами
        /// перестаёт запускаться до повторной установки модпака.
        /// </para>
        /// </summary>
        /// <param name="gameTitle">Название игры.</param>
        /// <param name="mods">Настройки модов игры.</param>
        /// <param name="steamDir">Найденная папка копии из Steam.</param>
        /// <returns>Текст вопроса.</returns>
        internal static string BuildConfirmText(string? gameTitle, ModsInfo? mods, string? steamDir) {
            var title = string.IsNullOrWhiteSpace(gameTitle) ? "игры" : $"«{gameTitle}»";
            var pack = mods?.Describe() ?? string.Empty;
            var packLine = string.IsNullOrWhiteSpace(pack) ? string.Empty : $"Модпак: {pack}\n";

            return $"Поставить моды в копию {title} из Steam?\n\n" +
                   $"Папка: {HomeFormat.NormalizeDisplayPath(steamDir ?? string.Empty)}\n" +
                   packLine +
                   "\nМоды будут записаны прямо в установку Steam. Обновление игры в Steam " +
                   "может сломать их — после такого обновления поставьте моды заново.";
        }

        /// <summary>
        /// Объясняет по-человечески, почему копию в Steam не нашли.
        /// <para>
        /// Каждая ступень поиска — своя причина и свой следующий шаг. «Ошибка» здесь
        /// бесполезна: не установлен Steam, не установлена игра и «папку унесли с диска»
        /// лечатся совершенно по-разному.
        /// </para>
        /// </summary>
        /// <param name="outcome">Чем закончился поиск.</param>
        /// <param name="gameTitle">Название игры для текста.</param>
        /// <returns>Текст для пользователя; пусто, если папка всё-таки нашлась.</returns>
        internal static string DescribeLookupFailure(SteamLookup outcome, string? gameTitle) {
            var title = string.IsNullOrWhiteSpace(gameTitle) ? "Игра" : $"«{gameTitle}»";
            return outcome switch {
                SteamLookup.Found => string.Empty,
                SteamLookup.SteamNotInstalled =>
                    "Steam на этом компьютере не найден: в реестре нет пути к нему. " +
                    "Установите Steam или запустите его хотя бы один раз.",
                SteamLookup.NoLibraries =>
                    "Библиотеки Steam не найдены — похоже, в эту установку Steam ещё ничего не скачано.",
                SteamLookup.GameNotInstalled =>
                    $"{title} не установлена в Steam. Установите её в Steam и повторите.",
                SteamLookup.FolderMissing =>
                    $"Steam считает, что {title} установлена, но папки игры на диске нет. " +
                    "Проверьте целостность файлов игры в Steam.",
                SteamLookup.NoAppId =>
                    "Для этой игры не задан Steam AppID — искать копию в Steam не по чему.",
                _ => "Копию игры в Steam найти не удалось. Подробности — в журнале.",
            };
        }

        /// <summary>
        /// Переводит итог установки в строку для пользователя.
        /// </summary>
        /// <param name="result">Что вернул <see cref="ModsService.EnsureAsync"/>.</param>
        /// <param name="gameTitle">Название игры.</param>
        /// <returns>Текст для тоста или для сообщения об ошибке.</returns>
        internal static string DescribeResult(ModsSyncResult result, string? gameTitle) {
            var title = string.IsNullOrWhiteSpace(gameTitle) ? "игры" : $"«{gameTitle}»";
            if (result == null) {
                return "Не удалось установить моды. Попробуйте ещё раз.";
            }

            switch (result.Outcome) {
                case ModsSyncOutcome.NoModpack:
                    return $"У {title} нет активного модпака — устанавливать нечего.";
                case ModsSyncOutcome.UpToDate:
                    return $"Моды в копии {title} из Steam уже актуальны.";
                case ModsSyncOutcome.Installed:
                    // Объём скачанного называется, потому что установка «мгновенно и
                    // молча» после полутора гигабайт трафика выглядит как отказ.
                    var size = result.Downloaded > 0 ? $", скачано {HomeFormat.FormatSize(result.Downloaded)}" : string.Empty;
                    return $"Моды установлены в копию {title} из Steam: {result.Version}{size}.";
                default:
                    return string.IsNullOrWhiteSpace(result.Message)
                        ? "Не удалось установить моды. Попробуйте ещё раз."
                        : result.Message;
            }
        }
    }
}
