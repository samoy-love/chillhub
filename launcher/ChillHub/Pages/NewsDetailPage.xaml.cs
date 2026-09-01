// <copyright file="NewsDetailPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Drawing;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    using ChillHub.Core.News;

    // single-theme: no need to read app theme here
    using Microsoft.Web.WebView2.Core;

    public partial class NewsDetailPage : Page {
        // Окружение WebView2 создаётся один раз на процесс: два окружения с разными
        // папками данных в одном процессе создать нельзя.
        private static readonly SemaphoreSlim EnvGate = new SemaphoreSlim(1, 1);
        private static CoreWebView2Environment? sharedEnvironment;

        private readonly string markdownUrl;

        /// <summary>Название из шапки: им же отсекается дублирующий заголовок в тексте.</summary>
        private readonly string newsTitle;

        // Общий клиент вместо своего на каждую страницу: свой не диспозился и держал сокеты
        private readonly NewsContentClient content = new NewsContentClient(ChillHub.Core.Net.HttpClientProvider.Shared);

        /// <summary>Страница уже освободила WebView2 — повторно им пользоваться нельзя.</summary>
        private bool browserReleased;

        /// <summary>Переходы этой страницы: отличает свою отрисовку от ссылки в тексте.</summary>
        private readonly NewsNavigationGate gate = new NewsNavigationGate();

        public NewsDetailPage(string title, string markdownUrl) {
            this.InitializeComponent();
            this.TitleText.Text = title;
            this.newsTitle = title;
            this.markdownUrl = markdownUrl;

            // WebView2 держит собственный процесс msedgewebview2.exe. Без освобождения
            // десяток прочитанных новостей оставляет десяток процессов до выхода из лаунчера.
            this.Unloaded += this.NewsDetailPage_Unloaded;
            this.Loaded += this.NewsDetailPage_Loaded;

            // Prevent white flash: set WebView2 background to app dark color before init
            try {
                var bg = GetMediaColor("Brush.Background");
                this.Browser.DefaultBackgroundColor = ToDrawingColor(bg);
            }
            catch { /* safe guard in case property isn't available at runtime */
            }

            _ = this.LoadAsync();
        }

        /// <summary>
        /// Разовая уборка каталога данных WebView2, оставшегося в папке установки
        /// от версий лаунчера без явного UserDataFolder.
        /// </summary>
        internal static void CleanupLegacyUserDataFolder() => NewsWebViewStorage.CleanupLegacyUserDataFolder();

        private static async Task<CoreWebView2Environment> GetEnvironmentAsync() {
            var existing = sharedEnvironment;
            if (existing != null) {
                return existing;
            }

            await EnvGate.WaitAsync();
            try {
                if (sharedEnvironment != null) {
                    return sharedEnvironment;
                }

                CleanupLegacyUserDataFolder();
                var folder = NewsWebViewStorage.GetUserDataFolder();
                Directory.CreateDirectory(folder);
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, folder, null);
                return sharedEnvironment;
            }
            finally {
                EnvGate.Release();
            }
        }

        /// <summary>
        /// Освобождает WebView2 вместе с его процессом. Вызывается при уходе со страницы:
        /// новость — лист навигации, назад к ней не возвращаются.
        /// </summary>
        private void NewsDetailPage_Unloaded(object sender, RoutedEventArgs e) {
            if (this.browserReleased) {
                return;
            }

            this.browserReleased = true;
            try {
                this.Browser?.Dispose();
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Warn($"NewsDetailPage: освободить WebView2 не удалось: {ex.Message}");
            }
        }

        // Страховка на случай возврата вперёд по журналу к уже освобождённой странице
        private void NewsDetailPage_Loaded(object sender, RoutedEventArgs e) {
            if (this.browserReleased) {
                this.ShowFallbackError("Просмотр новости был закрыт. Откройте новость заново из списка.");
            }
        }

        private async Task LoadAsync() {
            // Окружение поднимаем до навигации: после инициализации WebView2
            // сменить UserDataFolder уже нельзя.
            try {
                var env = await GetEnvironmentAsync();
                if (this.browserReleased) {
                    return; // пользователь ушёл со страницы, пока поднималось окружение
                }

                await this.Browser.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex) {
                try {
                    ChillHub.Core.Logging.Logger.Error(ex, "NewsDetailPage: инициализация WebView2");
                }
                catch {
                }

                // Без CoreWebView2 недоступен и NavigateToString — показываем сообщение средствами WPF
                this.ShowFallbackError(
                    "Компонент просмотра (WebView2) не запустился. Возможно, не установлен Microsoft Edge WebView2 Runtime.\n\n"
                    + ex.Message);
                return;
            }

            // Pull colors from Theme.Dark.xaml brushes
            var palette = BuildPalette();
            try {
                // Loader removed: directly fetch and render content
                var md = await this.content.FetchAsync(this.markdownUrl);
                if (this.browserReleased) {
                    return; // страница закрыта, пока грузился markdown
                }

                var page = NewsPageRenderer.RenderPage(md, this.markdownUrl, palette, this.newsTitle);

                // Картинки вкладываются в страницу, а не подгружаются: у NavigateToString
                // origin about:blank, и подгрузить по нему картинку такой странице не
                // дают — на экране оставались пустые места. Байты берутся тем же
                // загрузчиком, что и обложки новостей на главном экране: общий кеш на
                // диске и содержимое с него, когда сети нет.
                page = await NewsImages.InlineAsync(
                    page,
                    NewsPageRenderer.OriginOf(this.markdownUrl),
                    Core.Home.ImageLoader.FetchBytesAsync);
                if (this.browserReleased) {
                    return; // страница закрыта, пока подтягивались картинки
                }

                // Ensure runtime background and wire events
                try {
                    var bgCol = GetMediaColor("Brush.Background");
                    this.Browser.DefaultBackgroundColor = ToDrawingColor(bgCol);
                }
                catch {
                }
                try {
                    // Подписки на DOMContentLoaded, NavigationCompleted и WebMessageReceived
                    // убраны: их обработчики были пустыми. Они остались от индикатора загрузки,
                    // который гасился по этим событиям, — сам индикатор удалён давно, а
                    // подписки пережили его и создавали видимость, будто страница чего-то ждёт
                    // от содержимого. Ждать нечего: страница новости статична.

                    // Ссылки уходят наружу, а сам WebView остаётся на странице новости:
                    // в окне лаунчера нет ни адресной строки, ни кнопки «назад», и
                    // ушедший по ссылке WebView оставил бы игрока на чужом сайте без
                    // единого признака, что он уже не в лаунчере. Что именно можно
                    // отдать оболочке, решает NewsLinkPolicy: обычный клик поднимает
                    // NavigationStarting, клик с новым окном — NewWindowRequested,
                    // и оба должны судить одинаково.
                    this.Browser.CoreWebView2.NavigationStarting += (s, ev) => {
                        // Своя отрисовка узнаётся по метке, а не по адресу: адрес нашей
                        // страницы придумывает сам движок, и его вид меняется от версии
                        // к версии. Порядок «пометили — пришёл переход — метка снялась»
                        // держит NewsNavigationGate, и он же проверяется тестами.
                        Apply(this.gate.OnNavigationStarting(ev.Uri), ev.Uri, c => ev.Cancel = c);
                    };

                    this.Browser.CoreWebView2.NewWindowRequested += (s, ev) => {
                        ev.Handled = true;
                        Apply(this.gate.OnNewWindowRequested(ev.Uri), ev.Uri, _ => { });
                    };
                }
                catch {
                }

                this.NavigateToOwnPage(page);
            }
            catch (Exception ex) {
                try {
                    this.NavigateToOwnPage(NewsPageRenderer.RenderError(ex.Message, palette));
                }
                catch {
                    // Даже отрисовать ошибку не вышло — уходим в запасную панель WPF
                    this.ShowFallbackError("Не удалось загрузить новость.\n\n" + ex.Message);
                }
            }
        }

        /// <summary>
        /// Исполняет решение: отменяет переход, пишет след в журнал и отдаёт адрес оболочке.
        /// <para>
        /// След обязателен: отменённый переход, которым оказалась сама новость, выглядит
        /// на экране просто пустотой, и объяснить её потом было нечем — ровно так этот
        /// обработчик однажды и погасил страницу целиком.
        /// </para>
        /// </summary>
        /// <param name="action">Решение шлюза.</param>
        /// <param name="uri">Адрес из события — только для журнала.</param>
        /// <param name="cancel">Как сообщить отмену событию.</param>
        private static void Apply(NewsNavigationAction action, string? uri, Action<bool> cancel) {
            cancel(action.Cancel);
            if (action.Cancel) {
                try {
                    Core.Logging.Logger.Info(
                        $"NewsDetailPage: переход отменён, адрес '{uri}', наружу={action.OpenExternally != null}");
                }
                catch {
                }
            }

            OpenOutside(action.OpenExternally);
        }

        /// <summary>
        /// Показывает собственную страницу, пометив её переход как свой.
        /// <para>
        /// Единственный путь к <c>NavigateToString</c> на этой странице: обработчик
        /// перехода отменяет всё, что не разрешила политика, и без пометки он отменял
        /// бы и саму новость — на экране оставалась пустота.
        /// </para>
        /// </summary>
        /// <param name="html">Готовая страница.</param>
        private void NavigateToOwnPage(string html) {
            this.gate.BeginOwnPageLoad();
            this.Browser.NavigateToString(html);
        }

        /// <summary>
        /// Отдаёт адрес системному браузеру — и только тот, который разрешила политика.
        /// </summary>
        /// <param name="uri">Адрес, который политика разрешила отдать; null — не отдавать.</param>
        private static void OpenOutside(string? uri) {
            if (string.IsNullOrWhiteSpace(uri)) {
                return;
            }

            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = uri,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) {
                try {
                    ChillHub.Core.Logging.Logger.Warn($"NewsDetailPage: открыть ссылку не удалось: {ex.Message}");
                }
                catch {
                }
            }
        }

        // Показывает сообщение об ошибке средствами WPF, когда WebView2 недоступен.
        private void ShowFallbackError(string message) {
            try {
                if (this.ErrorText != null) {
                    this.ErrorText.Text = message;
                }

                if (this.ErrorPanel != null) {
                    this.ErrorPanel.Visibility = Visibility.Visible;
                }

                if (this.Browser != null) {
                    this.Browser.Visibility = Visibility.Collapsed;
                }
            }
            catch {
            }
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e) {
            // Возвращаемся по стеку навигации, чтобы сохранить состояние HomePage (включая выбранную игру)
            if (this.NavigationService?.CanGoBack == true) {
                this.NavigationService.GoBack();
                return;
            }

            // Fallback: если по какой-то причине стека нет — открываем главную,
            // переиспользуя единственный экземпляр страницы
            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            win?.NavigateToHome();
        }

        /// <summary>Собирает цвета страницы новости из кистей темы.</summary>
        private static NewsPalette BuildPalette() => new NewsPalette(
            Background: BrushToCss("Brush.Background", "#0F1116"),
            Text: BrushToCss("Brush.Text", "#E5E5E5"),
            CodeBackground: BrushToCss("Brush.Surface", "#171B24"),
            Link: BrushToCss("Brush.Accent", "#EF4444"),
            LinkHover: BrushToCss("Brush.AccentHover", "#DC2626"),
            HorizontalRule: BrushToCss("Brush.Border", "#262626"),
            Surface: BrushToCss("Brush.Surface", "#0B0B0B"),
            ScrollThumb: BrushToCss("Brush.ScrollbarThumb", BrushToCss("Brush.ListHover", "#2E2E2E")),
            ScrollThumbHover: BrushToCss("Brush.ScrollbarThumbHover", BrushToCss("Brush.ListHoverAlt", "#474747")));

        private static string BrushToCss(string key, string fallback) {
            try {
                var brush = Application.Current?.Resources[key] as SolidColorBrush;
                if (brush != null) {
                    var c = brush.Color;
                    return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                }
            }
            catch {
            }
            return fallback;
        }

        private static System.Windows.Media.Color GetMediaColor(string key) {
            var brush = Application.Current?.Resources[key] as SolidColorBrush;
            if (brush != null) {
                return brush.Color;
            }

            // Fallback to a safe dark bg if not found
            return System.Windows.Media.Color.FromRgb(0x0F, 0x11, 0x16);
        }

        private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color c) {
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
    }
}
