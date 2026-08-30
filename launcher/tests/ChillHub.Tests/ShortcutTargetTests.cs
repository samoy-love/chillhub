// <copyright file="ShortcutTargetTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Командная строка ярлыка игры.
    /// <para>
    /// Ярлык ведёт в лаунчер, а не в игру, и всё, что он несёт, — эта строка. Соберётся
    /// она не так, как разберётся, — и нажатие на ярлык открывает каталог вместо игры
    /// (или, того хуже, окно «игры больше нет» на игре, которая на месте).
    /// </para>
    /// </summary>
    public class ShortcutTargetTests {
        /// <summary>Что собрали, то и разбираем: пути с пробелами — обычное дело.</summary>
        [Fact]
        public void СобранноеРазбираетсяОбратно() {
            var args = ShortcutTarget.BuildArguments("gid", "Half-Life: Alyx", @"C:\Games\Chill Hub\gid\game.exe");

            var parsed = ShortcutTarget.Parse(Split(args));

            Assert.NotNull(parsed);
            Assert.Equal("gid", parsed!.GameId);
            Assert.Equal("Half-Life: Alyx", parsed.Title);
            Assert.Equal(@"C:\Games\Chill Hub\gid\game.exe", parsed.ExePath);
        }

        /// <summary>
        /// Без игры строки нет вовсе: ярлык с одним лишь путём к exe открывал бы лаунчер
        /// «просто так», не понимая, о какой игре речь.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void БезИгрыАргументовНет(string? gameId)
            => Assert.Equal(string.Empty, ShortcutTarget.BuildArguments(gameId, "Игра", @"C:\game.exe"));

        /// <summary>Название и путь необязательны: без них ярлык всё равно откроет страницу игры.</summary>
        [Fact]
        public void БезНазванияИПутиОстаётсяТолькоИгра() {
            var args = ShortcutTarget.BuildArguments("gid", null, null);

            Assert.Equal("--game \"gid\"", args);
            Assert.Equal("gid", ShortcutTarget.Parse(Split(args))!.GameId);
        }

        /// <summary>
        /// Кавычка в названии выбрасывается: экранировать её в строке аргументов нечем, и
        /// оболочка разобрала бы такую строку не так, как мы её собирали.
        /// </summary>
        [Fact]
        public void КавычкиИзНазванияНеЛомаютСтроку() {
            var args = ShortcutTarget.BuildArguments("gid", "Игра \"Про\" всё", @"C:\game.exe");

            var parsed = ShortcutTarget.Parse(Split(args));

            Assert.Equal("Игра Про всё", parsed!.Title);
            Assert.Equal(@"C:\game.exe", parsed.ExePath);
        }

        /// <summary>
        /// Чужие ключи молча пропускаются: лаунчер запускают и установщик, и апдейтер, и
        /// человек из консоли — падать на незнакомом аргументе ярлыку незачем.
        /// </summary>
        [Fact]
        public void ЧужиеАргументыНеМешают() {
            var parsed = ShortcutTarget.Parse(new[] { "--updated", "1.6.25", "--game", "gid", "--verbose" });

            Assert.Equal("gid", parsed!.GameId);
            Assert.Equal(string.Empty, parsed.ExePath);
        }

        /// <summary>Обычный запуск лаунчера — не запрос ярлыка.</summary>
        [Fact]
        public void БезАргументовЗапросаНет() {
            Assert.Null(ShortcutTarget.Parse(null));
            Assert.Null(ShortcutTarget.Parse(System.Array.Empty<string>()));
            Assert.Null(ShortcutTarget.Parse(new[] { "--updated", "1.6.25" }));
        }

        /// <summary>Значение через '=' — то же самое значение: так пишут руками из консоли.</summary>
        [Fact]
        public void ЗначениеЧерезРавноТожеПонимается() {
            var parsed = ShortcutTarget.Parse(new[] { "--game=gid", @"--exe=C:\game.exe" });

            Assert.Equal("gid", parsed!.GameId);
            Assert.Equal(@"C:\game.exe", parsed.ExePath);
        }

        /// <summary>Ключ без значения не съедает следующий ключ.</summary>
        [Fact]
        public void КлючБезЗначенияНеЗабираетСледующийКлюч() {
            var parsed = ShortcutTarget.Parse(new[] { "--title", "--game", "gid" });

            Assert.Equal("gid", parsed!.GameId);
            Assert.Equal(string.Empty, parsed.Title);
        }

        /// <summary>
        /// Разбивает строку аргументов так же, как это делает Windows перед передачей
        /// программе: по пробелам вне кавычек.
        /// </summary>
        /// <param name="commandLine">Строка аргументов ярлыка.</param>
        /// <returns>Аргументы по одному.</returns>
        private static string[] Split(string commandLine) {
            var parts = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;
            foreach (var c in commandLine) {
                if (c == '"') {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == ' ' && !inQuotes) {
                    if (current.Length > 0) {
                        parts.Add(current.ToString());
                        current.Clear();
                    }

                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) {
                parts.Add(current.ToString());
            }

            return parts.ToArray();
        }
    }
}
