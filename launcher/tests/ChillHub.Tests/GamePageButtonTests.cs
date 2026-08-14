// <copyright file="GamePageButtonTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Controls;

    using ChillHub.Core;
    using ChillHub.Core.Maintenance;
    using ChillHub.Pages;

    using Xunit;

    /// <summary>
    /// Кнопки страницы игры: доступность после закачки и подпись после техработ.
    /// <para>
    /// Оба сценария живут в code-behind <c>GamePage.xaml.cs</c>, а не в вынесенной чистой
    /// логике (<c>GameStateResolver</c>),
    /// поэтому чистыми юнит-тестами их не закрыть — баг был именно в том, КАК страница
    /// вызывает уже корректную логику. Страница поднимается по-настоящему на выделенном
    /// STA-потоке с диспетчером (<see cref="UiThread"/>), сеть отрезана заведомо мёртвым
    /// петлевым адресом (страница ловит собственные сетевые сбои и не падает), приватные
    /// методы вызываются рефлексией — этот приём уже используется в проекте
    /// (см. <c>FeedbackServiceTests</c>, <c>ErrorReporterAutoReportTests</c>).
    /// </para>
    /// </summary>
    [Collection(ConfigStorageCollection.Name)]
    public class GamePageButtonTests {
        private static void InvokePrivate(GamePage page, string method, params object[] args)
            => typeof(GamePage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, args);

        private static void SetPrivateField(GamePage page, string field, object value)
            => typeof(GamePage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(page, value);

        private static GamePage NewPage(GameInfo game) {
            // Петлевой адрес без слушателя: разрешён IsAcceptableApiBaseUrl (loopback), но
            // запросы GamePage.InitAsync к списку сборок/changelog проваливаются мгновенно
            // (ECONNREFUSED, не таймаут) и глушатся страницей самостоятельно — реальная сеть
            // в тест не уходит.
            ConfigService.TrySave(new AppConfig { ApiBaseUrl = "http://127.0.0.1:1/", GamesPath = ConfigService.Current.GamesPath }, out _);
            return new GamePage(game);
        }

        /// <summary>
        /// B2: кнопка действия должна вернуться в норму по окончании техработ.
        /// <para>
        /// Раньше на изменение режима работ звался только <c>ApplyMaintenanceToButtons</c>,
        /// который умеет исключительно ЗАПРЕЩАТЬ. Он ничего не делает, когда работы уже не
        /// блокируют операцию, — поэтому подпись «Технические работы» и заблокированная
        /// кнопка переживали конец работ и держались до следующей другой операции.
        /// </para>
        /// </summary>
        [Fact]
        public void КнопкаДействияВосстанавливаетсяПослеТехработ() {
            using var cfgDir = new ConfigDirsScope();
            MaintenanceService.ResetForTests();
            try {
                UiThread.Run(async () => {
                    var game = new GameInfo { GameId = "probe-game", LatestVersion = "1.2.0", IsInstalled = false };
                    var page = NewPage(game);
                    var actionBtn = (Button)typeof(GamePage).GetField("ActionBtn", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

                    // Работы начались: сервер блокирует установку (game.IsInstalled == false).
                    MaintenanceService.HttpOverride = FakeHandler(enabled: true);
                    await MaintenanceService.RefreshNowAsync();

                    Assert.Equal("Технические работы", actionBtn.Content);
                    Assert.False(actionBtn.IsEnabled);

                    // Работы закончились.
                    MaintenanceService.HttpOverride = FakeHandler(enabled: false);
                    await MaintenanceService.RefreshNowAsync();

                    Assert.NotEqual("Технические работы", actionBtn.Content);
                    Assert.True(actionBtn.IsEnabled, "после конца техработ кнопка действия обязана снова стать доступной");
                });
            }
            finally {
                MaintenanceService.ResetForTests();
            }
        }

        private static HttpClient FakeHandler(bool enabled) {
            var body = enabled
                ? "{\"enabled\":true,\"reason\":\"Тест\",\"blocks\":{\"install\":true,\"update\":true,\"launch\":false}}"
                : "{\"enabled\":false,\"blocks\":{\"install\":false,\"update\":false,\"launch\":false}}";
            return new HttpClient(new StubHandler(body));
        }

        private sealed class StubHandler : HttpMessageHandler {
            private readonly string body;

            internal StubHandler(string body) => this.body = body;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(this.body, Encoding.UTF8, "application/json"),
                });
        }
    }
}
