// <copyright file="ImageLoader.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    /// <summary>
    /// Загрузка обложек/иконок: разбор URL относительно origin API, HTTP-загрузка с дедупликацией
    /// параллельных запросов, кеш готовых (замороженных) <see cref="BitmapImage"/> и управление
    /// скелетоном-заглушкой рядом с картинкой.
    /// </summary>
    internal static class ImageLoader {
        /// <summary>
        /// Идущие загрузки по URL. Хранится сама задача, а не признак «занято»:
        /// второй элемент с тем же URL должен дождаться результата, а не остаться пустым.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Task<byte[]>> Inflight = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Высота декодирования по умолчанию, если у элемента не задан размер.</summary>
        private const int DefaultDecodeHeight = 88;

        /// <summary>
        /// Потолок на одну картинку.
        /// <para>
        /// Адрес обложки приходит в новости с сервера, то есть это внешний вход, а не наша
        /// константа. Без предела ответ читается в память целиком, сколько бы его ни отдали.
        /// 16 МиБ заведомо больше любой разумной обложки и заведомо меньше того, от чего
        /// лаунчеру станет плохо.
        /// </para>
        /// </summary>
        internal const int MaxImageBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Сколько ждать одну картинку.
        /// <para>
        /// Своего таймаута у загрузки не было, работал общий <see cref="HttpClient.Timeout"/>
        /// в 100 секунд — и всё это время элемент списка стоял пустым. Обложка столько
        /// ожидания не стоит: лучше показать карточку без картинки.
        /// </para>
        /// </summary>
        internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Отдельный HttpClient: много мелких параллельных запросов за картинками.</summary>
        private static HttpClient http = CreateDefaultClient();

        /// <summary>
        /// Клиент, которым качаются картинки. Выведен наружу сборки только затем, чтобы тесты
        /// могли подставить свой обработчик: иначе проверить дедупликацию и поведение при
        /// отказе сети можно было бы только реальными запросами наружу.
        /// </summary>
        internal static HttpClient Http {
            get => http;
            set => http = value ?? CreateDefaultClient();
        }

        /// <summary>
        /// Возвращает загрузчик в исходное состояние: пустой кеш, пустой список идущих
        /// загрузок, клиент по умолчанию.
        /// Нужно тестам: статика переживает границу теста, и без сброса результат одного
        /// теста подменял бы сеть в следующем.
        /// </summary>
        internal static void ResetForTests() {
            Cache.Clear();
            Inflight.Clear();
            http = CreateDefaultClient();
        }

        /// <summary>Есть ли готовая картинка для этого URL — проверяется без обращения к сети.</summary>
        internal static bool IsCached(string url) => Cache.ContainsKey(url);

        /// <summary>
        /// Полный сценарий обработчика Loaded у картинки: достать URL (Tag → DataContext → текущий Source),
        /// нормализовать относительно origin API и запустить загрузку.
        /// </summary>
        internal static void AttachAndLoad(Image img, string baseApi) {
            if (img == null) {
                return;
            }

            string raw = (img.Tag as string) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) {
                raw = ExtractUrlFromDataContext(img.DataContext);
            }

            if (string.IsNullOrWhiteSpace(raw)) {
                raw = ExtractUrlFromSource(img.Source);
            }

            if (string.IsNullOrWhiteSpace(raw)) {
                img.Visibility = Visibility.Collapsed;
                HideSkeleton(img);
                return;
            }

            string url;
            try {
                url = ResolveUrl(raw, baseApi);
            }
            catch (Exception ex) {
                // Битый URL в манифесте/новости: картинку не показываем, но страница живёт дальше.
                Logging.Logger.Warn($"[ImgLoad] не удалось разобрать URL raw='{raw}' baseApi='{baseApi}': {ex.Message}");
                img.Visibility = Visibility.Collapsed;
                HideSkeleton(img);
                return;
            }

            DebugLog($"[ImgLoad] resolved url='{url}'");

            // Если уже есть валидный источник с тем же URL — просто показать и скрыть скелетон
            if (img.Source is BitmapImage existing && existing.UriSource != null &&
                string.Equals(existing.UriSource.OriginalString, url, StringComparison.OrdinalIgnoreCase)) {
                img.Visibility = Visibility.Visible;
                HideSkeleton(img);
                return;
            }

            // Загрузка через HttpClient -> Stream, чтобы избежать проблем с относительными путями/кэшем
            _ = LoadImageAsync(img, url);

            // Скелетон скрываем сразу: картинка появится по готовности
            HideSkeleton(img);
        }

        /// <summary>Реакция на событие ImageFailed: спрятать картинку и скелетон.</summary>
        internal static void HandleImageFailed(Image img, Exception? error) {
            if (img == null) {
                return;
            }

            img.Visibility = Visibility.Collapsed;
            HideSkeleton(img);
            Logging.Logger.Warn($"[ImgLoad] ImageFailed: {error?.Message ?? "(без деталей)"}");
        }

        /// <summary>Скачивает картинку и применяет её к элементу в UI-потоке.</summary>
        internal static async Task LoadImageAsync(Image img, string url) {
            try {
                // Cache hit: apply immediately
                if (Cache.TryGetValue(url, out var cached)) {
                    DebugLog($"[ImgLoad] cache hit url='{url}'");
                    await img.Dispatcher.InvokeAsync(() => {
                        img.Source = cached;
                        img.Visibility = Visibility.Visible;
                    });
                    return;
                }

                byte[] bytes = await FetchBytesAsync(url).ConfigureAwait(false);

                // Пока ждали, готовую картинку мог положить в кеш другой элемент
                if (Cache.TryGetValue(url, out var ready)) {
                    await img.Dispatcher.InvokeAsync(() => {
                        img.Source = ready;
                        img.Visibility = Visibility.Visible;
                    });
                    return;
                }

                await img.Dispatcher.InvokeAsync(() => {
                    // Отдельный MemoryStream на элемент: BitmapImage читает его при EndInit
                    using var ms = new MemoryStream(bytes);
                    ApplyBitmap(img, ms, url);
                });
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"[ImgLoad] error url='{url}': {ex.Message}");
                try {
                    await img.Dispatcher.InvokeAsync(() => img.Visibility = Visibility.Collapsed);
                }
                catch (Exception exUi) {
                    // Диспетчер уже завершён (окно закрывается) — прятать нечего.
                    Logging.Logger.Warn($"[ImgLoad] не удалось скрыть картинку url='{url}': {exUi.Message}");
                }
            }
        }

        /// <summary>
        /// Байты картинки по URL: сеть дёргается ОДИН раз на URL, результат разделяют все ждущие.
        /// <para>
        /// Раньше второй запрос за тем же адресом просто выходил из метода, и одинаковые
        /// иконки (одна игра в списке и в шапке, повторные обложки новостей) оставались пустыми.
        /// </para>
        /// </summary>
        internal static async Task<byte[]> FetchBytesAsync(string url) {
            var download = Inflight.GetOrAdd(url, DownloadAsync);
            try {
                return await download.ConfigureAwait(false);
            }
            finally {
                // Ключ убираем ровно на свою задачу: за это время под тем же URL
                // мог появиться уже следующий, не связанный с нашим, запрос.
                Inflight.TryRemove(new KeyValuePair<string, Task<byte[]>>(url, download));
            }
        }

        /// <summary>Ищет соседний скелетон-заглушку по имени ImgSkeleton в общем родителе.</summary>
        internal static Border? FindImgSkeleton(DependencyObject? parent) {
            try {
                if (parent == null) {
                    return null;
                }

                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++) {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is Border b && b.Name == "ImgSkeleton") {
                        return b;
                    }
                }
            }
            catch (Exception ex) {
                // Визуальное дерево может быть ещё не построено — скелетон просто не найдём.
                Logging.Logger.Warn($"[ImgLoad] FindImgSkeleton: {ex.Message}");
            }

            return null;
        }

        /// <summary>Прячет скелетон рядом с картинкой, если он есть.</summary>
        internal static void HideSkeleton(Image img) {
            var sk = FindImgSkeleton(VisualTreeHelper.GetParent(img));
            if (sk != null) {
                sk.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Нормализует URL картинки: поддержаны протокол-относительные (//host/path),
        /// абсолютные и корне-относительные пути; последние привязываются к origin из BaseApi.
        /// </summary>
        internal static string ResolveUrl(string raw, string baseApi) {
            var apiUri = new Uri(baseApi.TrimEnd('/') + "/", UriKind.Absolute);
            DebugLog($"[ImgLoad] raw='{raw}' baseApi='{baseApi}' origin='{apiUri.Scheme}://{apiUri.Authority}'");

            if (raw.StartsWith("//", StringComparison.Ordinal)) {
                var url = new Uri(apiUri.Scheme + ":" + raw, UriKind.Absolute).ToString();
                DebugLog($"[ImgLoad] case='protocol-relative' url='{url}'");
                return url;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var abs)) {
                var url = abs.ToString();
                DebugLog($"[ImgLoad] case='absolute' url='{url}'");
                return url;
            }

            // Относительные URL: принудительно делаем корневыми к origin
            // Пример: "manifests/game/icon.png" -> "/manifests/game/icon.png"
            var rel = raw.StartsWith("/", StringComparison.Ordinal) ? raw : ("/" + raw);
            var resolved = new Uri(apiUri, rel).ToString();
            DebugLog($"[ImgLoad] case='relative' rel='{rel}' url='{resolved}'");
            return resolved;
        }

        /// <summary>
        /// Раскодирует скачанные байты и показывает картинку. Вызывается только в UI-потоке.
        /// </summary>
        internal static void ApplyBitmap(Image img, MemoryStream ms, string url) {
            try {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.PreservePixelFormat;

                // Декодируем примерно под визуальный размер: быстрее и экономит память
                int targetH = 0;
                if (img.Height > 0 && !double.IsNaN(img.Height)) {
                    targetH = (int)Math.Round(img.Height);
                }

                if (targetH <= 0 && img.DesiredSize.Height > 0) {
                    targetH = (int)Math.Round(img.DesiredSize.Height);
                }

                bi.DecodePixelHeight = targetH > 0 ? targetH : DefaultDecodeHeight;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();

                Cache[url] = bi; // заморожен — безопасно переиспользовать между потоками
                img.Source = bi;
                img.Visibility = Visibility.Visible;
                DebugLog($"[ImgLoad] image applied url='{url}'");
            }
            catch (Exception ex) {
                // Битый/неподдерживаемый формат: показывать нечего, прячем элемент.
                img.Visibility = Visibility.Collapsed;
                Logging.Logger.Warn($"[ImgLoad] apply error url='{url}': {ex.Message}");
            }
        }

        /// <summary>
        /// URL картинки из модели, к которой привязан элемент.
        /// Принимает сам DataContext, а не элемент: разбор модели к интерфейсу отношения не имеет,
        /// и проверять его WPF-элементом было бы незачем.
        /// </summary>
        internal static string ExtractUrlFromDataContext(object? dataContext) {
            try {
                return dataContext switch {
                    GameInfo gi => gi.IconUrl ?? string.Empty, // у GameInfo только IconUrl
                    NewsItem ni => ni.CoverUrl ?? string.Empty, // NewsItem использует CoverUrl
                    _ => string.Empty,
                };
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"[ImgLoad] не удалось прочитать URL из DataContext: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// URL картинки из уже назначенного источника — последняя надежда, когда ни Tag,
        /// ни модель адреса не дали.
        /// </summary>
        internal static string ExtractUrlFromSource(ImageSource? source) {
            try {
                if (source is BitmapImage bi && bi.UriSource != null) {
                    return bi.UriSource.OriginalString;
                }
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"[ImgLoad] не удалось прочитать URL из Source: {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>Клиент по умолчанию: много мелких параллельных запросов за картинками.</summary>
        private static HttpClient CreateDefaultClient() => new HttpClient(new HttpClientHandler {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            MaxConnectionsPerServer = 16,
        });

        /// <summary>Одна HTTP-загрузка картинки в память; результат разделяют все ждущие элементы.</summary>
        private static async Task<byte[]> DownloadAsync(string url) {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DebugLog($"[ImgLoad] HTTP GET start url='{url}'");
            // Таймаут ставится здесь, а не на клиенте: клиент общий и подменяемый тестами,
            // а ждать дольше положенного не должна ни одна картинка.
            using var cts = new System.Threading.CancellationTokenSource(DownloadTimeout);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                var contentType = resp.Content?.Headers?.ContentType?.ToString() ?? string.Empty;
                DebugLog($"[ImgLoad] HTTP non-200 status={(int)resp.StatusCode} contentType='{contentType}' url='{url}'");
                throw new HttpRequestException("HTTP " + (int)resp.StatusCode + " " + resp.StatusCode);
            }

            // Заявленную длину проверяем ДО чтения — это отсекает заведомо большой ответ,
            // не потратив ни байта памяти. Но полагаться только на неё нельзя: заголовка
            // может не быть вовсе, а может стоять и неправда. Поэтому предел проверяется
            // ещё раз по факту прочитанного, ниже.
            var declared = resp.Content.Headers.ContentLength;
            if (declared > MaxImageBytes) {
                throw new InvalidDataException(
                    $"картинка заявлена размером {declared} Б при пределе {MaxImageBytes} Б: {url}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(declared is > 0 and <= MaxImageBytes ? (int)declared : 0);
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cts.Token).ConfigureAwait(false)) > 0) {
                if (buffer.Length + read > MaxImageBytes) {
                    throw new InvalidDataException($"картинка больше предела {MaxImageBytes} Б: {url}");
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            sw.Stop();
            DebugLog($"[ImgLoad] HTTP ok bytes={bytes.Length} elapsedMs={sw.ElapsedMilliseconds} url='{url}'");
            return bytes;
        }

        /// <summary>
        /// Трассировка загрузки картинок. В client.log НЕ пишет: на каждую иконку
        /// приходилось три-четыре строки, и полезные записи (планы обновления, ошибки)
        /// вытеснялись из лога ротацией ещё до того, как их успевали прочитать.
        /// Ошибки загрузки логируются отдельно, через Logger.Warn.
        /// </summary>
        private static void DebugLog(string msg) {
            System.Diagnostics.Debug.WriteLine(msg);
        }
    }
}
