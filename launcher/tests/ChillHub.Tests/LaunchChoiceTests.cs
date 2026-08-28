// <copyright file="LaunchChoiceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Core;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Память о том, чем игрок запускает игру с модами.
    /// <para>
    /// Вариантов запуска четыре, играют почти всегда одним. Без памяти каждый запуск
    /// стоил двух кликов через меню, и меню это открывалось на кнопке «Играть» —
    /// то есть кнопка не делала того, что на ней написано.
    /// </para>
    /// </summary>
    public class LaunchChoiceTests : IDisposable {
        private readonly string appDir;
        private readonly string legacyDir;
        private readonly IDisposable scope;

        /// <summary>Уводит конфиг во временные каталоги на время класса тестов.</summary>
        public LaunchChoiceTests() {
            var root = Path.Combine(Path.GetTempPath(), "ChillHubLaunchChoice", Guid.NewGuid().ToString("N"));
            this.appDir = Path.Combine(root, "app");
            this.legacyDir = Path.Combine(root, "legacy");
            Directory.CreateDirectory(this.appDir);
            Directory.CreateDirectory(this.legacyDir);
            this.scope = ConfigService.OverrideForTests(this.appDir, this.legacyDir);
        }

        /// <inheritdoc/>
        public void Dispose() {
            this.scope.Dispose();
            try {
                Directory.Delete(Path.GetDirectoryName(this.appDir)!, true);
            }
            catch (IOException) {
                // Временный каталог не удалился — прогону это не мешает.
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>Ничего не выбирали — спрашивать надо.</summary>
        [Fact]
        public void БезВыбораПамятиНет() {
            Assert.Null(LaunchChoice.Remembered("lethal-company"));
            Assert.Null(LaunchChoice.Remembered(null));
            Assert.Null(LaunchChoice.Remembered("   "));
        }

        /// <summary>Выбор переживает перезапуск: он пишется в конфиг, а не в поле.</summary>
        [Fact]
        public void ВыборЗапоминается() {
            LaunchChoice.Remember("lethal-company", LaunchTarget.SteamModded);
            ConfigService.InvalidateCache();

            Assert.Equal(LaunchTarget.SteamModded, LaunchChoice.Remembered("lethal-company"));
        }

        /// <summary>Память у каждой игры своя.</summary>
        [Fact]
        public void ПамятьРазделенаПоИграм() {
            LaunchChoice.Remember("lethal-company", LaunchTarget.SteamModded);
            LaunchChoice.Remember("how-to-fish", LaunchTarget.LocalVanilla);

            Assert.Equal(LaunchTarget.SteamModded, LaunchChoice.Remembered("lethal-company"));
            Assert.Equal(LaunchTarget.LocalVanilla, LaunchChoice.Remembered("how-to-fish"));
        }

        /// <summary>Мусор в конфиге не роняет запуск и читается как «не выбирали».</summary>
        [Fact]
        public void НепонятноеЗначениеЧитаетсяКакОтсутствие() {
            ConfigService.Current.LaunchTargets = new Dictionary<string, string> {
                ["lethal-company"] = "SteamModdedButNot",
            };

            Assert.Null(LaunchChoice.Remembered("lethal-company"));
        }

        /// <summary>
        /// Запомненный, но СЕЙЧАС недоступный вариант не запускается молча: игру могли
        /// удалить из Steam, и подставить вместо неё другую копию — худший исход.
        /// </summary>
        [Fact]
        public void НедоступныйЗапомненныйВариантНеПодставляется() {
            LaunchChoice.Remember("game", LaunchTarget.SteamModded);
            var options = new List<LaunchOption> {
                new(LaunchTarget.SteamModded, "Steam · с модами", @"C:\s", true, false, "копия в Steam не найдена"),
                new(LaunchTarget.LocalVanilla, "Сборка · без модов", @"C:\l", false, true, string.Empty),
            };

            Assert.Null(LaunchChoice.Preferred("game", options));
        }

        /// <summary>Доступный запомненный вариант — тот, что стартует по «Играть».</summary>
        [Fact]
        public void ДоступныйЗапомненныйВариантВозвращается() {
            LaunchChoice.Remember("game", LaunchTarget.LocalVanilla);
            var options = new List<LaunchOption> {
                new(LaunchTarget.SteamModded, "Steam · с модами", @"C:\s", true, true, string.Empty),
                new(LaunchTarget.LocalVanilla, "Сборка · без модов", @"C:\l", false, true, string.Empty),
            };

            var chosen = LaunchChoice.Preferred("game", options);

            Assert.NotNull(chosen);
            Assert.Equal(LaunchTarget.LocalVanilla, chosen!.Target);
        }

        /// <summary>Подпись варианта называет и копию, и модпак — она идёт на подсказку кнопки.</summary>
        [Fact]
        public void ПодписьВариантаНазываетМодпак() {
            var mods = new ModsInfo { HasLatest = true, DisplayName = "Lethal Reloaded", DisplayVersion = "2.2.12" };

            Assert.Equal("Steam · с модами (Lethal Reloaded 2.2.12)", ModsLaunch.TitleOf(LaunchTarget.SteamModded, mods));
            Assert.Equal("Steam · без модов", ModsLaunch.TitleOf(LaunchTarget.SteamVanilla, mods));
            Assert.Equal("Сборка Chill Hub · без модов", ModsLaunch.TitleOf(LaunchTarget.LocalVanilla, null));
        }
    }
}
