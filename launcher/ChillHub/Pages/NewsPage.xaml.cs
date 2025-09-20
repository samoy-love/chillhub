// <copyright file="NewsPage.xaml.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Pages
{
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading.Tasks;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.Net;

    public partial class NewsPage : Page
    {
        public NewsPage()
        {
            this.InitializeComponent();
            _ = this.LoadAsync();
        }

        private async Task LoadAsync()
        {
            var http = HttpClientProvider.Shared;
            try
            {
                var resp = await http.GetFromJsonAsync<NewsIndex>($"{ConfigService.Current.ApiBaseUrl}/news/index.json");
                this.NewsList.ItemsSource = resp?.Items ?? new List<NewsItem>();
            }
            catch
            {
                this.NewsList.ItemsSource = new List<NewsItem>();
            }
        }
    }
}
