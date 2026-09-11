// <copyright file="UpdaterShortcutRefreshTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;

    using Xunit;

    /// <summary>
    /// Какие ярлыки апдейтер просит оболочку перерисовать после смены значка.
    /// <para>
    /// Промах здесь не падает ничем: обновление проходит, а на рабочем столе
    /// остаётся старая картинка. Поэтому поиск ярлыков проверяется на настоящих
    /// папках.
    /// </para>
    /// </summary>
    public class UpdaterShortcutRefreshTests : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "chillhub-lnk-" + Guid.NewGuid().ToString("N"));

        /// <inheritdoc/>
        public void Dispose() {
            try {
                Directory.Delete(this.root, true);
            }
            catch (IOException) {
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>Берутся ярлыки Chill Hub под обоими именами — нынешним и прежним.</summary>
        [Fact]
        public void НаходитЯрлыкиПодОбоимиИменами() {
            var desk = this.Dir("desk");
            this.Touch(desk, "Chill Hub.lnk");
            this.Touch(desk, "ChillHub.lnk");
            this.Touch(desk, "Steam.lnk");
            this.Touch(desk, "Chill Hub.txt");

            var found = global::Program.UpdaterHost.ShortcutsToRefresh(new[] { (desk, false) });

            Assert.Equal(
                new[] { "Chill Hub.lnk", "ChillHub.lnk" },
                found.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        /// <summary>
        /// Рабочий стол — без вложенных папок: там бывают тысячи чужих файлов. А «Пуск»
        /// кладёт ярлык в подпапку «Chill Hub», и без обхода вложенных его не найти.
        /// </summary>
        [Fact]
        public void ВложенныеПапкиОбходятсяТолькоГдеСказано() {
            var desk = this.Dir("desk");
            var start = this.Dir("start");
            this.Touch(Path.Combine(desk, "Архив"), "Chill Hub.lnk");
            this.Touch(Path.Combine(start, "Chill Hub"), "Chill Hub.lnk");

            var found = global::Program.UpdaterHost.ShortcutsToRefresh(new[] { (desk, false), (start, true) });

            var only = Assert.Single(found);
            Assert.StartsWith(start, only, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Пустые и несуществующие папки не мешают найти ярлыки в остальных.</summary>
        [Fact]
        public void ОтсутствующаяПапкаНеМешаетОстальным() {
            var desk = this.Dir("desk");
            this.Touch(desk, "Chill Hub.lnk");

            var found = global::Program.UpdaterHost.ShortcutsToRefresh(new[]
            {
                (string.Empty, false),
                (Path.Combine(this.root, "нет-такой"), true),
                (desk, false),
            });

            Assert.Single(found);
        }

        private string Dir(string name) {
            var dir = Path.Combine(this.root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void Touch(string dir, string name) {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), string.Empty);
        }
    }
}
