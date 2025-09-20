using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using ChillHub.Core;

namespace ChillHub.Pages
{
    public partial class CatalogPage : Page
    {
        public CatalogPage()
        {
            InitializeComponent();
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            using var http = new HttpClient();
            try
            {
                var resp = await http.GetFromJsonAsync<GamesResponse>($"{ConfigService.Current.ApiBaseUrl}/api/games");
                GamesList.ItemsSource = resp?.Items ?? new List<GameInfo>();
            }
            catch
            {
                // Пустой список, если недоступно API
                GamesList.ItemsSource = new List<GameInfo>();
            }
        }
    }
}
