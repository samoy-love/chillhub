// <copyright file="GameVersionMarkerAtomicTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.IO;

    using ChillHub.Core.Home;
    using ChillHub.Core.Sync;
    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Запись маркера установленной версии `.version`.
    /// <para>
    /// Перезапись на месте — это truncate + write: между ними файл существует и он
    /// ПУСТОЙ. Обрыв в этот момент (выключили питание сразу после установки, сняли
    /// процесс) оставляет пустой маркер навсегда: он читается как «версия неизвестна»,
    /// быстрый путь проверки статуса не совпадает уже никогда, и каждый запуск лаунчера
    /// заново обходит все файлы игры. Само это не лечится — маркер переписывают только
    /// после синхронизации, а её никто не запускает, потому что игра выглядит свежей.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class GameVersionMarkerAtomicTests {
        private const string GameId = "lethal-company";

        /// <summary>
        /// Незавершённая запись оставляет прежнюю версию, а не пустой маркер.
        /// <para>
        /// Запись обрывается тем же приёмом, что и в тестах апдейтера: на месте
        /// временного файла стоит каталог, и содержимое до маркера не доходит. Проверяется
        /// главное — новая версия не записана, СТАРАЯ на месте, и лаунчер по-прежнему
        /// знает, что у игрока установлено.
        /// </para>
        /// </summary>
        [Fact]
        public void ОборваннаяЗаписьНеПортитПрежнийМаркер() {
            using var games = new GamesPathScope();
            Assert.True(GameLocalState.WriteLocalVersion(GameId, "1.0.0"));

            var marker = Path.Combine(games.Root, GameId, IntegrityChecker.VersionMarkerFileName);
            Directory.CreateDirectory(marker + AtomicFile.TempSuffix);

            Assert.False(GameLocalState.WriteLocalVersion(GameId, "2.0.0"));
            Assert.Equal("1.0.0", GameLocalState.ReadLocalVersion(GameId));
        }

        /// <summary>
        /// После обычной записи в папке игры не остаётся временного файла: иначе
        /// синхронизация сочла бы его лишним, а игрок увидел бы мусор рядом с игрой.
        /// </summary>
        [Fact]
        public void ВременныйФайлПослеЗаписиНеОстаётся() {
            using var games = new GamesPathScope();

            Assert.True(GameLocalState.WriteLocalVersion(GameId, "1.0.0"));
            Assert.True(GameLocalState.WriteLocalVersion(GameId, "2.0.0"));

            var marker = Path.Combine(games.Root, GameId, IntegrityChecker.VersionMarkerFileName);
            Assert.Equal("2.0.0", GameLocalState.ReadLocalVersion(GameId));
            Assert.False(File.Exists(marker + AtomicFile.TempSuffix));
        }
    }
}
