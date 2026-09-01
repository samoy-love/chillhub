// <copyright file="NewsNavigationGateTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;

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
        private readonly List<string> opened = new List<string>();
        private readonly List<string> notes = new List<string>();

        /// <summary>Своя отрисовка проходит, даже если движок назвал её как угодно.</summary>
        /// <param name="uri">Адрес, сообщённый движком для нашей же страницы.</param>
        [Theory]
        [InlineData("about:blank")]
        [InlineData("data:text/html;charset=utf-8,%3Chtml%3E")]
        [InlineData("https://launcher.samoy.love/news/x.md")]
        [InlineData("")]
        [InlineData("нечто непредвиденное")]
        public void ПослеПометкиПереходПроходитПриЛюбомАдресе(string uri) {
            var gate = this.Gate();
            gate.BeginOwnPageLoad();

            var cancel = gate.OnNavigationStarting(uri);

            Assert.False(cancel, "отменённая своя страница — это пустая новость");
            Assert.Empty(this.opened);
            Assert.Empty(this.notes);
        }

        /// <summary>
        /// Метка одноразовая: следующий переход после отрисовки — уже ссылка из текста,
        /// и он обязан быть отменён, а адрес уйти в браузер.
        /// </summary>
        [Fact]
        public void СледующийПереходПослеОтрисовкиУжеСсылка() {
            var gate = this.Gate();
            gate.BeginOwnPageLoad();
            gate.OnNavigationStarting("about:blank");

            var cancel = gate.OnNavigationStarting("https://example.com/article");

            Assert.True(cancel, "по ссылке из новости окно лаунчера никуда не уходит");
            Assert.Equal(new[] { "https://example.com/article" }, this.opened);
        }

        /// <summary>Без пометки не проходит ничего чужого — метка не выдаётся сама собой.</summary>
        [Fact]
        public void БезПометкиЧужойАдресОтменяетсяИНикудаНеУходит() {
            var gate = this.Gate();

            var cancel = gate.OnNavigationStarting("file:///C:/Windows/System32/cmd.exe");

            Assert.True(cancel);
            Assert.Empty(this.opened);
        }

        /// <summary>
        /// Ошибку рисуют тем же способом, что и новость, поэтому пометка ставится и
        /// перед ней: иначе сообщение об ошибке гасло бы так же, как гасла новость.
        /// </summary>
        [Fact]
        public void ПовторнаяОтрисовкаСноваПроходит() {
            var gate = this.Gate();
            gate.BeginOwnPageLoad();
            gate.OnNavigationStarting("about:blank");
            gate.BeginOwnPageLoad();

            Assert.False(gate.OnNavigationStarting("about:blank"));
        }

        /// <summary>
        /// Новое окно не открывается никогда и метку не тратит: своя отрисовка нового
        /// окна не просит, а потратив метку, она погасила бы саму страницу.
        /// </summary>
        [Fact]
        public void НовоеОкноНеТратитПометку() {
            var gate = this.Gate();
            gate.BeginOwnPageLoad();

            Assert.True(gate.OnNewWindowRequested("https://example.com"));
            Assert.Equal(new[] { "https://example.com" }, this.opened);
            Assert.False(gate.OnNavigationStarting("about:blank"));
        }

        /// <summary>Запрещённый адрес гасится и оболочке не достаётся.</summary>
        [Fact]
        public void ЗапрещённыйАдресНовогоОкнаНикудаНеУходит() {
            var gate = this.Gate();

            Assert.True(gate.OnNewWindowRequested("steam://run/570"));
            Assert.Empty(this.opened);
        }

        /// <summary>
        /// Каждый погашенный переход оставляет след с адресом.
        /// <para>
        /// Без следа пустая страница ничем себя не объясняет: в прошлый раз объяснить
        /// её было нечем, и разбираться пришлось по описанию с чужого экрана.
        /// </para>
        /// </summary>
        [Fact]
        public void ПогашенныйПереходОставляетСледСАдресом() {
            var gate = this.Gate();

            gate.OnNavigationStarting("file:///C:/Windows/System32/cmd.exe");

            var note = Assert.Single(this.notes);
            Assert.Contains("file:///C:/Windows/System32/cmd.exe", note);
        }

        /// <summary>Своя отрисовка следа не оставляет: гасить в ней нечего.</summary>
        [Fact]
        public void СвояОтрисовкаСледаНеОставляет() {
            var gate = this.Gate();
            gate.BeginOwnPageLoad();

            gate.OnNavigationStarting("about:blank");

            Assert.Empty(this.notes);
        }

        private NewsNavigationGate Gate() => new NewsNavigationGate(this.opened.Add, this.notes.Add);
    }
}
