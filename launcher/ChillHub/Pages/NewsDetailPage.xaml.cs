using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Markdig;
// single-theme: no need to read app theme here
using Microsoft.Web.WebView2.Core;
using System.Drawing;
using System.Windows.Media;

namespace ChillHub.Pages
{
    public partial class NewsDetailPage : Page
    {
        private readonly string _markdownUrl;
        private readonly HttpClient _http = new HttpClient();

        public NewsDetailPage(string title, string markdownUrl)
        {
            InitializeComponent();
            TitleText.Text = title;
            _markdownUrl = markdownUrl;
            // Prevent white flash: set WebView2 background to app dark color before init
            try
            {
                var bg = GetMediaColor("Brush.Background");
                Browser.DefaultBackgroundColor = ToDrawingColor(bg);
            }
            catch { /* safe guard in case property isn't available at runtime */ }

            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                // Loader removed: directly fetch and render content

                var md = await _http.GetStringAsync(_markdownUrl);
                var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
                var html = Markdown.ToHtml(md, pipeline);
                // Ensure absolute URLs like "/assets/..." resolve correctly in WebView2 when using NavigateToString
                // We inject a <base> tag with the origin derived from the markdown URL.
                var origin = new Uri(_markdownUrl).GetLeftPart(UriPartial.Authority);
                // Pull colors from Theme.Dark.xaml brushes
                string bg = BrushToCss("Brush.Background", "#0F1116");
                string text = BrushToCss("Brush.Text", "#E5E5E5");
                string codeBg = BrushToCss("Brush.Surface", "#171B24");
                string link = BrushToCss("Brush.Accent", "#EF4444");
                string linkHover = BrushToCss("Brush.AccentHover", "#DC2626");
                string hr = BrushToCss("Brush.Border", "#262626");
                string scrollThumb = BrushToCss("Brush.ScrollbarThumb", BrushToCss("Brush.ListHover", "#2E2E2E"));
                string scrollThumbHover = BrushToCss("Brush.ScrollbarThumbHover", BrushToCss("Brush.ListHoverAlt", "#474747"));
                var page = $@"<html><head><meta charset='utf-8'><base href='{origin}/'>
<style>
  html,body{{height:100%; overflow-x:hidden;}}
  body{{font-family:Segoe UI,Segoe UI Emoji,Arial; margin:0; color:{text}; background:{bg}; overflow-x:hidden;}}
  .wrap{{width:min(100vw - 24px, 860px); margin:16px auto 28px auto; padding:0 12px; font-size:17px; line-height:1.7;}}
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
                await Browser.EnsureCoreWebView2Async();

                // Ensure runtime background and wire events
                try
                {
                    var bgCol = GetMediaColor("Brush.Background");
                    Browser.DefaultBackgroundColor = ToDrawingColor(bgCol);
                }
                catch {}
                try
                {
                    // Loader removed: no need to toggle overlay on events
                    Browser.CoreWebView2.DOMContentLoaded += (_, __) => { };
                    Browser.CoreWebView2.NavigationCompleted += (_, __) => { };
                    Browser.CoreWebView2.WebMessageReceived += (_, e) =>
                    {
                        // Loader removed: ignore 'loaded' message; read once to avoid warnings
                        e.TryGetWebMessageAsString();
                    };
                    // Открывать внешние ссылки во внешнем браузере
                    Browser.CoreWebView2.NewWindowRequested += (s, ev) =>
                    {
                        try
                        {
                            ev.Handled = true;
                            var uri = ev.Uri;
                            if (!string.IsNullOrWhiteSpace(uri))
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = uri,
                                    UseShellExecute = true
                                });
                            }
                        }
                        catch { }
                    };
                }
                catch { }

                Browser.NavigateToString(page);
            }
            catch (Exception ex)
            {
                try
                {
                    Browser.NavigateToString($"<html><body><p>Не удалось загрузить новость: {System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>");
                }
                finally { }
            }
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e)
        {
            // Возвращаемся по стеку навигации, чтобы сохранить состояние HomePage (включая выбранную игру)
            if (NavigationService?.CanGoBack == true)
            {
                NavigationService.GoBack();
                return;
            }
            // Fallback: если по какой-то причине стека нет, открываем новый HomePage
            var win = Window.GetWindow(this) as ChillHub.MainWindow;
            win?.ContentFrame.Navigate(new HomePage());
        }

        private static string BrushToCss(string key, string fallback)
        {
            try
            {
                var brush = Application.Current?.Resources[key] as SolidColorBrush;
                if (brush != null)
                {
                    var c = brush.Color;
                    return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                }
            }
            catch { }
            return fallback;
        }

        private static System.Windows.Media.Color GetMediaColor(string key)
        {
            var brush = Application.Current?.Resources[key] as SolidColorBrush;
            if (brush != null)
            {
                return brush.Color;
            }
            // Fallback to a safe dark bg if not found
            return System.Windows.Media.Color.FromRgb(0x0F, 0x11, 0x16);
        }

        private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color c)
        {
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
    }
}
