// <copyright file="UpdaterPathPlanTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using Xunit;

    /// <summary>
    /// Проверка списков обновления перед их применением.
    /// <para>
    /// filelist/emptydirs/deletelist — это данные, пришедшие с сервера. Апдейтер
    /// подставляет каждую строку в путь внутри папки установки и открывает результат
    /// на запись или удаление, работая с правами пользователя, а после UAC — и выше.
    /// Строка вида «../../..» или «C:/Windows/System32/...» уводит запись за пределы
    /// установки: в автозагрузку, в системный каталог, в чужой профиль.
    /// </para>
    /// <para>
    /// Проверка обязана отработать ДО первой операции и отвергнуть обновление целиком:
    /// наполовину применённое обновление хуже неприменённого — откатить его уже нечем.
    /// </para>
    /// </summary>
    public class UpdaterValidateListsTests {
        /// <summary>Нормальные относительные пути проходят — иначе не применится ни одно обновление.</summary>
        [Fact]
        public void НормальныеПутиПроходят() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "ChillHub.exe\r\nsub/dir/lib.dll\r\nruntimes/win-x64/native/x.dll\r\n");

            Assert.True(Validate(dir, files));
        }

        /// <summary>Строки списков приходят и с обратными слешами: они нормализуются, а не отвергаются.</summary>
        [Fact]
        public void ОбратныеСлешиНормализуются() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "sub\\dir\\lib.dll\r\n");

            Assert.True(Validate(dir, files));
        }

        /// <summary>
        /// Выход за пределы папки установки — главный сценарий, ради которого
        /// проверка и существует: такая строка кладёт файл в автозагрузку.
        /// </summary>
        [Theory]
        [InlineData("../evil.exe")]
        [InlineData("sub/../../evil.exe")]
        [InlineData("..\\..\\..\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\x.exe")]
        [InlineData("./ChillHub.exe")]
        public void ВыходЗаПределыУстановкиОтвергается(string line) {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", line + "\r\n");

            Assert.False(Validate(dir, files));
        }

        /// <summary>Абсолютный путь игнорирует корень целиком — Path.Combine просто отдаст его как есть.</summary>
        [Theory]
        [InlineData("C:/Windows/System32/evil.dll")]
        [InlineData("C:\\Windows\\System32\\evil.dll")]
        [InlineData("C:evil.dll")]
        [InlineData("ChillHub.exe:поток")]
        public void АбсолютныйПутьИПотокNTFSОтвергаются(string line) {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", line + "\r\n");

            Assert.False(Validate(dir, files));
        }

        /// <summary>
        /// Пустая строка — это не путь, и она пропускается, а не отвергает список.
        /// Это обязано совпадать с поведением самих циклов копирования и удаления:
        /// разойдись они, апдейтер отказывался бы применять совершенно корректный
        /// список из-за хвостового перевода строки, который есть в любом файле.
        /// </summary>
        [Fact]
        public void ПустыеСтрокиПропускаются() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "\r\nChillHub.exe\r\n\r\n   \r\n///\r\n");

            Assert.True(Validate(dir, files));
        }

        /// <summary>Отсутствующий или не заданный список — штатная ситуация (полный пакет без диффа).</summary>
        [Fact]
        public void ОтсутствующиеСпискиПропускаются() {
            using var dir = new TempDir();

            Assert.True(global::Program.ValidateLists(
                new string?[] { null, string.Empty, "   ", dir.PathTo("нет-такого.txt") },
                string.Empty,
                _ => { }));
        }

        /// <summary>
        /// Нечитаемый список — это отказ, а не «пропускаем». «Не проверено» не равно
        /// «безопасно»: применить непрочитанный список означало бы применить неизвестно что.
        /// </summary>
        [Fact]
        public void НечитаемыйСписокОтвергается() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "ChillHub.exe\r\n");

            using var hold = new FileStream(files, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.False(Validate(dir, files));
        }

        /// <summary>Проверяются ВСЕ переданные списки, а не только первый: удаления опаснее копирования.</summary>
        [Fact]
        public void ПроверяетсяКаждыйСписок() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "ChillHub.exe\r\n");
            var dirs = dir.WriteFile("emptydirs.txt", "data/cache\r\n");
            var del = dir.WriteFile("deletelist.txt", "../../важное.txt\r\n");

            Assert.False(global::Program.ValidateLists(new string?[] { files, dirs, del }, string.Empty, _ => { }));
        }

        /// <summary>
        /// Проверка не останавливается на первой плохой строке: в лог должны попасть
        /// все, иначе разбор битого обновления превращается в отгадывание по одной строке за релиз.
        /// </summary>
        [Fact]
        public void ВЛогПопадаютВсеПлохиеСтроки() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "../a.exe\r\nok.dll\r\nC:/b.exe\r\n");
            var log = new List<string>();

            Assert.False(global::Program.ValidateLists(new string?[] { files }, string.Empty, log.Add));

            Assert.Equal(2, log.FindAll(l => l.Contains("REJECT", StringComparison.Ordinal)).Count);
            Assert.Contains(log, l => l.Contains("строка 1", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("строка 3", StringComparison.Ordinal));
        }

        /// <summary>
        /// strip-prefix проверяется наравне со строками списков: он подставляется
        /// в тот же путь, и «..» в нём уводит наружу ровно так же.
        /// </summary>
        [Theory]
        [InlineData("..")]
        [InlineData("../../Startup")]
        [InlineData("C:/Windows")]
        [InlineData("pkg:поток")]
        public void НебезопасныйStripPrefixОтвергается(string strip) {
            Assert.False(global::Program.ValidateLists(Array.Empty<string?>(), strip, _ => { }));
        }

        /// <summary>Нормальный strip-prefix, в том числе записанный с краевыми слешами, принимается.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ChillHub-1.2.3")]
        [InlineData("/ChillHub-1.2.3/")]
        [InlineData("pkg/inner")]
        public void ДопустимыйStripPrefixПринимается(string strip) {
            Assert.True(global::Program.ValidateLists(Array.Empty<string?>(), strip, _ => { }));
        }

        // Прогоняет один список файлов через проверку, отбрасывая журнал.
        private static bool Validate(TempDir dir, string files)
            => global::Program.ValidateLists(new string?[] { files }, string.Empty, _ => { });
    }

    /// <summary>
    /// Общий префикс архива (strip-prefix).
    /// <para>
    /// Архив обновления часто распакован «с папкой внутри»: ChillHub-1.2.3/ChillHub.exe.
    /// Апдейтер обязан снять этот сегмент, иначе вся новая сборка ляжет в подпапку
    /// установки, а запускаемый лаунчер останется старым — обновление будет
    /// предлагаться при каждом старте. Ошибка в обратную сторону не лучше: снятый
    /// «префикс», которого на самом деле нет, разложит файлы на уровень выше нужного.
    /// </para>
    /// </summary>
    public class UpdaterStripPrefixTests {
        /// <summary>Без префикса путь не меняется — это самый частый случай (архив без обёртки).</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void БезПрефиксаПутьНеМеняется(string? strip) {
            Assert.Equal("sub/ChillHub.exe", global::Program.StripOf("sub/ChillHub.exe", strip!));
        }

        /// <summary>Совпавший префикс снимается вместе с разделителем.</summary>
        [Fact]
        public void СовпавшийПрефиксСнимается() {
            Assert.Equal("ChillHub.exe", global::Program.StripOf("ChillHub-1.2.3/ChillHub.exe", "ChillHub-1.2.3"));
            Assert.Equal("sub/lib.dll", global::Program.StripOf("pkg/sub/lib.dll", "pkg"));
        }

        /// <summary>Регистр не важен: имя папки в архиве и вычисленный префикс приходят из разных источников.</summary>
        [Fact]
        public void РегистрПрефиксаНеВажен() {
            Assert.Equal("ChillHub.exe", global::Program.StripOf("PKG/ChillHub.exe", "pkg"));
        }

        /// <summary>
        /// Префикс — это ЦЕЛЫЙ сегмент пути. Иначе «pkg» срезал бы начало у «pkg-extra/»,
        /// и файлы соседней папки легли бы поверх корня установки.
        /// </summary>
        [Theory]
        [InlineData("pkg-extra/lib.dll")]
        [InlineData("pkg2/lib.dll")]
        [InlineData("other/pkg/lib.dll")]
        public void ЧастичноеСовпадениеИмениНеСчитается(string rel) {
            Assert.Equal(rel, global::Program.StripOf(rel, "pkg"));
        }

        /// <summary>Файл, у которого путь равен самому префиксу, не превращается в пустую строку.</summary>
        [Fact]
        public void ПутьРавныйПрефиксуНеОбрезается() {
            Assert.Equal("pkg", global::Program.StripOf("pkg", "pkg"));
        }

        /// <summary>Снимается ровно одно вхождение: «pkg/pkg/x» — это подпапка с тем же именем, а не двойная обёртка.</summary>
        [Fact]
        public void СнимаетсяТолькоОдноВхождение() {
            Assert.Equal("pkg/lib.dll", global::Program.StripOf("pkg/pkg/lib.dll", "pkg"));
        }

        /// <summary>Многоуровневый префикс снимается целиком.</summary>
        [Fact]
        public void МногоуровневыйПрефиксСнимается() {
            Assert.Equal("lib.dll", global::Program.StripOf("pkg/inner/lib.dll", "pkg/inner"));
        }
    }

    /// <summary>
    /// Автоопределение общего префикса архива.
    /// <para>
    /// Определять префикс приходится потому, что упаковщики кладут содержимое
    /// по-разному. Цена ошибки несимметрична: не найденный префикс оставляет всю
    /// сборку в подпапке (лаунчер не обновится никогда), а придуманный — раскидывает
    /// файлы мимо папки установки. Поэтому кандидат принимается, только если он
    /// подтверждён с ДВУХ сторон: он общий для всех строк списка И реально существует
    /// в распакованном источнике.
    /// </para>
    /// </summary>
    public class UpdaterDetectStripPrefixTests {
        /// <summary>Единый корень во всех строках списка, подтверждённый папкой в источнике, — это префикс.</summary>
        [Fact]
        public void ЕдиныйКореньСпискаПодтверждённыйНаДискеПринимается() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub-1.2.3/ChillHub.exe", "x");
            dir.WriteFile("src/ChillHub-1.2.3/sub/lib.dll", "y");
            var files = dir.WriteFile("filelist.txt", "ChillHub-1.2.3/ChillHub.exe\r\nChillHub-1.2.3/sub/lib.dll\r\n");

            Assert.Equal("ChillHub-1.2.3", global::Program.DetectStripPrefix(dir.PathTo("src"), files));
        }

        /// <summary>Обратные слеши в списке не мешают распознать общий корень.</summary>
        [Fact]
        public void ОбратныеСлешиВСпискеНеМешают() {
            using var dir = new TempDir();
            dir.WriteFile("src/pkg/a.dll", "x");
            var files = dir.WriteFile("filelist.txt", "pkg\\a.dll\r\npkg\\sub\\b.dll\r\n");

            Assert.Equal("pkg", global::Program.DetectStripPrefix(dir.PathTo("src"), files));
        }

        /// <summary>
        /// Кандидат, которого нет на диске, отвергается. Это и есть защита от
        /// «придуманного» префикса: снять несуществующую обёртку — значит промахнуться
        /// мимо папки установки на уровень.
        /// </summary>
        [Fact]
        public void КандидатБезПапкиВИсточникеОтвергается() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub.exe", "x");
            dir.WriteFile("src/lib.dll", "y");
            var files = dir.WriteFile("filelist.txt", "pkg/ChillHub.exe\r\npkg/lib.dll\r\n");

            Assert.Null(global::Program.DetectStripPrefix(dir.PathTo("src"), files));
        }

        /// <summary>Разные корни в списке означают, что общей обёртки нет.</summary>
        [Fact]
        public void РазныеКорниПрефиксаНеДают() {
            using var dir = new TempDir();
            dir.WriteFile("src/a/x.dll", "x");
            dir.WriteFile("src/b/y.dll", "y");
            var files = dir.WriteFile("filelist.txt", "a/x.dll\r\nb/y.dll\r\n");

            Assert.Null(global::Program.DetectStripPrefix(dir.PathTo("src"), files));
        }

        /// <summary>
        /// Файл в корне архива с именем будущей папки — не обёртка. Раньше такой
        /// случай легко принять за префикс: первый сегмент у всех строк один и тот же.
        /// </summary>
        [Fact]
        public void ФайлВКорнеНеСчитаетсяОбёрткой() {
            using var dir = new TempDir();
            dir.WriteFile("src/pkg", "это файл, а не папка");
            var files = dir.WriteFile("filelist.txt", "pkg\r\npkg/lib.dll\r\n");

            Assert.Null(global::Program.DetectStripPrefix(dir.PathTo("src"), files));
        }

        /// <summary>Без списка файлов работает запасной путь: ровно одна папка в корне источника и ни одного файла.</summary>
        [Fact]
        public void БезСпискаПрефиксБерётсяИзСтруктурыИсточника() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub-1.2.3/ChillHub.exe", "x");

            Assert.Equal("ChillHub-1.2.3", global::Program.DetectStripPrefix(dir.PathTo("src"), string.Empty));
        }

        /// <summary>
        /// Файл рядом с папкой в корне источника отменяет запасное определение:
        /// значит содержимое лежит прямо в корне, и снимать нечего.
        /// </summary>
        [Fact]
        public void ФайлВКорнеИсточникаОтменяетЗапаснойПуть() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub.exe", "x");
            dir.WriteFile("src/runtimes/win-x64/native.dll", "y");

            Assert.Null(global::Program.DetectStripPrefix(dir.PathTo("src"), string.Empty));
        }

        /// <summary>Две папки в корне источника — тоже не обёртка.</summary>
        [Fact]
        public void ДвеПапкиВКорнеИсточникаПрефиксаНеДают() {
            using var dir = new TempDir();
            dir.WriteFile("src/a/x.dll", "x");
            dir.WriteFile("src/b/y.dll", "y");

            Assert.Null(global::Program.DetectStripPrefix(dir.PathTo("src"), string.Empty));
        }

        /// <summary>Несуществующий источник и битый список не роняют апдейтер — просто «префикса нет».</summary>
        [Fact]
        public void НесуществующийИсточникДаётNull() {
            using var dir = new TempDir();

            Assert.Null(global::Program.DetectStripPrefix(dir.PathTo("нет-такого"), dir.PathTo("нет-списка.txt")));
        }

        /// <summary>Пустой список файлов не должен выглядеть как «общий корень найден».</summary>
        [Fact]
        public void ПустойСписокНеДаётПрефикса() {
            using var dir = new TempDir();
            dir.WriteFile("src/a/x.dll", "x");
            dir.WriteFile("src/b/y.dll", "y");
            var files = dir.WriteFile("filelist.txt", "\r\n   \r\n");

            Assert.Null(global::Program.DetectStripPrefix(dir.PathTo("src"), files));
        }
    }
}
