// <copyright file="NewsNavigationGateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.News;

    using Xunit;

    /// <summary>
    /// Порядок переходов открытой новости.
    /// <para>
    /// Здесь жил дефект, из-за которого новость открывалась пустой: обработчик
    /// отменял переход, чей адрес не узнавал, — и не узнавал в том числе адрес
    /// собственной отрисовки, который придумывает сам движок. Проверяется
    /// последовательность целиком, а не отдельное решение.
    /// </para>
    /// </summary>
    public class NewsNavigationGateTests {
        /// <summary>Своя отрисовка проходит, даже если движок назвал её как угодно.</summary>
        /// <param name="uri">Адрес, сообщённый движком для нашей же страницы.</param>
        [Theory]
        [InlineData("about:blank")]
        [InlineData("data:text/html;charset=utf-8,%3Chtml%3E")]
        [InlineData("https://launcher.samoy.love/news/x.md")]
        [InlineData("")]
        [InlineData("нечто непредвиденное")]
        public void ПослеПометкиПереходПроходитПриЛюбомАдресе(string uri) {
            var gate = new NewsNavigationGate();
            gate.BeginOwnPageLoad();

            var action = gate.OnNavigationStarting(uri);

            Assert.False(action.Cancel, "отменённая своя страница — это пустая новость");
            Assert.Null(action.OpenExternally);
        }

        /// <summary>
        /// Метка одноразовая: следующий переход после отрисовки — уже ссылка из текста,
        /// и он обязан быть отменён, а адрес уйти в браузер.
        /// </summary>
        [Fact]
        public void СледующийПереходПослеОтрисовкиУжеСсылка() {
            var gate = new NewsNavigationGate();
            gate.BeginOwnPageLoad();
            gate.OnNavigationStarting("about:blank");

            var action = gate.OnNavigationStarting("https://example.com/article");

            Assert.True(action.Cancel, "по ссылке из новости окно лаунчера никуда не уходит");
            Assert.Equal("https://example.com/article", action.OpenExternally);
        }

        /// <summary>Без пометки не проходит ничего чужого — метка не выдаётся сама собой.</summary>
        [Fact]
        public void БезПометкиЧужойАдресОтменяетсяИНикудаНеУходит() {
            var gate = new NewsNavigationGate();

            var action = gate.OnNavigationStarting("file:///C:/Windows/System32/cmd.exe");

            Assert.True(action.Cancel);
            Assert.Null(action.OpenExternally);
        }

        /// <summary>
        /// Ошибку рисуют тем же способом, что и новость, поэтому пометка ставится и
        /// перед ней: иначе сообщение об ошибке гасло бы так же, как гасла новость.
        /// </summary>
        [Fact]
        public void ПовторнаяОтрисовкаСноваПроходит() {
            var gate = new NewsNavigationGate();
            gate.BeginOwnPageLoad();
            gate.OnNavigationStarting("about:blank");
            gate.BeginOwnPageLoad();

            Assert.False(gate.OnNavigationStarting("about:blank").Cancel);
        }

        /// <summary>
        /// Новое окно не открывается никогда и метку не тратит: своя отрисовка нового
        /// окна не просит, а потратив метку, она погасила бы саму страницу.
        /// </summary>
        [Fact]
        public void НовоеОкноНеТратитПометку() {
            var gate = new NewsNavigationGate();
            gate.BeginOwnPageLoad();

            var window = gate.OnNewWindowRequested("https://example.com");

            Assert.True(window.Cancel);
            Assert.Equal("https://example.com", window.OpenExternally);
            Assert.False(gate.OnNavigationStarting("about:blank").Cancel);
        }
    }
}
