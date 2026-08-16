// <copyright file="SelfUpdateApplyTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Запуск внешнего апдейтера — точка невозврата самообновления.
    /// <para>
    /// После успешного старта лаунчер завершается, и файлы работающей установки
    /// заменяет чужой процесс. Любой отказ ДО этого момента обязан оставить лаунчер
    /// живым: пользователь, у которого лаунчер погас, а апдейтер не поднялся,
    /// остаётся без приложения, которое уже не может обновиться само.
    /// </para>
    /// </summary>
    public class SelfUpdateApplyTests {
        /// <summary>Пакета нет — приложение не гасим и даём повторить.</summary>
        [Fact]
        public void БезПакетаОбновлениеНеПрименяется() {
            using var stand = new SelfUpdateStand();
            var ui = new UiRecorder();
            var started = 0;

            var result = NewApplier(stand, ui, _ => { started++; return null; })
                .Apply(Path.Combine(stand.Temp.Root, "нет-такого"), null, "1.2.4", string.Empty);

            Assert.Equal(SelfUpdateApplyResult.PackageMissing, result);
            Assert.Equal(0, started);
            Assert.Equal("Не найден пакет обновления.", ui.LastStatus);
            Assert.True(ui.LastButtonEnabled);
        }

        /// <summary>Пустой путь к пакету — тот же отказ, а не попытка запустить апдейтер «наугад».</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void ПустойПутьКПакетуОстанавливаетПрименение(string? tempRoot) {
            using var stand = new SelfUpdateStand();
            var ui = new UiRecorder();

            var result = NewApplier(stand, ui, _ => throw new InvalidOperationException("сюда ходить нельзя"))
                .Apply(tempRoot, null, "1.2.4", string.Empty);

            Assert.Equal(SelfUpdateApplyResult.PackageMissing, result);
        }

        /// <summary>
        /// A10. Комплект апдейтера неполон — приложение не гасим. Раньше проверялось
        /// наличие одного .exe: если .dll и .runtimeconfig.json не скопировались,
        /// лаунчер всё равно завершался, апдейтер тут же умирал, и пользователь
        /// оставался вообще без приложения.
        /// </summary>
        [Fact]
        public void НеполныйКомплектАпдейтераНеГаситЛаунчер() {
            using var stand = new SelfUpdateStand();
            var payload = PreparePayload(stand);
            var ui = new UiRecorder();
            var started = 0;

            var result = NewApplier(stand, ui, _ => { started++; return null; })
                .Apply(payload, null, "1.2.4", string.Empty);

            Assert.Equal(SelfUpdateApplyResult.UpdaterIncomplete, result);
            Assert.Equal(0, started);
            Assert.Contains("Модуль обновления подготовлен не полностью", ui.LastStatus!, StringComparison.Ordinal);
            Assert.True(ui.LastButtonEnabled);
        }

        /// <summary>В папке установки нет ни одного файла апдейтера — говорим об этом прямо.</summary>
        [Fact]
        public void ОтсутствиеФайловАпдейтераНазываетсяЯвно() {
            using var dir = new TempDir();
            using var dst = new TempDir();

            var missing = SelfUpdateApplier.PrepareUpdaterPayload(dir.Root, dst.Root);

            Assert.Contains(missing, m => m.Contains("в папке установки нет ни одного файла модуля обновления", StringComparison.Ordinal));
        }

        /// <summary>Полный комплект копируется целиком и претензий не вызывает.</summary>
        [Fact]
        public void ПолныйКомплектАпдейтераКопируетсяЦеликом() {
            using var src = new TempDir();
            using var dst = new TempDir();
            src.WriteFile("ChillHub.Updater.exe", "apphost");
            src.WriteFile("ChillHub.Updater.dll", "код");
            src.WriteFile("ChillHub.Updater.runtimeconfig.json", "{}");
            src.WriteFile("ChillHub.exe", "не апдейтер");

            var missing = SelfUpdateApplier.PrepareUpdaterPayload(src.Root, dst.Root);

            Assert.Empty(missing);
            Assert.True(File.Exists(dst.PathTo("ChillHub.Updater.exe")));
            Assert.True(File.Exists(dst.PathTo("ChillHub.Updater.dll")));
            Assert.True(File.Exists(dst.PathTo("ChillHub.Updater.runtimeconfig.json")));

            // Посторонние файлы установки в копию не едут.
            Assert.False(File.Exists(dst.PathTo("ChillHub.exe")));
        }

        /// <summary>
        /// Занятый файл апдейтера (антивирус, второй экземпляр) обязан попасть в список
        /// недостающего вместе с причиной, а не тихо пропасть.
        /// </summary>
        [Fact]
        public void ЗанятыйФайлАпдейтераПопадаетВСписокНедостающего() {
            using var src = new TempDir();
            using var dst = new TempDir();
            src.WriteFile("ChillHub.Updater.exe", "apphost");
            src.WriteFile("ChillHub.Updater.dll", "код");

            // Держим цель копирования открытой на запись: File.Copy туда не сможет.
            using var busy = new FileStream(
                dst.PathTo("ChillHub.Updater.dll"), FileMode.Create, FileAccess.Write, FileShare.None);

            var missing = SelfUpdateApplier.PrepareUpdaterPayload(src.Root, dst.Root);

            Assert.Contains(missing, m => m.StartsWith("ChillHub.Updater.dll", StringComparison.Ordinal));
        }

        /// <summary>
        /// A3. Каталог установки занят другим апдейтером — второй не запускаем.
        /// Два процесса перемешают файлы и бэкапы, и откат любого из них оставит
        /// смесь версий, которую уже не разобрать.
        /// </summary>
        [Fact]
        public void ЗанятыйЗамокУстановкиОстанавливаетЗапуск() {
            using var stand = new SelfUpdateStand();
            var payload = PreparePayload(stand, withUpdater: true);
            using var foreign = new ForeignUpdateLock(stand.Paths.TargetDir);
            var ui = new UiRecorder();
            var started = 0;

            var result = NewApplier(stand, ui, _ => { started++; return null; })
                .Apply(payload, null, "1.2.4", string.Empty);

            Assert.Equal(SelfUpdateApplyResult.LockBusy, result);
            Assert.Equal(0, started);
            Assert.Contains("уже применяется другим процессом", ui.LastStatus!, StringComparison.Ordinal);
            Assert.True(ui.LastButtonEnabled);
        }

        /// <summary>A8. Апдейтер не стартовал — приложение НЕ закрываем и попытку не засчитываем.</summary>
        [Fact]
        public void НеудачныйЗапускАпдейтераНеГаситЛаунчер() {
            using var stand = new SelfUpdateStand();
            var payload = PreparePayload(stand, withUpdater: true);
            var attempts = stand.Attempts;
            var ui = new UiRecorder();

            var result = NewApplier(stand, ui, _ => null, attempts).Apply(payload, null, "1.2.4", string.Empty);

            Assert.Equal(SelfUpdateApplyResult.StartFailed, result);
            Assert.Contains("Не удалось запустить модуль обновления", ui.LastStatus!, StringComparison.Ordinal);
            Assert.True(ui.LastButtonEnabled);
            Assert.Equal(0, attempts.Get("1.2.4"));
        }

        /// <summary>Исключение при запуске процесса — тот же исход, и его текст виден пользователю.</summary>
        [Fact]
        public void ИсключениеПриЗапускеПоказываетсяПользователю() {
            using var stand = new SelfUpdateStand();
            var payload = PreparePayload(stand, withUpdater: true);
            var ui = new UiRecorder();

            var result = NewApplier(stand, ui, _ => throw new System.ComponentModel.Win32Exception("отказано в доступе"))
                .Apply(payload, null, "1.2.4", string.Empty);

            Assert.Equal(SelfUpdateApplyResult.StartFailed, result);
            Assert.Contains("отказано в доступе", ui.LastStatus!, StringComparison.Ordinal);
            Assert.True(ui.LastButtonEnabled);
        }

        /// <summary>
        /// Апдейтер запущен — только теперь попытка засчитывается (A1: защита от петли
        /// считает применения, а не нажатия кнопки).
        /// </summary>
        [Fact]
        public void ПопыткаЗасчитываетсяТолькоПослеРеальногоЗапуска() {
            using var stand = new SelfUpdateStand();
            var payload = PreparePayload(stand, withUpdater: true);
            var attempts = stand.Attempts;
            var ui = new UiRecorder();

            var result = NewApplier(stand, ui, _ => Process.GetCurrentProcess(), attempts)
                .Apply(payload, null, "1.2.4", string.Empty);

            Assert.Equal(SelfUpdateApplyResult.Started, result);
            Assert.Equal(1, attempts.Get("1.2.4"));
            Assert.Contains("Применение обновления...", ui.LastStatus!, StringComparison.Ordinal);
        }

        /// <summary>Журнал апдейтера создаётся заранее и содержит откуда/куда — без него разбирать нечего.</summary>
        [Fact]
        public void ЖурналСоздаётсяДоЗапускаАпдейтера() {
            using var stand = new SelfUpdateStand();
            var payload = PreparePayload(stand, withUpdater: true);
            var work = stand.Paths.WorkDir("1.2.4");

            NewApplier(stand, new UiRecorder(), _ => Process.GetCurrentProcess()).Apply(payload, work, "1.2.4", string.Empty);

            var log = File.ReadAllText(Path.Combine(work, "apply-update.log"));
            Assert.Contains("Apply started", log, StringComparison.Ordinal);
            Assert.Contains(payload, log, StringComparison.Ordinal);
            Assert.Contains(stand.Paths.TargetDir, log, StringComparison.Ordinal);
        }

        // -------------------------------------------------------------------
        // Аргументы апдейтера. Разбирает их отдельный процесс, диалога с
        // пользователем у него нет: неверный аргумент — обновление не туда.
        // -------------------------------------------------------------------

        /// <summary>Обязательные аргументы на месте и указывают на реальные пути сессии.</summary>
        [Fact]
        public void ОбязательныеАргументыАпдейтераНаМесте() {
            var args = Args(version: "1.2.4", stripPrefix: string.Empty, exeArgsPath: string.Empty);

            Assert.Equal(@"C:\temp\payload", Value(args, "--src"));
            Assert.Equal(@"C:\app", Value(args, "--dst"));
            Assert.Equal(@"C:\app\ChillHub.exe", Value(args, "--exe"));
            Assert.Equal("4242", Value(args, "--parent"));
            Assert.Equal(@"C:\temp\work\apply-update.log", Value(args, "--log"));
            Assert.Equal(@"C:\temp\work\filelist.txt", Value(args, "--files"));
            Assert.Equal(@"C:\temp\work\emptydirs.txt", Value(args, "--dirs"));
            Assert.Equal(@"C:\temp\work\deletelist.txt", Value(args, "--del"));
            Assert.Equal("1.2.4", Value(args, "--version"));
        }

        /// <summary>
        /// A2. Preserve-правила уезжают апдейтеру из общего PreserveMatcher, а не из
        /// строкового литерала: разошедшиеся списки — это вечная петля самообновления.
        /// </summary>
        [Fact]
        public void PreserveПравилаБерутсяИзОбщегоСписка() {
            var args = Args(version: "1.2.4", stripPrefix: string.Empty, exeArgsPath: string.Empty);

            Assert.Equal(PreserveMatcher.DefaultRulesArg, Value(args, "--preserve"));
            Assert.Contains("config.json", Value(args, "--preserve"), StringComparison.Ordinal);
            Assert.Contains("launcher.version", Value(args, "--preserve"), StringComparison.Ordinal);
        }

        /// <summary>
        /// A10. Автодетект strip-prefix апдейтеру запрещён: префикс считает лаунчер по
        /// манифесту, иначе стороны разойдутся в понимании путей.
        /// </summary>
        [Fact]
        public void АвтодетектПрефиксаВсегдаВыключен() {
            Assert.Equal("false", Value(Args(version: "1.2.4", stripPrefix: string.Empty, exeArgsPath: string.Empty), "--auto-strip"));
            Assert.Equal("false", Value(Args(version: "1.2.4", stripPrefix: "ChillHub", exeArgsPath: string.Empty), "--auto-strip"));
        }

        /// <summary>Непустой префикс передаётся, пустой — не передаётся вовсе.</summary>
        [Fact]
        public void ПрефиксПередаётсяТолькоКогдаОнЕсть() {
            Assert.Equal("ChillHub", Value(Args(version: "1.2.4", stripPrefix: "ChillHub", exeArgsPath: string.Empty), "--strip-prefix"));
            Assert.DoesNotContain("--strip-prefix", Args(version: "1.2.4", stripPrefix: string.Empty, exeArgsPath: string.Empty));
        }

        /// <summary>Необязательные аргументы не передаются пустыми: пустое значение апдейтер разберёт как мусор.</summary>
        [Fact]
        public void ПустыеНеобязательныеАргументыНеПередаются() {
            var args = Args(version: null, stripPrefix: string.Empty, exeArgsPath: string.Empty);

            Assert.DoesNotContain("--version", args);
            Assert.DoesNotContain("--exe-args-file", args);
        }

        /// <summary>A9. Файл с исходными аргументами лаунчера передаётся, когда он записан.</summary>
        [Fact]
        public void ФайлАргументовЛаунчераПередаётсяКогдаЕсть() {
            var args = Args(version: "1.2.4", stripPrefix: string.Empty, exeArgsPath: @"C:\temp\work\exeargs.txt");

            Assert.Equal(@"C:\temp\work\exeargs.txt", Value(args, "--exe-args-file"));
        }

        /// <summary>
        /// Каталог установки заканчивается на '\', и раньше ручное экранирование съедало
        /// закрывающую кавычку, склеивая соседние аргументы. ArgumentList передаёт значение
        /// как есть — расклейкой занимается операционная система.
        /// </summary>
        [Fact]
        public void ПутьСЗавершающимСлешемНеСклеиваетАргументы() {
            var psi = SelfUpdateApplier.BuildStartInfo(
                @"C:\temp\work\updater\ChillHub.Updater.exe",
                @"C:\temp\work\updater",
                @"C:\temp\payload",
                @"C:\Program Files\Chill Hub\",
                @"C:\app\ChillHub.exe",
                4242,
                @"C:\temp\work\apply-update.log",
                @"C:\temp\work",
                string.Empty,
                "1.2.4",
                string.Empty);

            Assert.Equal(@"C:\Program Files\Chill Hub\", Value(psi.ArgumentList.ToList(), "--dst"));
        }

        /// <summary>Апдейтер запускается скрытым и без своей консоли: это фон, а не приложение.</summary>
        [Fact]
        public void АпдейтерЗапускаетсяСкрытымИзСвоегоКаталога() {
            var psi = SelfUpdateApplier.BuildStartInfo(
                @"C:\temp\work\updater\ChillHub.Updater.exe",
                @"C:\temp\work\updater",
                @"C:\temp\payload",
                @"C:\app",
                @"C:\app\ChillHub.exe",
                4242,
                @"C:\temp\work\apply-update.log",
                @"C:\temp\work",
                string.Empty,
                "1.2.4",
                string.Empty);

            Assert.Equal(@"C:\temp\work\updater\ChillHub.Updater.exe", psi.FileName);
            Assert.Equal(@"C:\temp\work\updater", psi.WorkingDirectory);
            Assert.False(psi.UseShellExecute);
            Assert.True(psi.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Hidden, psi.WindowStyle);
        }

        /// <summary>
        /// A9. Аргументы лаунчера пишутся по строке на аргумент, а аргумент с переводом
        /// строки выбрасывается: он разрушил бы построчный формат.
        /// </summary>
        [Fact]
        public void АргументыЛаунчераПишутсяПострочно() {
            using var dir = new TempDir();

            var path = SelfUpdateApplier.WriteExeArgs(dir.Root);

            Assert.Equal(dir.PathTo("exeargs.txt"), path);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(File.ReadAllLines(path), line => line.Contains('\r', StringComparison.Ordinal));
        }

        /// <summary>Файл аргументов записать не вышло — обновление это не отменяет, путь просто пустеет.</summary>
        [Fact]
        public void НеудачаЗаписиАргументовНеОтменяетОбновление() {
            using var dir = new TempDir();
            Directory.CreateDirectory(dir.PathTo("exeargs.txt"));

            Assert.Equal(string.Empty, SelfUpdateApplier.WriteExeArgs(dir.Root));
        }

        private static List<string> Args(string? version, string stripPrefix, string exeArgsPath)
            => SelfUpdateApplier.BuildStartInfo(
                @"C:\temp\work\updater\ChillHub.Updater.exe",
                @"C:\temp\work\updater",
                @"C:\temp\payload",
                @"C:\app",
                @"C:\app\ChillHub.exe",
                4242,
                @"C:\temp\work\apply-update.log",
                @"C:\temp\work",
                exeArgsPath,
                version,
                stripPrefix).ArgumentList.ToList();

        private static string Value(List<string> args, string key) {
            var idx = args.IndexOf(key);
            Assert.True(idx >= 0, $"аргумент {key} не передан");
            return args[idx + 1];
        }

        /// <summary>Готовит каталог с «полезной нагрузкой» и, по требованию, комплект апдейтера.</summary>
        private static string PreparePayload(SelfUpdateStand stand, bool withUpdater = false) {
            var payload = stand.Paths.PayloadDir("1.2.4");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "ChillHub.dll"), "новая версия");
            if (withUpdater) {
                stand.Install.WriteFile("ChillHub.Updater.exe", "apphost");
                stand.Install.WriteFile("ChillHub.Updater.dll", "код");
                stand.Install.WriteFile("ChillHub.Updater.runtimeconfig.json", "{}");
            }

            return payload;
        }

        private static SelfUpdateApplier NewApplier(
            SelfUpdateStand stand, UiRecorder ui, Func<ProcessStartInfo, Process?> start, UpdateAttemptsStore? attempts = null)
            => new SelfUpdateApplier(stand.Paths, attempts ?? stand.Attempts, ui.Apply, start);
    }
}
