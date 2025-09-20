// <copyright file="CatalogPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages {
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading.Tasks;
    using System.Windows.Controls;

    using ChillHub.Core;

    public partial class CatalogPage : Page {
        public CatalogPage() {
            this.InitializeComponent();
            _ = this.LoadAsync();
        }

        private async Task LoadAsync() {
            using var http = new HttpClient();
            try {
                var resp = await http.GetFromJsonAsync<GamesResponse>($"{ConfigService.Current.ApiBaseUrl}/api/games");
                this.GamesList.ItemsSource = resp?.Items ?? new List<GameInfo>();
            }
            catch {
                // Пустой список, если недоступно API
                this.GamesList.ItemsSource = new List<GameInfo>();
            }
        }
    }
}
