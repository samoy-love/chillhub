// <copyright file="ImageLoaderSupport.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Все тесты загрузчика картинок идут в одной очереди.
    /// <para>
    /// Кеш, список идущих загрузок и HttpClient у <see cref="ImageLoader"/> статические:
    /// это одна картинка на всё приложение, и иначе быть не может. Но если xUnit пустит
    /// два таких класса параллельно, они начнут подменять друг другу сеть и чистить чужой
    /// кеш — прогон станет случайным.
    /// </para>
    /// </summary>
    [CollectionDefinition(ImageLoaderCollection.Name, DisableParallelization = true)]
    public class ImageLoaderCollection {
        /// <summary>Имя коллекции.</summary>
        public const string Name = "ImageLoader";
    }

    /// <summary>
    /// Подставная сеть: считает запросы и отдаёт заранее заданный ответ.
    /// Настоящих запросов тесты не делают — ни один адрес в них не существует.
    /// </summary>
    internal sealed class FakeImageHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        private readonly List<string> requested = new();
        private int calls;

        internal FakeImageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => this.respond = respond;

        /// <summary>Сколько раз обработчик реально сходил «в сеть».</summary>
        internal int Calls => Volatile.Read(ref this.calls);

        /// <summary>Адреса запросов в порядке поступления.</summary>
        internal IReadOnlyList<string> Requested {
            get {
                lock (this.requested) {
                    return this.requested.ToArray();
                }
            }
        }

        /// <summary>Отдаёт указанные байты с кодом 200 на любой запрос.</summary>
        internal static FakeImageHandler Ok(byte[] payload) => new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

        /// <summary>Отдаёт указанный код ответа на любой запрос.</summary>
        internal static FakeImageHandler Status(HttpStatusCode code) => new((_, _) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(Array.Empty<byte>()) }));

        /// <summary>Падает так, как падает недоступная сеть.</summary>
        internal static FakeImageHandler Broken() => new((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("сеть недоступна")));

        /// <summary>Клиент поверх этого обработчика.</summary>
        internal HttpClient Client() => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Interlocked.Increment(ref this.calls);
            lock (this.requested) {
                this.requested.Add(request.RequestUri?.ToString() ?? string.Empty);
            }

            return this.respond(request, cancellationToken);
        }
    }
}
