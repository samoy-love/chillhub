// <copyright file="ShortcutRequestFileTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Передача запроса ярлыка живому лаунчеру.
    /// <para>
    /// Второй экземпляр лаунчера не запускается — он только сигналит первому «покажи
    /// окно». Сигнал не несёт ничего, кроме факта, поэтому игра, на ярлык которой нажали,
    /// доезжает до живого лаунчера через этот файл. Здесь и проверяется поведение ярлыка
    /// при уже запущенном лаунчере.
    /// </para>
    /// </summary>
    public class ShortcutRequestFileTests {
        /// <summary>Записали — забрали: обычный путь ярлыка при живом лаунчере.</summary>
        [Fact]
        public void ЗаписанныйЗапросЗабираетсяЦеликом() {
            using var dir = new TempDir();
            using (ShortcutRequestFile.OverrideDirForTests(dir.Root)) {
                ShortcutRequestFile.Write(new ShortcutRequest("gid", "Игра", @"C:\Games\gid\game.exe"));

                var consumed = ShortcutRequestFile.Consume();

                Assert.NotNull(consumed);
                Assert.Equal("gid", consumed!.GameId);
                Assert.Equal("Игра", consumed.Title);
                Assert.Equal(@"C:\Games\gid\game.exe", consumed.ExePath);
            }
        }

        /// <summary>
        /// Запрос забирается ровно один раз: иначе одно нажатие на ярлык открывало бы
        /// страницу игры при каждом следующем запуске лаунчера.
        /// </summary>
        [Fact]
        public void ЗапросЗабираетсяТолькоОдинРаз() {
            using var dir = new TempDir();
            using (ShortcutRequestFile.OverrideDirForTests(dir.Root)) {
                ShortcutRequestFile.Write(new ShortcutRequest("gid", "Игра", @"C:\game.exe"));

                Assert.NotNull(ShortcutRequestFile.Consume());
                Assert.Null(ShortcutRequestFile.Consume());
                Assert.Empty(Directory.GetFiles(dir.Root));
            }
        }

        /// <summary>
        /// Протухший запрос игру не открывает: ярлык, нажатый при выключенном лаунчере,
        /// который так и не поднялся, не должен выстрелить неделю спустя.
        /// </summary>
        [Fact]
        public void ПротухшийЗапросИгноритсяИУдаляется() {
            using var dir = new TempDir();
            using (ShortcutRequestFile.OverrideDirForTests(dir.Root)) {
                var now = DateTime.UtcNow;
                ShortcutRequestFile.Write(new ShortcutRequest("gid", "Игра", @"C:\game.exe"), now.AddHours(-1));

                Assert.Null(ShortcutRequestFile.Consume(now));
                Assert.Empty(Directory.GetFiles(dir.Root));
            }
        }

        /// <summary>
        /// Запрос «из будущего» — это переведённые назад часы, а не предвидение: верить
        /// ему нельзя ровно так же, как протухшему.
        /// </summary>
        [Fact]
        public void ЗапросИзБудущегоИгнорится() {
            using var dir = new TempDir();
            using (ShortcutRequestFile.OverrideDirForTests(dir.Root)) {
                var now = DateTime.UtcNow;
                ShortcutRequestFile.Write(new ShortcutRequest("gid", "Игра", @"C:\game.exe"), now.AddHours(1));

                Assert.Null(ShortcutRequestFile.Consume(now));
            }
        }

        /// <summary>Битый файл ничего не открывает и не мешает следующему запуску.</summary>
        [Fact]
        public void БитыйЗапросУдаляетсяИНичегоНеОткрывает() {
            using var dir = new TempDir();
            using (ShortcutRequestFile.OverrideDirForTests(dir.Root)) {
                File.WriteAllText(Path.Combine(dir.Root, "shortcut_request.txt"), "не запрос вовсе");

                Assert.Null(ShortcutRequestFile.Consume());
                Assert.Empty(Directory.GetFiles(dir.Root));
            }
        }

        /// <summary>Обычный запуск лаунчера файла не оставляет.</summary>
        [Fact]
        public void БезЗапросаФайлНеПоявляется() {
            using var dir = new TempDir();
            using (ShortcutRequestFile.OverrideDirForTests(dir.Root)) {
                ShortcutRequestFile.Write(null);
                ShortcutRequestFile.Write(new ShortcutRequest("   ", "Игра", @"C:\game.exe"));

                Assert.Empty(Directory.GetFiles(dir.Root));
                Assert.Null(ShortcutRequestFile.Consume());
            }
        }
    }
}
