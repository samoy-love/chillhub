// <copyright file="GameDeletionTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Запреты на удаление файлов игры.
    /// <para>
    /// Удалить игру можно из двух мест — из контекстного меню списка и со страницы игры.
    /// Раньше защиты были выписаны только в первом; вторая кнопка сносила бы файлы
    /// из-под работающей закачки. Теперь решение одно на оба места, и проверяется оно
    /// здесь, а не двумя копиями глазами.
    /// </para>
    /// </summary>
    public class GameDeletionTests {
        /// <summary>
        /// Пока игра в очереди, удалять нечего: закачка пишет в ту же папку, и половина
        /// файлов вернулась бы обратно уже после удаления.
        /// </summary>
        [Fact]
        public void ИграВОчередиУдалятьЗапрещено() {
            var refusal = GameDeletion.Blocker(queued: true, "repo.exe", _ => 0);

            Assert.Contains("Дождитесь завершения", refusal);
        }

        /// <summary>Запущенную игру удалять тоже нельзя, и отказ называет процесс.</summary>
        [Fact]
        public void ЗапущеннуюИгруУдалятьЗапрещеноИПроцессНазван() {
            var refusal = GameDeletion.Blocker(queued: false, @"bin\repo.exe", name => name == "repo" ? 1 : 0);

            Assert.Contains("repo", refusal);
            Assert.Contains("Закройте игру", refusal);
        }

        /// <summary>Ничего не мешает — запрета нет.</summary>
        [Fact]
        public void СвободнуюИгруУдалятьМожно()
            => Assert.Equal(string.Empty, GameDeletion.Blocker(queued: false, "repo.exe", _ => 0));

        /// <summary>
        /// Игра без известного exe тоже удаляется: запретить из-за незнания — значит
        /// оставить папку навсегда. Занятые файлы всё равно защищены самой системой.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void БезИзвестногоExeУдалениеНеЗапрещают(string? exe)
            => Assert.Equal(string.Empty, GameDeletion.Blocker(queued: false, exe, _ => 1));
    }
}
