// <copyright file="ImageClientIdentityTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Linq;
    using System.Net.Http;

    using ChillHub.Core.Home;
    using ChillHub.Core.Net;

    using Xunit;

    /// <summary>
    /// Чем загрузчик картинок представляется серверу.
    /// <para>
    /// Сайт стоит за Cloudflare, и запросы без User-Agent тот роняет молча: соединение
    /// повисает до таймаута, а не отвечает ошибкой. Клиент картинок заводится в обход
    /// общего провайдера, заголовок ему никто не ставил — и обложки в ленте пропадали
    /// примерно на трети запросов, причём каждый раз на разных.
    /// </para>
    /// </summary>
    [Collection(ImageLoaderCollection.Name)]
    public class ImageClientIdentityTests : IDisposable {
        public ImageClientIdentityTests() => ImageLoader.ResetForTests();

        public void Dispose() => ImageLoader.ResetForTests();

        /// <summary>Клиент по умолчанию обязан называть себя — иначе картинки не доедут.</summary>
        [Fact]
        public void КлиентКартинокПредставляетсяЛаунчером() {
            var ua = ImageLoader.Http.DefaultRequestHeaders.UserAgent.ToString();

            Assert.False(string.IsNullOrWhiteSpace(ua));
            Assert.Contains("ChillHub", ua, StringComparison.Ordinal);
        }

        /// <summary>
        /// То же и у общего клиента: провайдер и загрузчик картинок должны представляться
        /// одинаково, иначе правку в одном месте молча потеряет другое.
        /// </summary>
        [Fact]
        public void ОбщийКлиентИКлиентКартинокПредставляютсяОдинаково() {
            var shared = HttpClientProvider.Shared.DefaultRequestHeaders.UserAgent.ToString();
            var images = ImageLoader.Http.DefaultRequestHeaders.UserAgent.ToString();

            Assert.Equal(shared, images);
        }

        /// <summary>Заголовок ставится и на клиент, заведённый на стороне.</summary>
        [Fact]
        public void ЧужомуКлиентуПровайдерТожеПроставляетЗаголовок() {
            using var http = new HttpClient();

            HttpClientProvider.ApplyIdentity(http);

            // Строка с комментарием разбирается на две части — продукт и комментарий,
            // поэтому сверяется склейка, а не число элементов.
            Assert.Equal(HttpClientProvider.UserAgent, http.DefaultRequestHeaders.UserAgent.ToString());
        }
    }
}
