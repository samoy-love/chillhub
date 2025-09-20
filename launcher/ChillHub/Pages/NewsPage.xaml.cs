using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using ChillHub.Core;
using ChillHub.Core.Net;

namespace ChillHub.Pages
{
    public partial class NewsPage : Page
    {
        public NewsPage()
        {
            InitializeComponent();
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            var http = HttpClientProvider.Shared;
            try
            {
                var resp = await http.GetFromJsonAsync<NewsIndex>($"{ConfigService.Current.ApiBaseUrl}/news/index.json");
                NewsList.ItemsSource = resp?.Items ?? new List<NewsItem>();
            }
            catch
            {
                NewsList.ItemsSource = new List<NewsItem>();
            }
        }
    }
}
