// <copyright file="NewsDetailPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System;
    using System.Drawing;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    using Markdig;

    // single-theme: no need to read app theme here
    using Microsoft.Web.WebView2.Core;

    public partial class NewsDetailPage : Page {
        // Окружение WebView2 создаётся один раз на процесс: два окружения с разными
        // папками данных в одном процессе создать нельзя.
        private static readonly SemaphoreSlim EnvGate = new SemaphoreSlim(1, 1);
        private static CoreWebView2Environment? sharedEnvironment;
        private static bool legacyFolderCleaned;

        private readonly string markdownUrl;
        private readonly HttpClient http = new HttpClient();

        public NewsDetailPage(string title, string markdownUrl) {
            this.InitializeComponent();
            this.TitleText.Text = title;
            this.markdownUrl = markdownUrl;

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
        /// Каталог данных WebView2 (кеш, куки, localStorage).
        /// Обязательно вне папки установки: по умолчанию WebView2 кладёт
        /// "ChillHub.exe.WebView2" рядом с exe, а самообновление сносит всё,
        /// чего нет в манифесте, — вместе с этим каталогом.
        /// </summary>
        /// <returns>Полный путь к каталогу данных WebView2.</returns>
        private static string GetUserDataFolder() {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "ChillHub", "WebView2");
        }

        /// <summary>
        /// Разовая уборка каталога данных WebView2, оставшегося в папке установки
        /// от версий лаунчера без явного UserDataFolder.
        /// </summary>
        internal static void CleanupLegacyUserDataFolder() {
            if (legacyFolderCleaned) {
                return;
            }

            legacyFolderCleaned = true;
            try {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var exeName = Path.GetFileName(Environment.ProcessPath) ?? "ChillHub.exe";
                foreach (var candidate in new[] { exeName + ".WebView2", "ChillHub.exe.WebView2" }) {
                    var legacy = Path.Combine(baseDir, candidate);
                    if (!Directory.Exists(legacy)) {
                        continue;
                    }

                    try {
                        Directory.Delete(legacy, recursive: true);
                        ChillHub.Core.Logging.Logger.Info($"WebView2: удалён старый каталог данных '{legacy}'");
                    }
                    catch (Exception ex) {
                        // Каталог мог остаться залоченным — не критично, попробуем в следующий раз
                        ChillHub.Core.Logging.Logger.Warn($"WebView2: не удалось удалить '{legacy}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, "NewsDetailPage.CleanupLegacyUserDataFolder");
            }
        }

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
                var folder = GetUserDataFolder();
                Directory.CreateDirectory(folder);
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, folder, null);
                return sharedEnvironment;
            }
            finally {
                EnvGate.Release();
            }
        }

        private async Task LoadAsync() {
            // Окружение поднимаем до навигации: после инициализации WebView2
            // сменить UserDataFolder уже нельзя.
            try {
                var env = await GetEnvironmentAsync();
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

            try {
                // Loader removed: directly fetch and render content
                var md = await this.http.GetStringAsync(this.markdownUrl);
                var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
                var html = Markdown.ToHtml(md, pipeline);

                // Ensure absolute URLs like "/assets/..." resolve correctly in WebView2 when using NavigateToString
                // We inject a <base> tag with the origin derived from the markdown URL.
                var origin = new Uri(this.markdownUrl).GetLeftPart(UriPartial.Authority);

                // Pull colors from Theme.Dark.xaml brushes
                string bg = BrushToCss("Brush.Background", "#0F1116");
                string text = BrushToCss("Brush.Text", "#E5E5E5");
                string codeBg = BrushToCss("Brush.Surface", "#171B24");
                string link = BrushToCss("Brush.Accent", "#EF4444");
                string linkHover = BrushToCss("Brush.AccentHover", "#DC2626");
                string hr = BrushToCss("Brush.Border", "#262626");
                string surface = BrushToCss("Brush.Surface", "#0B0B0B");
                string scrollThumb = BrushToCss("Brush.ScrollbarThumb", BrushToCss("Brush.ListHover", "#2E2E2E"));
                string scrollThumbHover = BrushToCss("Brush.ScrollbarThumbHover", BrushToCss("Brush.ListHoverAlt", "#474747"));
                var page = $@"<html><head><meta charset='utf-8'><base href='{origin}/'>
<style>
  html,body{{height:100%; overflow-x:hidden;}}
  body{{font-family:Segoe UI,Segoe UI Emoji,Arial; margin:0; color:{text}; background:{bg}; overflow-x:hidden;}}
  .wrap{{width:min(100vw - 24px, 860px); margin:16px auto 28px auto; padding:16px; font-size:17px; line-height:1.7; background:{surface}; border-radius:8px;}}
  img{{max-width:100%; height:auto; max-height:360px; display:inline-block; margin:16px 0; border-radius:8px;}}
  pre,code{{background:{codeBg}; border-radius:6px;}}
  pre{{padding:12px; overflow:auto;}}
  a{{color:{link}; text-decoration:none;}}
  a:hover{{color:{linkHover}; text-decoration:underline;}}
  h1{{font-size:24px; margin:20px 0 10px 0;}}
  h2{{font-size:21px; margin:18px 0 8px 0;}}
  h3{{font-size:19px; margin:16px 0 6px 0;}}
  hr{{border:none; border-top:1px solid {hr}; margin:20px 0;}}
  /* Themed scrollbars for WebView2 (Chromium) */
  ::-webkit-scrollbar{{ width:8px; height:8px; }}
  ::-webkit-scrollbar-track{{ background: transparent; }}
  ::-webkit-scrollbar-thumb{{ background: {scrollThumb}; border-radius:8px; }}
  ::-webkit-scrollbar-thumb:hover{{ background: {scrollThumbHover}; }}
</style></head><body><div class='wrap'>{html}</div>
<script>
  // Signal when everything is loaded (including images)
  window.addEventListener('load', function(){{
    try {{ chrome.webview.postMessage('loaded'); }} catch(e) {{}}
  }});
</script>
</body></html>";
                // Ensure runtime background and wire events
                try {
                    var bgCol = GetMediaColor("Brush.Background");
                    this.Browser.DefaultBackgroundColor = ToDrawingColor(bgCol);
                }
                catch {
                }
                try {
                    // Loader removed: no need to toggle overlay on events
                    this.Browser.CoreWebView2.DOMContentLoaded += (_, __) => { };
                    this.Browser.CoreWebView2.NavigationCompleted += (_, __) => { };
                    this.Browser.CoreWebView2.WebMessageReceived += (_, e) => {
                        // Loader removed: ignore 'loaded' message; read once to avoid warnings
                        e.TryGetWebMessageAsString();
                    };

                    // Открывать внешние ссылки во внешнем браузере
                    this.Browser.CoreWebView2.NewWindowRequested += (s, ev) => {
                        try {
                            ev.Handled = true;
                            var uri = ev.Uri;
                            if (!string.IsNullOrWhiteSpace(uri)) {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                                    FileName = uri,
                                    UseShellExecute = true,
                                });
                            }
                        }
                        catch {
                        }
                    };
                }
                catch {
                }

                this.Browser.NavigateToString(page);
            }
            catch (Exception ex) {
                try {
                    this.Browser.NavigateToString($"<html><body style='background:{BrushToCss("Brush.Background", "#0F1116")};color:{BrushToCss("Brush.Text", "#E5E5E5")};font-family:Segoe UI,Arial'><p>Не удалось загрузить новость: {System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>");
                }
                catch {
                    // Даже отрисовать ошибку не вышло — уходим в запасную панель WPF
                    this.ShowFallbackError("Не удалось загрузить новость.\n\n" + ex.Message);
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

            // Fallback: если по какой-то причине стека нет, открываем новый HomePage
            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            win?.ContentFrame.Navigate(new HomePage());
        }

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
