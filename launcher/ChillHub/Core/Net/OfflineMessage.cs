// <copyright file="OfflineMessage.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Net {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;

    /// <summary>Почему лаунчер сейчас не может достучаться до сервера.</summary>
    internal enum OfflineKind {
        /// <summary>На компьютере нет сети: кабель, Wi-Fi, самолётный режим.</summary>
        NoInternet,

        /// <summary>Сеть есть, но сервер молчит: не отвечает, обрывает, отдаёт мусор.</summary>
        ServerUnreachable,

        /// <summary>Сервер ответил, но ошибкой — у него самого что-то сломалось.</summary>
        ServerError,
    }

    /// <summary>
    /// Что показать вместо содержимого, пока связи нет.
    /// </summary>
    /// <param name="Title">Заголовок пустого состояния — одна строка, без точки.</param>
    /// <param name="Hint">Пояснение под заголовком: что игроку с этим делать.</param>
    /// <param name="Status">Строка для нижней панели окна.</param>
    internal readonly record struct OfflineText(string Title, string Hint, string Status);

    /// <summary>
    /// Превращает сетевой сбой в человеческий текст.
    /// <para>
    /// ЭТО ЕДИНСТВЕННОЕ, ЧТО ИГРОК ВИДИТ БЕЗ ИНТЕРНЕТА. До появления этого класса он
    /// читал «GET https://launcher.samoy.love/manifests/launcher/latest.json: The SSL
    /// connection could not be established, see inner exception» — адрес, метод запроса и
    /// английскую фразу про внутреннее исключение. Ни один из трёх кусков не помогает
    /// включить Wi-Fi. Текст исключения нужен разработчику и уходит в лог и в подсказку,
    /// а на экране остаётся ответ на два вопроса: что случилось и что делать.
    /// </para>
    /// <para>
    /// Разделять «нет интернета» и «сервер не отвечает» приходится потому, что советы
    /// противоположные: в первом случае чинить связь у себя, во втором — просто подождать.
    /// «Проверьте подключение к интернету» при работающем интернете отправляет человека
    /// перезагружать роутер из-за нашей же аварии.
    /// </para>
    /// </summary>
    internal static class OfflineMessage {
        /// <summary>
        /// Определяет причину сбоя.
        /// </summary>
        /// <param name="ex">Пойманное исключение (может отсутствовать).</param>
        /// <param name="networkAvailable">На компьютере поднята хоть одна сеть.</param>
        /// <returns>Причина.</returns>
        internal static OfflineKind Classify(Exception? ex, bool networkAvailable) {
            if (!networkAvailable) {
                return OfflineKind.NoInternet;
            }

            for (var current = ex; current != null; current = current.InnerException) {
                // Сервер ответил, и ответ — его собственная поломка. 4xx сюда не попадают:
                // это не «сервер лежит», а наш неверный запрос, и советовать ждать нечего.
                if (current is HttpRequestException http && http.StatusCode is HttpStatusCode code
                    && (int)code >= 500) {
                    return OfflineKind.ServerError;
                }

                if (current is SocketException socket && IsNoNetwork(socket.SocketErrorCode)) {
                    return OfflineKind.NoInternet;
                }
            }

            return OfflineKind.ServerUnreachable;
        }

        /// <summary>
        /// Подбирает текст под причину.
        /// </summary>
        /// <param name="kind">Причина.</param>
        /// <returns>Заголовок, пояснение и строка состояния.</returns>
        internal static OfflineText Describe(OfflineKind kind) => kind switch {
            OfflineKind.NoInternet => new OfflineText(
                "Нет интернета",
                "Проверьте Wi-Fi или сетевой кабель и попробуйте снова.",
                "Нет интернета — игры и новости появятся, когда связь вернётся."),
            OfflineKind.ServerError => new OfflineText(
                "На сервере неполадки",
                "С интернетом всё в порядке, сбой на нашей стороне. Попробуйте позже.",
                "На сервере неполадки — попробуйте позже."),
            _ => new OfflineText(
                "Сервер не отвечает",
                "Интернет есть, а сервер молчит. Обычно это ненадолго — попробуйте позже.",
                "Сервер не отвечает — попробуйте ещё раз через минуту."),
        };

        /// <summary>
        /// Разбирает сбой и сразу подбирает текст.
        /// </summary>
        /// <param name="ex">Пойманное исключение (может отсутствовать).</param>
        /// <param name="networkAvailable">На компьютере поднята хоть одна сеть.</param>
        /// <returns>Заголовок, пояснение и строка состояния.</returns>
        internal static OfflineText Describe(Exception? ex, bool networkAvailable) =>
            Describe(Classify(ex, networkAvailable));

        /// <summary>
        /// Строка окна самообновления: проверить новую версию не вышло.
        /// <para>
        /// Обязана заканчиваться тем, что будет дальше. Одно «не удалось проверить
        /// обновление» перед кнопкой «Продолжить» не отвечает на главный вопрос игрока —
        /// запустится ли лаунчер вообще.
        /// </para>
        /// </summary>
        /// <param name="kind">Причина.</param>
        /// <returns>Текст статуса.</returns>
        internal static string UpdateCheckFailed(OfflineKind kind) {
            var reason = kind switch {
                OfflineKind.NoInternet => "нет интернета",
                OfflineKind.ServerError => "на сервере неполадки",
                _ => "сервер не отвечает",
            };

            return $"Не удалось проверить обновления: {reason}. Лаунчер запустится с установленной версией.";
        }

        /// <summary>
        /// Есть ли на компьютере поднятая сеть. Отдельным методом, чтобы вызывающий
        /// код не тянул System.Net.NetworkInformation ради одной строки, а тесты
        /// могли передать признак руками.
        /// </summary>
        /// <returns>true, если хотя бы один сетевой интерфейс работает.</returns>
        internal static bool NetworkAvailable() {
            try {
                return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            }
            catch (Exception ex) {
                // Опрос интерфейсов — не то, ради чего стоит терять сообщение об ошибке:
                // считаем, что сеть есть, и остаёмся на нейтральном «сервер не отвечает».
                Logging.Logger.Warn($"OfflineMessage.NetworkAvailable: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Отказ такого рода означает, что до сети дело не дошло: имя не разрешилось
        /// или маршрута наружу нет. Отказ в соединении и таймаут сюда не входят —
        /// они говорят как раз о том, что сеть работает, а молчит сервер.
        /// </summary>
        /// <param name="error">Код ошибки сокета.</param>
        /// <returns>true, если похоже на отсутствие сети.</returns>
        private static bool IsNoNetwork(SocketError error) => error switch {
            SocketError.HostNotFound => true,
            SocketError.TryAgain => true,
            SocketError.NoData => true,
            SocketError.NetworkDown => true,
            SocketError.NetworkUnreachable => true,
            SocketError.HostUnreachable => true,
            _ => false,
        };
    }
}
