// <copyright file="StuckUpdateMarkerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Выход из состояния «предыдущее обновление прервано».
    /// <para>
    /// Маркер `.updating` — единственное, что отделяет игрока от кнопки «Играть», и
    /// снять его умеет только синхронизация. Значит, у неё обязан быть путь наружу из
    /// любого состояния, при котором файлы на диске уже соответствуют манифесту.
    /// </para>
    /// </summary>
    public class StuckUpdateMarkerTests {
        /// <summary>
        /// Обновление после перезагрузки снимает маркер, оставленный отложенной заменой.
        /// <para>
        /// Занятый файл (запущенная игра, античит, антивирус) заменяется не сразу, а
        /// системой при перезагрузке; маркер при этом намеренно остаётся. Перезагрузка
        /// замену выполняет, но маркер убрать некому — и лаунчер снова показывает
        /// «требуется обновление». Пока «Обновить» с пустым планом выходил, не дойдя до
        /// снятия маркера, петля не размыкалась ничем, кроме удаления файла руками.
        /// </para>
        /// </summary>
        /// <returns>Задача теста.</returns>
        [Fact]
        public async Task ОбновлениеПослеПерезагрузкиВыводитИзСостоянияОбновлениеПрервано() {
            using var dir = new TempDir();

            // То, что осталось на диске от прогона с занятым файлом.
            SimpleSyncService.WriteRebootPendingMarker(dir.Root, "1.0.0", new List<string> { "game.exe" });
            Assert.True(SimpleSyncService.HasUpdateMarker(dir.Root));

            // Перезагрузка выполнила отложенную замену: файлы сошлись с манифестом,
            // поэтому план пуст — качать и удалять нечего.
            using var http = new HttpClient();
            var sync = new SimpleSyncService(http);
            await sync.ExecuteAsync(
                new DiffPlan { GameId = "lethal-company", Version = "1.0.0", LocalRoot = dir.Root },
                new Progress<SyncProgress>(),
                CancellationToken.None);

            Assert.False(
                SimpleSyncService.HasUpdateMarker(dir.Root),
                "маркер остался — игра требует обновления, которого нет, и выйти из этого нельзя");
        }
    }
}
