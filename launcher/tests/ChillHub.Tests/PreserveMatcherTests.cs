// <copyright file="PreserveMatcherTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Linq;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Правила preserve — механизм, из-за отказа которого проект дважды уходил в вечный
    /// цикл самообновления.
    /// <para>
    /// Логика простая: файл, попавший ОДНОВРЕМЕННО в манифест и под preserve, создаёт
    /// неустранимое расхождение. Апдейтер отказывается его перезаписывать, лаунчер видит
    /// несовпадение хешей и предлагает обновление снова — и так при каждом запуске.
    /// Так было с config.json в версиях 1.0.2 и 1.0.3 и с Uninstall.exe в 1.1.7 и 1.1.8.
    /// </para>
    /// <para>
    /// Guard-тест проверяет манифесты, но не сам матчер: до сих пор он был непокрыт.
    /// </para>
    /// </summary>
    public class PreserveMatcherTests {
        /// <summary>Набор по умолчанию защищает ровно те четыре файла, что живут в установке.</summary>
        [Theory]
        [InlineData("config.json")]
        [InlineData("launcher.version")]
        [InlineData("launcher.update-status")]
        [InlineData("Uninstall.exe")]
        public void НаборПоУмолчаниюЗащищаетСостояниеУстановки(string path) {
            Assert.True(new PreserveMatcher().ShouldPreserve(path));
        }

        /// <summary>Обычное содержимое сборки не защищается — иначе оно перестанет обновляться.</summary>
        [Theory]
        [InlineData("ChillHub.exe")]
        [InlineData("ChillHub.dll")]
        [InlineData("runtimes/win-x64/native/blake3_dotnet.dll")]
        public void СодержимоеСборкиНеЗащищается(string path) {
            Assert.False(new PreserveMatcher().ShouldPreserve(path));
        }

        /// <summary>
        /// А11: сравнивается ТОЧНЫЙ путь верхнего уровня. Раньше клиент дополнительно
        /// сравнивал имя файла в любом подкаталоге, а сервер — только точное совпадение.
        /// Из-за расхождения «data/config.json» сервер публиковал, а клиент молча
        /// пропускал: файл не обновлялся никогда.
        /// </summary>
        [Theory]
        [InlineData("data/config.json")]
        [InlineData("tools/Uninstall.exe")]
        [InlineData("sub/dir/launcher.version")]
        public void ФайлВПодкаталогеНеЗащищёнДажеПриСовпаденииИмени(string path) {
            Assert.False(
                new PreserveMatcher().ShouldPreserve(path),
                $"'{path}' — обычное содержимое сборки, правило защищает только корень");
        }

        /// <summary>Похожее имя — не то же имя.</summary>
        [Theory]
        [InlineData("myconfig.json")]
        [InlineData("config.json.bak")]
        [InlineData("launcher.version.old")]
        public void ПохожиеИменаНеЗащищаются(string path) {
            Assert.False(new PreserveMatcher().ShouldPreserve(path));
        }

        /// <summary>Регистр не имеет значения: файловая система Windows его не различает.</summary>
        [Theory]
        [InlineData("CONFIG.JSON")]
        [InlineData("Launcher.Version")]
        [InlineData("uninstall.exe")]
        public void СравнениеНеЗависитОтРегистра(string path) {
            Assert.True(new PreserveMatcher().ShouldPreserve(path));
        }

        /// <summary>Обратные слеши приводятся к прямым: пути приходят с обеих сторон.</summary>
        [Theory]
        [InlineData("\\config.json")]
        [InlineData("/config.json")]
        [InlineData("config.json")]
        public void РазделителиИКраевыеСлешиНормализуются(string path) {
            Assert.True(new PreserveMatcher().ShouldPreserve(path));
        }

        /// <summary>Пустой путь — не файл, защищать нечего.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("/")]
        public void ПустойПутьНеЗащищается(string? path) {
            Assert.False(new PreserveMatcher().ShouldPreserve(path));
        }

        /// <summary>Правило с завершающим слешем защищает весь каталог.</summary>
        [Fact]
        public void ПравилоКаталогаЗащищаетВсёВнутри() {
            var m = new PreserveMatcher("saves/");
            Assert.True(m.ShouldPreserve("saves/slot1.dat"));
            Assert.True(m.ShouldPreserve("saves/deep/nested.dat"));

            // Сам каталог без содержимого правилу «saves/» не соответствует,
            // как и файл с похожим началом имени.
            Assert.False(m.ShouldPreserve("saves2/x.dat"));
            Assert.False(m.ShouldPreserve("other/slot1.dat"));
        }

        /// <summary>Подстановочные знаки работают по всему пути.</summary>
        [Fact]
        public void ПодстановочныеЗнакиРаботают() {
            var m = new PreserveMatcher("*.log,cfg?.ini");
            Assert.True(m.ShouldPreserve("client.log"));
            Assert.True(m.ShouldPreserve("cfg1.ini"));
            Assert.False(m.ShouldPreserve("client.txt"));
            Assert.False(m.ShouldPreserve("cfg10.ini"));
        }

        /// <summary>Свой список правил полностью заменяет набор по умолчанию.</summary>
        [Fact]
        public void СвойСписокЗаменяетУмолчания() {
            var m = new PreserveMatcher("only-this.txt");
            Assert.True(m.ShouldPreserve("only-this.txt"));
            Assert.False(m.ShouldPreserve("config.json"));
        }

        /// <summary>Пустая строка правил означает набор по умолчанию, а не «ничего не защищать».</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойСписокОзначаетУмолчания(string? csv) {
            Assert.True(new PreserveMatcher(csv).ShouldPreserve("config.json"));
        }

        /// <summary>Разбор списка чистит пробелы и повторы — он приходит из командной строки.</summary>
        [Fact]
        public void РазборСпискаЧиститПробелыИПовторы() {
            var m = new PreserveMatcher(" a.txt , a.txt ,, b.txt ");
            Assert.Equal(2, m.Rules.Count);
            Assert.True(m.ShouldPreserve("a.txt"));
            Assert.True(m.ShouldPreserve("b.txt"));
        }

        /// <summary>
        /// Строка для передачи апдейтеру и сам набор правил обязаны совпадать: расхождение
        /// означало бы, что лаунчер и апдейтер защищают разные файлы.
        /// </summary>
        [Fact]
        public void СтрокаАргументаСоответствуетНаборуПравил() {
            var fromArg = new PreserveMatcher(PreserveMatcher.DefaultRulesArg).Rules;
            Assert.Equal(PreserveMatcher.DefaultRules.OrderBy(x => x), fromArg.OrderBy(x => x));
        }

        /// <summary>Артефакты самого механизма обновления опознаются и в корне, и в подкаталоге.</summary>
        [Fact]
        public void АртефактыАпдейтераОпознаются() {
            foreach (var f in PreserveMatcher.UpdaterArtifactFiles) {
                Assert.True(PreserveMatcher.IsUpdaterArtifact(f), $"'{f}' — артефакт апдейтера");
            }

            Assert.True(PreserveMatcher.IsUpdaterArtifact(PreserveMatcher.UpdaterArtifactDir + "/anything.dll"));
            Assert.False(PreserveMatcher.IsUpdaterArtifact("ChillHub.exe"));
            Assert.False(PreserveMatcher.IsUpdaterArtifact(null));
            Assert.False(PreserveMatcher.IsUpdaterArtifact(string.Empty));
        }

        /// <summary>Журналирование не влияет на решение — только объясняет его.</summary>
        [Fact]
        public void ЖурналированиеНеМеняетРешение() {
            var m = new PreserveMatcher();
            string? logged = null;
            Assert.True(m.ShouldPreserve("config.json", s => logged = s));
            Assert.NotNull(logged);
            Assert.Contains("config.json", logged!, StringComparison.Ordinal);
        }
    }
}
