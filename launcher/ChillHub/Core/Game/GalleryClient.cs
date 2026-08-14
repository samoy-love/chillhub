// <copyright file="GalleryClient.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Game {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Одна картинка галереи с уже разрешённым абсолютным адресом — этим и биндится
    /// UI карусели (стрелки/точки/превью), без знания про base URL и контракт файла.
    /// </summary>
    /// <param name="Caption">Подпись картинки (может быть пустой).</param>
    /// <param name="ImageUrl">Абсолютный адрес картинки.</param>
    /// <param name="IsCover">true, если это обложка галереи (`cover` из gallery.json).</param>
    public sealed record GalleryImage(string Caption, string ImageUrl, bool IsCover);

    /// <summary>Сырой контракт `gallery.json`: пишет его админка (server/internal/adminapi/gamegallery).</summary>
    internal sealed class GalleryManifest {
        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("items")]
        public List<GalleryManifestItem> Items { get; set; } = new();
    }

    /// <summary>Одна запись `items` в `gallery.json`.</summary>
    internal sealed class GalleryManifestItem {
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("caption")]
        public string? Caption { get; set; }
    }

    /// <summary>
    /// Галерея игры: `content/&lt;gameId&gt;/gallery/gallery.json` — по тому же base URL,
    /// с которого лаунчер тянет остальной контент игры (см. <see cref="ChillHub.Core.Sync.IntegrityChecker"/>
    /// и <see cref="GameChangelogLoader"/> — тот же приём: относительный путь от `baseApi`,
    /// абсолютные адреса картинок клиент достраивает сам).
    /// <para>
    /// Результат кешируется в памяти на время процесса: карусель на витрине не должна
    /// перезапрашивать сервер при каждом наведении на игру в сайдбаре.
    /// </para>
    /// </summary>
    public sealed class GalleryClient {
        private readonly HttpClient http;
        private readonly ConcurrentDictionary<string, IReadOnlyList<GalleryImage>> cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initializes a new instance of the <see cref="GalleryClient"/> class.</summary>
        /// <param name="http">Клиент, которым ходят за `gallery.json`; по умолчанию — общий клиент лаунчера.</param>
        public GalleryClient(HttpClient? http = null) => this.http = http ?? ChillHub.Core.Net.HttpClientProvider.Shared;

        /// <summary>Адрес `gallery.json` конкретной игры.</summary>
        /// <param name="baseApi">База API/контента из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Полный адрес манифеста галереи.</returns>
        public static string ManifestUrl(string baseApi, string gameId)
            => $"{(baseApi ?? string.Empty).TrimEnd('/')}/content/{gameId}/gallery/gallery.json";

        /// <summary>Папка, от которой строятся относительные адреса картинок галереи.</summary>
        /// <param name="baseApi">База API/контента из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Базовый адрес папки галереи (с завершающим `/`).</returns>
        public static string GalleryBaseUrl(string baseApi, string gameId)
            => $"{(baseApi ?? string.Empty).TrimEnd('/')}/content/{gameId}/gallery/";

        /// <summary>
        /// Отдаёт галерею игры: обложка — первым элементом (если она входит в `items`,
        /// не дублируется), порядок остальных — как в `items`. При отсутствии
        /// `gallery.json` на сервере (404/сетевая ошибка) — пустой список, а не исключение:
        /// у витрины должен быть план Б (градиент вместо карусели), а не сломанная страница.
        /// </summary>
        /// <param name="baseApi">База API/контента из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <param name="forceRefresh">Не брать из кеша, перезапросить сервер.</param>
        /// <returns>Упорядоченный список картинок галереи.</returns>
        public async Task<IReadOnlyList<GalleryImage>> GetGalleryAsync(
            string baseApi, string gameId, CancellationToken ct = default, bool forceRefresh = false) {
            if (string.IsNullOrWhiteSpace(gameId)) {
                return Array.Empty<GalleryImage>();
            }

            if (!forceRefresh && this.cache.TryGetValue(gameId, out var cached)) {
                return cached;
            }

            IReadOnlyList<GalleryImage> result;
            try {
                var manifest = await this.http
                    .GetFromJsonAsync<GalleryManifest>(ManifestUrl(baseApi, gameId), ct)
                    .ConfigureAwait(true);
                result = ParseManifest(manifest, baseApi, gameId);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException) {
                ChillHub.Core.Logging.Logger.Warn($"GalleryClient: галерея '{gameId}' недоступна: {ex.Message}");
                result = Array.Empty<GalleryImage>();
            }

            this.cache[gameId] = result;
            return result;
        }

        /// <summary>Разбор контракта галереи в готовый к биндингу список картинок.</summary>
        /// <param name="manifest">Разобранный `gallery.json` (может быть null при пустом теле ответа).</param>
        /// <param name="baseApi">База API/контента из конфига.</param>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Упорядоченный список картинок; обложка — первым элементом.</returns>
        internal static IReadOnlyList<GalleryImage> ParseManifest(GalleryManifest? manifest, string baseApi, string gameId) {
            if (manifest is null) {
                return Array.Empty<GalleryImage>();
            }

            var galleryBase = GalleryBaseUrl(baseApi, gameId);
            var cover = manifest.Cover?.Trim();

            // Обложка без `items` — это галерея из одной картинки, а не пустая галерея.
            // Так выглядят все манифесты, записанные админкой до того, как SetCover
            // научился регистрировать файл в `items`: витрина у таких игр молча
            // оставалась пустой, хотя в админке обложка была выбрана и подсвечена.
            if (manifest.Items is null || manifest.Items.Count == 0) {
                return string.IsNullOrWhiteSpace(cover)
                    ? Array.Empty<GalleryImage>()
                    : new[] { new GalleryImage(Caption: string.Empty, ImageUrl: galleryBase + cover!.TrimStart('/'), IsCover: true) };
            }

            var images = manifest.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.File))
                .Select(i => new GalleryImage(
                    Caption: i.Caption?.Trim() ?? string.Empty,
                    ImageUrl: galleryBase + i.File.TrimStart('/'),
                    IsCover: !string.IsNullOrWhiteSpace(cover) && string.Equals(i.File, cover, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // Обложка — первым элементом карусели, даже если в items она стояла не первой:
            // это то, что игрок видит сразу при открытии витрины/страницы игры.
            if (images.Any(i => i.IsCover)) {
                images = images.OrderByDescending(i => i.IsCover).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(cover)) {
                // Обложка названа, но её нет среди items — витрина всё равно обязана
                // показать именно её: это выбор администратора, а не первый попавшийся кадр.
                images.Insert(0, new GalleryImage(
                    Caption: string.Empty,
                    ImageUrl: galleryBase + cover!.TrimStart('/'),
                    IsCover: true));
            }

            return images;
        }

        /// <summary>Сбрасывает закешированный результат для игры (например, после правок в админке).</summary>
        /// <param name="gameId">Идентификатор игры.</param>
        public void InvalidateCache(string gameId) => this.cache.TryRemove(gameId, out _);
    }
}
