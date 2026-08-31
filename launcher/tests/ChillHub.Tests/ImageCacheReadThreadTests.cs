// <copyright file="ImageCacheReadThreadTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// На каком потоке загрузчик картинок читает кеш с диска.
    /// <para>
    /// Картинку просит поток интерфейса, и фабрику <c>Inflight.GetOrAdd</c> он исполняет
    /// синхронно — всё тело загрузки до первого await идёт прямо на нём. Пока чтение
    /// кеша (File.Exists + ReadAllBytes + разбор json) стояло первой строкой, на список
    /// из полусотни игр приходилось полсотни таких чтений подряд, и на холодном диске
    /// «Обновить список игр» подвешивало окно.
    /// </para>
    /// </summary>
    [Collection(ImageLoaderCollection.Name)]
    public class ImageCacheReadThreadTests : IDisposable {
        private const string Url = "https://images.invalid/hero.png";
        private static readonly byte[] Payload = { 9, 8, 7 };

        public ImageCacheReadThreadTests() => ImageLoader.ResetForTests();

        public void Dispose() => ImageLoader.ResetForTests();

        /// <summary>Диск читается в пуле потоков, а не на том потоке, который попросил картинку.</summary>
        [Fact]
        public void ЧтениеДисковогоКешаНеИдётНаПотокеПросившего() {
            ImageLoader.Http = new HttpClient(new OfflineHandler());

            var callerThread = 0;
            var readThread = 0;
            var readOnPool = false;
            var bytes = Array.Empty<byte>();

            var caller = new Thread(() => {
                callerThread = Environment.CurrentManagedThreadId;
                bytes = ImageLoader.DownloadAsync(Url, _ => {
                    readThread = Environment.CurrentManagedThreadId;
                    readOnPool = Thread.CurrentThread.IsThreadPoolThread;
                    return new CachedImage(Payload, null, null);
                }).GetAwaiter().GetResult();
            }) {
                IsBackground = true,
            };

            caller.Start();
            Assert.True(caller.Join(TimeSpan.FromSeconds(30)), "загрузка картинки не уложилась в отведённое время");

            // Сеть недоступна — вернулось то, что лежало на диске: путь именно тот, что у игрока.
            Assert.Equal(Payload, bytes);
            Assert.NotEqual(callerThread, readThread);
            Assert.True(readOnPool, "чтение дискового кеша обязано уходить в пул потоков");
        }

        /// <summary>Сети нет вовсе — как на запуске без интернета.</summary>
        private sealed class OfflineHandler : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new HttpRequestException("сеть недоступна");
        }
    }
}
