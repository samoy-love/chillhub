// <copyright file="GameInfoNotifyTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Строка списка игр обновляется по уведомлению, а не по пересборке всего списка.
    /// <para>
    /// Пересборка (<c>Items.Refresh()</c>) заново создаёт каждую строку: значки
    /// перезагружаются, выделение и прокрутка дёргаются. Приходила она на каждую
    /// проверенную игру, на каждый выбор в списке и на каждую завершённую закачку —
    /// отсюда и мерцание. Проверять это глазами нельзя, а вот уведомления — можно.
    /// </para>
    /// </summary>
    public class GameInfoNotifyTests {
        /// <summary>Всё, от чего зависит вид строки, сообщает о своих изменениях.</summary>
        [Fact]
        public void ПоляСтрокиСписковСообщаютОбИзменении() {
            var game = new GameInfo { GameId = "lethal-company" };
            var seen = new List<string?>();
            game.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

            game.Title = "Lethal Company";
            game.IconUrl = "https://example.invalid/icon.png";
            game.IsInstalled = true;
            game.InstalledVersion = "1.0.9";
            game.NeedsUpdate = true;
            game.QueueLabel = "Скачивание · 38%";

            Assert.Equal(
                new[] { "Title", "IconUrl", "IsInstalled", "InstalledVersion", "NeedsUpdate", "QueueLabel" },
                seen);
        }

        /// <summary>
        /// ТО ЖЕ ЗНАЧЕНИЕ — НЕ ИЗМЕНЕНИЕ. Проверка файлов переписывает статусы пачками, и
        /// уведомление на «установлена = установлена» перерисовывало бы список так же
        /// часто, как раньше это делала пересборка.
        /// </summary>
        [Fact]
        public void ПовторнаяЗаписьТогоЖеЗначенияМолчит() {
            var game = new GameInfo { GameId = "a", Title = "А", IsInstalled = true };
            var seen = new List<string?>();
            game.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

            game.Title = "А";
            game.IsInstalled = true;
            game.QueueLabel = string.Empty;

            Assert.Empty(seen);
        }

        /// <summary>null вместо строки — это пустая строка, а не падение привязки.</summary>
        [Fact]
        public void NullВСтроковомПолеСтановитсяПустойСтрокой() {
            var game = new GameInfo { GameId = "a", Title = "А" };

            game.Title = null!;

            Assert.Equal(string.Empty, game.Title);
        }
    }
}
