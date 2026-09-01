// <copyright file="UpdateErrorScopeTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Game;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Кому принадлежит сорвавшаяся закачка.
    /// <para>
    /// Игрок стоит на игре C — установленной и свежей — и ставит в очередь игру A.
    /// Закачка A падает. Пока ошибка была общим флагом страницы, следующий же пересчёт
    /// кнопки (запуск игры, конец проверки статусов) ставил на витрине C «Повторить»:
    /// клик отвечал «уже установлена или уже в очереди» и ничего не менял.
    /// </para>
    /// </summary>
    public class UpdateErrorScopeTests {
        /// <summary>Ошибка на игре A не превращает кнопку свежей игры C в «Повторить».</summary>
        [Fact]
        public void ОшибкаОднойИгрыНеДелаетПовторУДругой() {
            var applies = UpdateErrorScope.AppliesTo(errorGameId: "repo", gameId: "peak");

            Assert.False(applies);
            Assert.Equal(
                ActionMode.Play,
                ActionButtonState.Decide(applies, unfinishedUpdate: false, isInstalled: true, needsUpdate: false));
        }

        /// <summary>А на своей игре «Повторить» по-прежнему стоит: попытку и правда стоит повторить.</summary>
        [Fact]
        public void НаСвоейИгреПовторитьОстаётся() {
            var applies = UpdateErrorScope.AppliesTo(errorGameId: "repo", gameId: "REPO");

            Assert.True(applies);
            Assert.Equal(
                ActionMode.Retry,
                ActionButtonState.Decide(applies, unfinishedUpdate: false, isInstalled: true, needsUpdate: false));
        }

        /// <summary>Сбоя не было или игра не выбрана — относить ошибку не к чему.</summary>
        [Theory]
        [InlineData(null, "repo")]
        [InlineData("", "repo")]
        [InlineData("repo", null)]
        [InlineData("repo", " ")]
        public void БезИгрыОтноситьОшибкуНеКЧему(string? errorGameId, string? gameId) {
            Assert.False(UpdateErrorScope.AppliesTo(errorGameId, gameId));
        }
    }
}
