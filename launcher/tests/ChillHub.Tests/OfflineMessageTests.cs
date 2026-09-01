// <copyright file="OfflineMessageTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;

    using ChillHub.Core.Net;

    using Xunit;

    /// <summary>
    /// Текст, который игрок читает вместо игр и новостей, когда связи нет.
    /// <para>
    /// Проверяется машиной по той же причине, что и список обновлений: это
    /// пользовательский текст, и его легко испортить обратно в «GET .../latest.json:
    /// The SSL connection could not be established». Здесь же закреплено главное
    /// различие — совет «проверьте интернет» звучит только тогда, когда интернета
    /// действительно нет.
    /// </para>
    /// </summary>
    public class OfflineMessageTests {
        /// <summary>Сети на компьютере нет — что бы ни бросил HTTP, причина в этом.</summary>
        [Fact]
        public void БезСетиНаКомпьютереПричинаВсегдаНетИнтернета() {
            var kind = OfflineMessage.Classify(new HttpRequestException("boom"), networkAvailable: false);

            Assert.Equal(OfflineKind.NoInternet, kind);
            Assert.Equal("Нет интернета", OfflineMessage.Describe(kind).Title);
        }

        /// <summary>Имя сервера не разрешилось — наружу ходу нет, это не молчание сервера.</summary>
        [Theory]
        [InlineData(SocketError.HostNotFound)]
        [InlineData(SocketError.TryAgain)]
        [InlineData(SocketError.NetworkUnreachable)]
        [InlineData(SocketError.NetworkDown)]
        public void ОтказНаУровнеСетиЧитаетсяКакОтсутствиеИнтернета(SocketError error) {
            var ex = new HttpRequestException("не удалось", new SocketException((int)error));

            Assert.Equal(OfflineKind.NoInternet, OfflineMessage.Classify(ex, networkAvailable: true));
        }

        /// <summary>
        /// Соединение отвергнуто или оборвалось на TLS — сеть работает, молчит сервер.
        /// Советовать здесь чинить интернет нельзя: чинить нечего.
        /// </summary>
        [Fact]
        public void ОтказСервераНеСоветуетЧинитьИнтернет() {
            var ex = new HttpRequestException("tls", new SocketException((int)SocketError.ConnectionRefused));

            var text = OfflineMessage.Describe(ex, networkAvailable: true);

            Assert.Equal(OfflineKind.ServerUnreachable, OfflineMessage.Classify(ex, networkAvailable: true));
            Assert.Equal("Сервер не отвечает", text.Title);
            Assert.DoesNotContain("Проверьте", text.Hint, StringComparison.Ordinal);
        }

        /// <summary>Сервер ответил своей поломкой — отдельный случай: ждать, а не чинить.</summary>
        [Fact]
        public void ПятисотыйОтветЧитаетсяКакНеполадкиНаСервере() {
            var ex = new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway);

            Assert.Equal(OfflineKind.ServerError, OfflineMessage.Classify(ex, networkAvailable: true));
        }

        /// <summary>
        /// 404 — не авария сервера: раздела просто нет, и обещать «попробуйте позже»
        /// нечестно. Такой ответ остаётся нейтральным «сервер не отвечает».
        /// </summary>
        [Fact]
        public void ЧетырёхсотыйОтветНеСчитаетсяАварией() {
            var ex = new HttpRequestException("not found", null, HttpStatusCode.NotFound);

            Assert.NotEqual(OfflineKind.ServerError, OfflineMessage.Classify(ex, networkAvailable: true));
        }

        /// <summary>Ни одного адреса, метода запроса и английского слова на экране.</summary>
        [Theory]
        [InlineData(nameof(OfflineKind.NoInternet))]
        [InlineData(nameof(OfflineKind.ServerUnreachable))]
        [InlineData(nameof(OfflineKind.ServerError))]
        public void ТекстыБезТехническихПодробностей(string kindName) {
            var kind = (OfflineKind)Enum.Parse(typeof(OfflineKind), kindName);
            var text = OfflineMessage.Describe(kind);

            foreach (var line in new[] { text.Title, text.Hint, text.Status, OfflineMessage.UpdateCheckFailed(kind) }) {
                Assert.False(string.IsNullOrWhiteSpace(line));
                foreach (var junk in new[] { "http", "GET", "Exception", "null", "SSL", "исключен" }) {
                    Assert.DoesNotContain(junk, line, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Заголовок — подпись пустого состояния, а не предложение: без точки в конце.
            Assert.False(text.Title.EndsWith(".", StringComparison.Ordinal));
        }

        /// <summary>
        /// Окно самообновления обязано сказать, что будет дальше: игрок в этот момент
        /// решает не «чинить ли сеть», а «запустится ли лаунчер вообще».
        /// </summary>
        [Fact]
        public void ПроваленнаяПроверкаОбновленияОбещаетЗапуск() {
            var text = OfflineMessage.UpdateCheckFailed(OfflineKind.NoInternet);

            Assert.Contains("нет интернета", text, StringComparison.Ordinal);
            Assert.Contains("запустится", text, StringComparison.Ordinal);
        }
    }
}
