// <copyright file="SelfUpdateVersionTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Text;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Номер версии, пришедший с сервера, и путь, который из него получается.
    /// <para>
    /// Строка из latest.json попадает в <c>Path.Combine</c>, в URL манифеста и в
    /// аргументы внешнего процесса, который сразу после этого заменяет файлы
    /// работающего лаунчера. Пропущенное сюда «..\..\Startup» — это не битый экран,
    /// а чужие файлы в автозагрузке. Проверка обязана быть белым списком.
    /// </para>
    /// </summary>
    public class SelfUpdateVersionTests {
        /// <summary>Обычные формы версии проходят: без них обновление вообще не поедет.</summary>
        [Theory]
        [InlineData("1.2.3")]
        [InlineData("1.2.3.4")]
        [InlineData("1.2.3-beta.1")]
        [InlineData("0.0.1")]
        [InlineData("123456.0.0")]
        public void ДопустимыеВерсииПроходят(string version) {
            Assert.True(SelfUpdateVersions.IsValidVersion(version));
        }

        /// <summary>
        /// Всё, что уводит путь или ломает разбор командной строки, отбрасывается целиком.
        /// Именно «отбрасывается», а не «чистится»: чистка оставляет шанс ошибиться.
        /// </summary>
        [Theory]
        [InlineData(@"..\..\Startup")]
        [InlineData("../../etc")]
        [InlineData("1.2.3 --dst C:\\Windows")]
        [InlineData("1.2.3\"")]
        [InlineData("C:\\Windows")]
        [InlineData("1")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("latest")]
        [InlineData("1.2.3\n1.2.4")]
        public void ОпасныеИНеполныеВерсииОтбрасываются(string? version) {
            Assert.False(SelfUpdateVersions.IsValidVersion(version));
        }

        /// <summary>Слишком длинная версия не проходит: она станет частью пути.</summary>
        [Fact]
        public void СлишкомДлиннаяВерсияОтбрасывается() {
            var long1 = "1.2.3-" + new string('a', 60);
            Assert.True(long1.Length > 64);
            Assert.False(SelfUpdateVersions.IsValidVersion(long1));
        }

        /// <summary>Маркер, записанный апдейтером, важнее версии сборки: он и есть «что реально стоит».</summary>
        [Fact]
        public void МаркерВерсииПредпочитаетсяВерсииСборки() {
            using var dir = new TempDir();
            dir.WriteFile("launcher.version", " 9.9.9 \r\n");

            Assert.Equal("9.9.9", SelfUpdateVersions.ReadLocalVersion(dir.Root));
        }

        /// <summary>
        /// Без маркера остаётся версия сборки. Пустой ответ здесь недопустим: пустая
        /// локальная версия трактуется как «ничего не знаем» и обновление не предлагается
        /// уже никогда.
        /// </summary>
        [Fact]
        public void БезМаркераБерётсяВерсияСборки() {
            using var dir = new TempDir();

            var local = SelfUpdateVersions.ReadLocalVersion(dir.Root);

            Assert.False(string.IsNullOrWhiteSpace(local));
            Assert.Equal(3, local.Split('.').Length);
        }

        /// <summary>Нечитаемый маркер не должен ронять проверку — откатываемся на версию сборки.</summary>
        [Fact]
        public void НечитаемыйМаркерОткатываетсяНаВерсиюСборки() {
            using var dir = new TempDir();

            // Каталог с именем файла: File.Exists даст false, но общий путь тот же.
            Directory.CreateDirectory(dir.PathTo("launcher.version"));

            Assert.False(string.IsNullOrWhiteSpace(SelfUpdateVersions.ReadLocalVersion(dir.Root)));
        }

        /// <summary>Пакет, упакованный с общей корневой папкой, даёт strip-prefix.</summary>
        [Fact]
        public void ОбщаяКорневаяПапкаСтановитсяПрефиксом() {
            var mf = SelfUpdateManifest.Of(
                SelfUpdateManifest.Different("ChillHub/ChillHub.exe"),
                SelfUpdateManifest.Different("ChillHub/data/x.pak"));

            Assert.Equal("ChillHub", SelfUpdateVersions.ComputeStripPrefix(mf));
        }

        /// <summary>
        /// Файл в корне пакета означает, что общей папки нет. Иначе «ChillHub» из части
        /// путей срезался бы, а из остальных нет — и апдейтер разложил бы файлы вперемешку.
        /// </summary>
        [Fact]
        public void ФайлВКорнеОтменяетПрефикс() {
            var mf = SelfUpdateManifest.Of(
                SelfUpdateManifest.Different("ChillHub/ChillHub.exe"),
                SelfUpdateManifest.Different("readme.txt"));

            Assert.Equal(string.Empty, SelfUpdateVersions.ComputeStripPrefix(mf));
        }

        /// <summary>Разные корневые папки — тоже «префикса нет».</summary>
        [Fact]
        public void РазныеКорневыеПапкиОтменяютПрефикс() {
            var mf = SelfUpdateManifest.Of(
                SelfUpdateManifest.Different("app/ChillHub.exe"),
                SelfUpdateManifest.Different("data/x.pak"));

            Assert.Equal(string.Empty, SelfUpdateVersions.ComputeStripPrefix(mf));
        }

        /// <summary>Пустой манифест префикса не даёт.</summary>
        [Fact]
        public void ПустойМанифестДаётПустойПрефикс() {
            Assert.Equal(string.Empty, SelfUpdateVersions.ComputeStripPrefix(new Manifest()));
        }

        /// <summary>Префикс срезается, регистр значения не имеет, чужой путь остаётся как был.</summary>
        [Theory]
        [InlineData("ChillHub", "ChillHub/data/x.pak", "data/x.pak")]
        [InlineData("ChillHub", "chillhub/data/x.pak", "data/x.pak")]
        [InlineData("ChillHub", "other/x.pak", "other/x.pak")]
        [InlineData("", "data/x.pak", "data/x.pak")]
        [InlineData("ChillHub", @"ChillHub\data\x.pak", "data/x.pak")]
        public void ПрефиксСрезаетсяТолькоУСвоихПутей(string prefix, string rel, string expected) {
            Assert.Equal(expected, SelfUpdateVersions.StripLocal(prefix, rel));
        }

        /// <summary>
        /// Маркер пишется без BOM и без лишних пробелов: BOM в начале файла превратил бы
        /// «1.2.3» в строку, которая не равна ничему, и лаунчер обновлялся бы вечно.
        /// </summary>
        [Fact]
        public void МаркерПишетсяБезBom() {
            using var dir = new TempDir();

            Assert.True(SelfUpdateVersions.TryWriteVersionMarker(dir.Root, " 1.2.4 ", out var error));

            Assert.Equal(string.Empty, error);
            var bytes = File.ReadAllBytes(dir.PathTo("launcher.version"));
            Assert.Equal("1.2.4", Encoding.UTF8.GetString(bytes));
            Assert.Equal("1.2.4", SelfUpdateVersions.ReadLocalVersion(dir.Root));
        }

        /// <summary>
        /// Неудачная запись маркера обязана быть видна вызывающему. Пока она молчала,
        /// счётчик попыток сбрасывался «как будто всё хорошо», а окно обновления
        /// всплывало при каждом запуске.
        /// </summary>
        [Fact]
        public void НеудачаЗаписиМаркераВозвращаетПричину() {
            using var dir = new TempDir();
            Directory.CreateDirectory(dir.PathTo("launcher.version"));

            Assert.False(SelfUpdateVersions.TryWriteVersionMarker(dir.Root, "1.2.4", out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        /// <summary>Совпадающий файл признаётся совпавшим — обновление из-за него не поедет.</summary>
        [Fact]
        public void СовпавшийФайлНеПричинаОбновления() {
            using var dir = new TempDir();
            dir.WriteFile("ChillHub.dll", "содержимое");

            var f = SelfUpdateManifest.Matching(dir.Root, "ChillHub.dll");

            Assert.True(SelfUpdateVersions.LocalFileMatches(dir.Root, string.Empty, f, out var reason));
            Assert.Equal(string.Empty, reason);
        }

        /// <summary>Пропавший файл — расхождение с внятной причиной, а не «всё в порядке».</summary>
        [Fact]
        public void ПропавшийФайлСчитаетсяРасхождением() {
            using var dir = new TempDir();

            Assert.False(SelfUpdateVersions.LocalFileMatches(dir.Root, string.Empty, SelfUpdateManifest.Different("ChillHub.dll"), out var reason));
            Assert.Equal("missing", reason);
        }

        /// <summary>Пустой путь в манифесте расхождением не считается — сверять нечего.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("/")]
        [InlineData("///")]
        public void ПустойПутьВМанифестеПропускается(string path) {
            using var dir = new TempDir();

            Assert.True(SelfUpdateVersions.LocalFileMatches(dir.Root, string.Empty, new ManifestFile { Path = path }, out _));
        }

        /// <summary>
        /// Отмену сверки нельзя проглатывать: отменённая проверка «подтвердила» бы
        /// битый файл и обновление прошло бы мимо него.
        /// </summary>
        [Fact]
        public void ОтменаСверкиПрокидываетсяНаружу() {
            using var dir = new TempDir();
            dir.WriteFile("ChillHub.dll", new string('x', 4096));
            var f = SelfUpdateManifest.Matching(dir.Root, "ChillHub.dll");
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => SelfUpdateVersions.LocalFileMatches(dir.Root, string.Empty, f, out _, cts.Token));
        }

        /// <summary>Strip-prefix учитывается и при сверке: иначе файл ищется не там, где лежит.</summary>
        [Fact]
        public void СверкаУчитываетStripPrefix() {
            using var dir = new TempDir();
            dir.WriteFile("data/x.pak", "полезное");
            var real = SelfUpdateManifest.Matching(dir.Root, "data/x.pak");
            var packed = new ManifestFile {
                Path = "ChillHub/data/x.pak",
                Size = real.Size,
                Sha256 = real.Sha256,
                Blake3 = real.Blake3,
            };

            Assert.True(SelfUpdateVersions.LocalFileMatches(dir.Root, "ChillHub", packed, out _));
            Assert.False(SelfUpdateVersions.LocalFileMatches(dir.Root, string.Empty, packed, out _));
        }
    }

    /// <summary>
    /// Счётчик попыток обновления на одну версию.
    /// <para>
    /// Это единственный тормоз петли «обновились — не записался маркер — обновляемся
    /// снова». Сломанный счётчик означает либо вечный цикл перезапусков лаунчера,
    /// либо навсегда заблокированное обновление.
    /// </para>
    /// </summary>
    public class UpdateAttemptsStoreTests {
        /// <summary>Пустого счётчика достаточно, чтобы обновление разрешили.</summary>
        [Fact]
        public void БезФайлаПопытокНоль() {
            using var dir = new TempDir();
            Assert.Equal(0, Store(dir).Get("1.2.4"));
        }

        /// <summary>Каждая зафиксированная попытка увеличивает счётчик.</summary>
        [Fact]
        public void ПопыткиНакапливаются() {
            using var dir = new TempDir();
            var store = Store(dir);

            store.Register("1.2.4");
            store.Register("1.2.4");

            Assert.Equal(2, store.Get("1.2.4"));
        }

        /// <summary>
        /// Счётчик привязан к версии: новая версия начинает с нуля, иначе давняя
        /// история блокировала бы обновление, к которому она не относится.
        /// </summary>
        [Fact]
        public void СчётчикПривязанКВерсии() {
            using var dir = new TempDir();
            var store = Store(dir);
            store.Register("1.2.4");
            store.Register("1.2.4");

            Assert.Equal(0, store.Get("1.2.5"));
            store.Register("1.2.5");
            Assert.Equal(1, store.Get("1.2.5"));
            Assert.Equal(0, store.Get("1.2.4"));
        }

        /// <summary>Испорченный файл счётчика читается как «попыток не было», а не как отказ.</summary>
        [Theory]
        [InlineData("мусор")]
        [InlineData("1.2.4")]
        [InlineData("1.2.4|не число|x")]
        [InlineData("")]
        public void ИспорченныйСчётчикЧитаетсяКакНоль(string content) {
            using var dir = new TempDir();
            var path = dir.PathTo("attempts.txt");
            File.WriteAllText(path, content);

            Assert.Equal(0, new UpdateAttemptsStore(path).Get("1.2.4"));
        }

        /// <summary>Сброс снимает блокировку: обновление дошло до конца либо оказалось не нужным.</summary>
        [Fact]
        public void СбросОбнуляетСчётчик() {
            using var dir = new TempDir();
            var store = Store(dir);
            store.Register("1.2.4");

            store.Reset();

            Assert.Equal(0, store.Get("1.2.4"));
            Assert.False(File.Exists(store.FilePath));
        }

        /// <summary>Сброс несуществующего счётчика — не ошибка.</summary>
        [Fact]
        public void СбросБезФайлаНеПадает() {
            using var dir = new TempDir();
            Store(dir).Reset();
        }

        /// <summary>Путь по умолчанию лежит в роуминг-профиле рядом с остальным состоянием.</summary>
        [Fact]
        public void ПутьПоУмолчаниюВПрофилеПользователя() {
            Assert.EndsWith(Path.Combine("ChillHub", "selfupdate-attempts.txt"), UpdateAttemptsStore.DefaultPath, StringComparison.Ordinal);
            Assert.Equal(UpdateAttemptsStore.DefaultPath, new UpdateAttemptsStore(" ").FilePath);
        }

        private static UpdateAttemptsStore Store(TempDir dir) => new UpdateAttemptsStore(dir.PathTo("attempts.txt"));
    }
}
