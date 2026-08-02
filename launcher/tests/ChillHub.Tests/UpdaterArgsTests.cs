// <copyright file="UpdaterArgsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Разбор командной строки апдейтера.
    /// <para>
    /// Апдейтер — отдельный процесс, который лаунчер запускает уже перед собственным
    /// закрытием. Диалога с пользователем у него нет: если аргумент понят неверно,
    /// обновление либо не применится вовсе, либо применится не туда, а увидит это
    /// только тот, кто найдёт лог. Отдельно опасен --parent: неверно разобранный,
    /// он означает копирование поверх ЖИВОГО лаунчера с залоченными файлами.
    /// </para>
    /// </summary>
    public class UpdaterArgsTests {
        /// <summary>Запуск вообще без аргументов не должен ронять разбор — дальше сработает проверка обязательных опций.</summary>
        [Fact]
        public void ПустаяКоманднаяСтрокаДаётПустойНабор() {
            Assert.Empty(global::Program.ParseArgs(Array.Empty<string>()));
        }

        /// <summary>Обычный набор опций разбирается по парам «ключ — значение».</summary>
        [Fact]
        public void КлючиИЗначенияРазбираютсяПопарно() {
            var map = global::Program.ParseArgs(new[] { "--src", @"C:\tmp\src", "--dst", @"C:\app", "--parent", "1234" });

            Assert.Equal(@"C:\tmp\src", map["--src"]);
            Assert.Equal(@"C:\app", map["--dst"]);
            Assert.Equal("1234", map["--parent"]);
        }

        /// <summary>
        /// Путь с пробелами приходит в аргументах ОДНИМ элементом массива: расклейкой
        /// занимается операционная система. Любая попытка склеить его обратно по пробелам
        /// разрезала бы «C:\Program Files\ChillHub» пополам.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Program Files\ChillHub")]
        [InlineData(@"C:\Users\Вася Пупкин\AppData\Local\ChillHub")]
        [InlineData("значение \"в кавычках\" целиком")]
        public void ЗначениеПередаётсяДословно(string value) {
            // Кавычки и пробелы внутри значения — часть пути, а не разделители.
            Assert.Equal(value, global::Program.ParseArgs(new[] { "--dst", value })["--dst"]);
        }

        /// <summary>
        /// Ключ без значения даёт null, а не «съедает» следующий ключ. Иначе
        /// «--exe-args-file --dst C:\app» потеряло бы каталог установки, и апдейтер
        /// стал бы писать неизвестно куда.
        /// </summary>
        [Fact]
        public void КлючБезЗначенияНеСъедаетСледующийКлюч() {
            var map = global::Program.ParseArgs(new[] { "--exe-args-file", "--dst", @"C:\app" });

            Assert.Null(map["--exe-args-file"]);
            Assert.Equal(@"C:\app", map["--dst"]);
        }

        /// <summary>Последний ключ без значения — не повод потерять весь разбор.</summary>
        [Fact]
        public void ПоследнийКлючБезЗначенияДаётNull() {
            var map = global::Program.ParseArgs(new[] { "--dst", @"C:\app", "--auto-strip" });

            Assert.Equal(@"C:\app", map["--dst"]);
            Assert.Null(map["--auto-strip"]);
            Assert.Equal(2, map.Count);
        }

        /// <summary>
        /// Дубль ключа выигрывает последним. Это важно знать наверняка: лаунчер
        /// добавляет опции к общему шаблону команды, и «побеждает первый» означало бы,
        /// что уточнение молча игнорируется.
        /// </summary>
        [Fact]
        public void ПовторныйКлючПерекрываетПредыдущий() {
            var map = global::Program.ParseArgs(new[] { "--dst", @"C:\old", "--dst", @"C:\new" });

            Assert.Single(map);
            Assert.Equal(@"C:\new", map["--dst"]);
        }

        /// <summary>Повтор без значения тоже перекрывает: «--dst C:\app --dst» не должен выглядеть как валидный каталог.</summary>
        [Fact]
        public void ПовторБезЗначенияСбрасываетЗначение() {
            Assert.Null(global::Program.ParseArgs(new[] { "--dst", @"C:\app", "--dst" })["--dst"]);
        }

        /// <summary>Регистр ключа не важен: команда собирается в разных местах (C#, .cmd, NSIS).</summary>
        [Fact]
        public void КлючиНечувствительныКРегистру() {
            var map = global::Program.ParseArgs(new[] { "--DST", @"C:\app" });

            Assert.Equal(@"C:\app", map["--dst"]);
            Assert.Equal(@"C:\app", map["--Dst"]);
        }

        /// <summary>Неизвестный ключ сохраняется, но ни на что не влияет — апдейтер не обязан падать от лишней опции.</summary>
        [Fact]
        public void НеизвестныйКлючСохраняетсяИНеМешает() {
            var map = global::Program.ParseArgs(new[] { "--совершенно-новая-опция", "42", "--dst", @"C:\app" });

            Assert.Equal("42", map["--совершенно-новая-опция"]);
            Assert.Equal(@"C:\app", map["--dst"]);
        }

        /// <summary>Токен без «--» в начале строки — мусор, а не значение: он не должен становиться ключом.</summary>
        [Fact]
        public void ПозиционныйТокенИгнорируется() {
            var map = global::Program.ParseArgs(new[] { "мусор", "--dst", @"C:\app" });

            Assert.Single(map);
            Assert.Equal(@"C:\app", map["--dst"]);
        }

        /// <summary>Одиночный дефис — часть значения, а не признак ключа: иначе отрицательные и дефисные значения потерялись бы.</summary>
        [Theory]
        [InlineData("-1")]
        [InlineData("-")]
        [InlineData("-версия")]
        public void ОдиночныйДефисОстаётсяЗначением(string value) {
            Assert.Equal(value, global::Program.ParseArgs(new[] { "--parent", value })["--parent"]);
        }

        /// <summary>Пустая строка как значение сохраняется — она отличима от «значения не было».</summary>
        [Fact]
        public void ПустаяСтрокаЭтоЗначениеАНеОтсутствие() {
            Assert.Equal(string.Empty, global::Program.ParseArgs(new[] { "--version", string.Empty })["--version"]);
        }
    }

    /// <summary>
    /// Разбор --parent: идентификатора процесса лаунчера, выхода которого апдейтер обязан дождаться.
    /// <para>
    /// A13. Пока лаунчер жив, его exe и dll заблокированы, и копирование поверх них
    /// проваливается. Ждать нечего только при parent=0 — и раньше именно этот ноль
    /// подставлялся молча, стоило значению не разобраться. Хуже того, значение
    /// теряется незаметно: разбор командной строки считает следующий токен с «--»
    /// новым ключом, поэтому «--parent --dst C:\app» даёт у --parent ровно null,
    /// а «0» после него выглядит как честно переданный ноль.
    /// </para>
    /// <para>
    /// Что видит пользователь, если ноль подставить: апдейтер начинает копировать
    /// поверх РАБОТАЮЩЕГО лаунчера, часть файлов залочена, обновление
    /// откатывается — и так при каждом запуске, без внятной причины в логе.
    /// Поэтому непонятый --parent обязан быть фатальной ошибкой.
    /// </para>
    /// </summary>
    public class UpdaterParentPidTests {
        /// <summary>Нормальный pid разбирается — иначе не применится ни одно обновление.</summary>
        [Theory]
        [InlineData("1234", 1234)]
        [InlineData(" 1234 ", 1234)]
        [InlineData("2147483647", int.MaxValue)]
        public void ЧисловойИдентификаторРазбирается(string raw, int expected) {
            Assert.True(global::Program.TryParseParentPid(raw, out var pid, out var problem));
            Assert.Equal(expected, pid);
            Assert.Equal(string.Empty, problem);
        }

        /// <summary>
        /// Явный ноль — законное «ждать некого» (лаунчер уже не запущен). Отличие от
        /// дефекта в том, что этот ноль передал человек, а не подставил разбор.
        /// </summary>
        [Fact]
        public void ЯвныйНольДопустим() {
            Assert.True(global::Program.TryParseParentPid("0", out var pid, out _));
            Assert.Equal(0, pid);
        }

        /// <summary>
        /// ГЛАВНОЕ. Значения нет вовсе (ключ «съеден» следующим ключом) — это отказ,
        /// а не ноль. Молчаливый ноль здесь означает копирование поверх живого лаунчера.
        /// </summary>
        [Fact]
        public void ОтсутствующееЗначениеЭтоОтказАНеНоль() {
            Assert.False(global::Program.TryParseParentPid(null, out var pid, out var problem));
            Assert.Equal(0, pid);
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        /// <summary>Пустое значение и пробелы — тоже отказ: ждать по ним нечего.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void ПустоеЗначениеОтвергается(string raw) {
            Assert.False(global::Program.TryParseParentPid(raw, out _, out var problem));
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        /// <summary>Мусор вместо числа отвергается — включая «почти числа», которые легко получить склейкой команды.</summary>
        [Theory]
        [InlineData("abc")]
        [InlineData("12.5")]
        [InlineData("1 2")]
        [InlineData("0x10")]
        [InlineData("99999999999999999999")]
        [InlineData("--dst")]
        public void НечисловоеЗначениеОтвергается(string raw) {
            Assert.False(global::Program.TryParseParentPid(raw, out _, out var problem));
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        /// <summary>Отрицательного pid не бывает: такое значение — признак испорченной команды, а не «ждать некого».</summary>
        [Fact]
        public void ОтрицательныйИдентификаторОтвергается() {
            Assert.False(global::Program.TryParseParentPid("-1", out _, out var problem));
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        /// <summary>
        /// Сквозная проверка вместе с разбором командной строки: ровно та команда,
        /// на которой защита A13 обходилась молча.
        /// </summary>
        [Theory]
        [InlineData(@"--parent|--dst|C:\app")]
        [InlineData(@"--src|C:\tmp|--parent")]
        [InlineData(@"--parent|   |--dst|C:\app")]
        public void КомандаБезЗначенияParentНеДаётМолчаливогоНуля(string commandLine) {
            var map = global::Program.ParseArgs(commandLine.Split('|'));
            var raw = map.TryGetValue("--parent", out var v) ? v : null;

            Assert.False(global::Program.TryParseParentPid(raw, out _, out _));
        }

        /// <summary>Причина отказа попадает в текст: она уходит и в журнал, и в статус обновления для пользователя.</summary>
        [Fact]
        public void ПричинаОтказаОписанаТекстом() {
            Assert.False(global::Program.TryParseParentPid("мусор", out _, out var problem));
            Assert.Contains("мусор", problem, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Чтение аргументов перезапуска лаунчера.
    /// <para>
    /// Лаунчер сам себя закрывает, чтобы отдать файлы апдейтеру, и поднять его обратно
    /// может только апдейтер. Аргументы для этого лежат в файле — по одному на строку,
    /// чтобы ничего не пришлось экранировать. Любая ошибка чтения означает либо
    /// лаунчер, стартующий без своих параметров, либо (при исключении) не стартующий
    /// вовсе: окно закрылось и больше не открылось.
    /// </para>
    /// </summary>
    public class UpdaterExeArgsTests {
        /// <summary>Файла аргументов может не быть — это штатный случай, а не ошибка.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ОтсутствующийПутьДаётПустойСписок(string? path) {
            Assert.Empty(global::Program.ReadExeArgs(path, new UpdateLog()));
        }

        /// <summary>Путь есть, а файла нет (удалён антивирусом, не создан) — перезапуск всё равно должен состояться.</summary>
        [Fact]
        public void НесуществующийФайлНеРоняетПерезапуск() {
            using var dir = new TempDir();
            Assert.Empty(global::Program.ReadExeArgs(dir.PathTo("нет-такого.txt"), new UpdateLog()));
        }

        /// <summary>Пустой файл означает «аргументов не было», а не строку-пустышку в командной строке.</summary>
        [Fact]
        public void ПустойФайлДаётПустойСписок() {
            using var dir = new TempDir();
            Assert.Empty(global::Program.ReadExeArgs(dir.WriteFile("args.txt", string.Empty), new UpdateLog()));
        }

        /// <summary>Пустые строки внутри файла отбрасываются: пустой аргумент лаунчер бы не понял.</summary>
        [Fact]
        public void ПустыеСтрокиОтбрасываются() {
            using var dir = new TempDir();
            var path = dir.WriteFile("args.txt", "--game\r\n\r\nchillhub\r\n\r\n");

            Assert.Equal(new[] { "--game", "chillhub" }, global::Program.ReadExeArgs(path, new UpdateLog()));
        }

        /// <summary>
        /// BOM в начале файла не должен приклеиться к первому аргументу: лаунчер
        /// сравнивает аргументы точной строкой, и «\uFEFF--game» для него — неизвестная опция.
        /// </summary>
        [Fact]
        public void BOMНеПопадаетВПервыйАргумент() {
            using var dir = new TempDir();
            var bytes = new UTF8Encoding(true).GetBytes("--game\r\nchillhub\r\n");
            var path = dir.WriteBytes("args-bom.txt", bytes);

            var args = global::Program.ReadExeArgs(path, new UpdateLog());

            Assert.Equal(new[] { "--game", "chillhub" }, args);
            Assert.DoesNotContain('\uFEFF', args[0]);
        }

        /// <summary>
        /// Аргумент восстанавливается дословно, вместе с пробелами и кавычками:
        /// файл на то и выбран вместо командной строки, чтобы ничего не экранировать.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Program Files\ChillHub\game")]
        [InlineData(@"--path=""C:\Games\My Game""")]
        [InlineData("--комментарий=строка с пробелами")]
        [InlineData("   ")]
        public void АргументВосстанавливаетсяДословно(string arg) {
            using var dir = new TempDir();
            var path = dir.WriteFile("args.txt", arg + "\r\n");

            Assert.Equal(new[] { arg }, global::Program.ReadExeArgs(path, new UpdateLog()));
        }

        /// <summary>Не-ASCII в путях — норма для Windows: имя пользователя кириллицей встречается постоянно.</summary>
        [Fact]
        public void НеASCIIСохраняется() {
            using var dir = new TempDir();
            var arg = @"C:\Users\Вася\Игры\日本語\game.exe";
            var path = dir.WriteFile("args.txt", arg + "\n");

            Assert.Equal(new[] { arg }, global::Program.ReadExeArgs(path, new UpdateLog()));
        }

        /// <summary>Одиночный LF — тоже перевод строки: файл мог быть записан не Windows-инструментом.</summary>
        [Fact]
        public void ПереводСтрокиLFРазбираетсяКакCRLF() {
            using var dir = new TempDir();
            var path = dir.WriteFile("args.txt", "--a\n--b\n");

            Assert.Equal(new[] { "--a", "--b" }, global::Program.ReadExeArgs(path, new UpdateLog()));
        }

        /// <summary>
        /// Очень длинная строка не обрезается и не роняет чтение: аргумент может
        /// нести длинный путь или сериализованное состояние, и урезанный до половины
        /// он опаснее отсутствующего.
        /// </summary>
        [Fact]
        public void ОченьДлиннаяСтрокаНеОбрезается() {
            using var dir = new TempDir();
            var arg = "--payload=" + new string('ы', 100_000);
            var path = dir.WriteFile("args.txt", arg + "\r\n--tail\r\n");

            var args = global::Program.ReadExeArgs(path, new UpdateLog());

            Assert.Equal(2, args.Count);
            Assert.Equal(arg.Length, args[0].Length);
            Assert.Equal("--tail", args[1]);
        }

        /// <summary>Каталог вместо файла — не повод бросить исключение в блоке finally, откуда зовут перезапуск.</summary>
        [Fact]
        public void КаталогВместоФайлаНеРоняет() {
            using var dir = new TempDir();
            Assert.Empty(global::Program.ReadExeArgs(dir.Root, new UpdateLog()));
        }

        /// <summary>
        /// Нечитаемый файл (занят другим процессом) не должен помешать перезапуску:
        /// лаунчер поднимется без аргументов, но поднимется.
        /// </summary>
        [Fact]
        public void ЗанятыйФайлНеОтменяетПерезапуск() {
            using var dir = new TempDir();
            var path = dir.WriteFile("args.txt", "--game\r\n");

            using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Empty(global::Program.ReadExeArgs(path, new UpdateLog()));
        }

        /// <summary>Порядок аргументов сохраняется: для командной строки он значим.</summary>
        [Fact]
        public void ПорядокАргументовСохраняется() {
            using var dir = new TempDir();
            var expected = Enumerable.Range(0, 20).Select(i => "--arg" + i).ToArray();
            var path = dir.WriteFile("args.txt", string.Join("\r\n", expected));

            Assert.Equal(expected, global::Program.ReadExeArgs(path, new UpdateLog()));
        }
    }
}
