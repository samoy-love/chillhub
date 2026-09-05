// <copyright file="FeedbackCloseTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Закрытие формы обратной связи: спрашивать ли.
    /// <para>
    /// Набранный текст пропадал без единого слова, и восстановить его было неоткуда. А
    /// пустую форму спрашивать не о чем: лишнее окно на каждый промах по крестику
    /// раздражает сильнее, чем помогает.
    /// </para>
    /// </summary>
    public class FeedbackCloseTests {
        /// <summary>Пустую форму закрываем молча.</summary>
        [Fact]
        public void ПустуюФормуЗакрываемБезВопросов()
            => Assert.False(FeedbackClose.NeedsConfirm(null, string.Empty, "   "));

        /// <summary>Любое заполненное поле — повод спросить: терять его молча нельзя.</summary>
        /// <param name="name">Имя.</param>
        /// <param name="contact">Контакт.</param>
        /// <param name="comment">Сообщение.</param>
        [Theory]
        [InlineData("Алексей", "", "")]
        [InlineData("", "@user", "")]
        [InlineData("", "", "не качается сборка")]
        public void ЛюбойНабранныйТекстСпрашивают(string name, string contact, string comment)
            => Assert.True(FeedbackClose.NeedsConfirm(name, contact, comment));

        /// <summary>Вопрос называет, что именно потеряется.</summary>
        [Fact]
        public void ВопросНазываетЧтоПотеряется() {
            Assert.Equal("Закрыть форму?", FeedbackClose.Title);
            Assert.Contains("потерян", FeedbackClose.Body);
        }
    }
}
