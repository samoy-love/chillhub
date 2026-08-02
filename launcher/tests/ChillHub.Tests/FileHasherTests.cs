// <copyright file="FileHasherTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Р5. Подсчёт хешей раньше жил в трёх независимых копиях: планировщик диффа игр,
    /// верификация скачанного .part и сверка файлов самообновления в UpdateWindow.
    /// Копии разошлись — одна проверяла отмену, другая нет — а это ровно тот класс бага,
    /// при котором два кода дают РАЗНЫЕ вердикты на одинаковых входах и лаунчер
    /// бесконечно предлагает одно и то же обновление. Тесты фиксируют, что реализация
    /// одна и вердикты совпадают.
    /// </summary>
    public class FileHasherTests {
        [Fact]
        public void ХешиСовпадаютСЭталоном() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");

            FileHasher.ComputeHashes(path, out var sha, out var b3);

            Assert.Equal(TestHash.Sha256OfFile(path), sha);
            Assert.Equal(TestHash.Blake3OfFile(path), b3);
        }

        /// <summary>
        /// Манифест хранит hex в нижнем регистре. Если однопроходный цикл вернёт
        /// верхний регистр, сравнение через Equals(Ordinal) где-нибудь в новом коде
        /// молча начнёт считать целый файл битым.
        /// </summary>
        [Fact]
        public void ХешиВозвращаютсяВНижнемРегистре() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "ABC");

            FileHasher.ComputeHashes(path, out var sha, out var b3);

            Assert.Equal(sha.ToLowerInvariant(), sha);
            Assert.Equal(b3.ToLowerInvariant(), b3);
            Assert.Equal(64, sha.Length);
            Assert.Equal(64, b3.Length);
        }

        /// <summary>
        /// Пустой файл — вырожденный случай цикла «читаем, пока читается»: тело цикла
        /// не выполняется ни разу, и хеш обязан получиться из одного финального блока.
        /// В игре пустые файлы встречаются (заглушки, маркеры), и ошибка здесь означала бы
        /// вечное перекачивание нулевого файла.
        /// </summary>
        [Fact]
        public void ПустойФайлХешируетсяКорректно() {
            using var dir = new TempDir();
            var path = dir.WriteBytes("empty.bin", Array.Empty<byte>());

            FileHasher.ComputeHashes(path, out var sha, out var b3);

            Assert.Equal(TestHash.Sha256OfFile(path), sha);
            Assert.Equal(TestHash.Blake3OfFile(path), b3);
            // Известная константа: SHA-256 от нуля байт.
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sha);
        }

        /// <summary>
        /// Файл больше буфера (256 КБ) заставляет цикл сделать несколько проходов.
        /// Это единственный способ поймать ошибку инкрементального обновления хешеров
        /// (например, скармливание всего буфера вместо реально прочитанных r байт):
        /// на маленьких файлах последний блок всегда полный и баг не виден.
        /// </summary>
        [Fact]
        public void ФайлБольшеБуфераСчитаетсяЗаНесколькоПроходов() {
            using var dir = new TempDir();

            // 700 КБ: два полных блока по 256 КБ плюс неполный хвост.
            var bytes = new byte[700 * 1024];
            new Random(1234).NextBytes(bytes);
            var path = dir.WriteBytes("big.pak", bytes);

            FileHasher.ComputeHashes(path, out var sha, out var b3);

            Assert.Equal(TestHash.Sha256OfFile(path), sha);
            Assert.Equal(TestHash.Blake3OfFile(path), b3);
        }

        /// <summary>
        /// Проход по многогигабайтному файлу — это минуты, и без проверки токена
        /// «Отмена» в UI физически не срабатывала бы. Важнее другое: отменённый подсчёт
        /// не имеет права тихо вернуть результат — иначе битый файл будет «подтверждён».
        /// </summary>
        [Fact]
        public void ОтменаПрерываетПодсчёт() {
            using var dir = new TempDir();
            var path = dir.WriteBytes("big.pak", new byte[2 * 1024 * 1024]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => FileHasher.ComputeHashes(path, out _, out _, cts.Token));
        }

        /// <summary>
        /// Токен проверяется в начале каждого блока, включая самый первый: даже
        /// крошечный файл на отменённом токене не досчитывается. Это важно как раз
        /// для вердикта — отменённая проверка не должна возвращать «файл в порядке».
        /// </summary>
        [Fact]
        public void ОтменаСрабатываетДажеНаПервомБлоке() {
            using var dir = new TempDir();
            var path = dir.WriteFile("small.txt", "мелочь");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => FileHasher.ComputeHashes(path, out _, out _, cts.Token));
        }

        /// <summary>
        /// Отмена в середине сверки не имеет права превратиться в вердикт «качать».
        /// Иначе отменённая пользователем проверка целостности выглядела бы как
        /// «файл битый» и тянула бы гигабайты заново.
        /// </summary>
        [Fact]
        public void ОтменаПробрасываетсяЧерезMatches() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var size = new FileInfo(path).Length;
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => FileHasher.Matches(path, size, TestHash.Sha256OfFile(path), string.Empty, out _, cts.Token));
        }

        [Fact]
        public void ОтсутствующийФайлНеСовпадает() {
            using var dir = new TempDir();

            Assert.False(FileHasher.Matches(dir.PathTo("нет.bin"), 10, "aa", string.Empty, out var reason));
            Assert.Equal("missing", reason);
        }

        /// <summary>
        /// «Пусто = не проверяем» — общее правило для обоих путей. Если бы пустой sha256
        /// трактовался как «ожидается пустая строка», совпал бы ноль файлов и лаунчер
        /// качал бы установку целиком на каждом запуске.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void ПустойХешВМанифестеНеПроверяется(bool haveSha, bool haveB3) {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var size = new FileInfo(path).Length;
            var sha = haveSha ? TestHash.Sha256OfFile(path) : string.Empty;
            var b3 = haveB3 ? TestHash.Blake3OfFile(path) : string.Empty;

            Assert.True(FileHasher.Matches(path, size, sha, b3, out var reason), reason);
        }

        /// <summary>
        /// Регистр хеша в манифесте не должен влиять на вердикт: сервер может отдать
        /// hex в верхнем регистре, а локально он всегда считается в нижнем.
        /// </summary>
        [Fact]
        public void РегистрХешаВМанифестеНеВлияетНаВердикт() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var size = new FileInfo(path).Length;

            Assert.True(FileHasher.Matches(
                path,
                size,
                TestHash.Sha256OfFile(path).ToUpperInvariant(),
                TestHash.Blake3OfFile(path).ToUpperInvariant(),
                out var reason), reason);
        }

        /// <summary>
        /// Ранний выход по размеру — оптимизация, экономящая чтение гигабайтов.
        /// Вердикт она менять не имеет права: при другом размере хеш всё равно не сойдётся.
        /// Тест проверяет и то, и другое: файл отвергнут, причина — про размер, а не про хеш.
        /// </summary>
        [Fact]
        public void РазныйРазмерОтвергаетФайлБезЧтенияХеша() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "короткий");
            var size = new FileInfo(path).Length;

            Assert.False(FileHasher.Matches(path, size + 1000, TestHash.Sha256OfFile(path), string.Empty, out var reason));
            Assert.StartsWith("size ", reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Size = 0 в манифесте означает «размер неизвестен»: ранний выход отключается
        /// и решает только хеш. Иначе любой непустой файл с нулевым size в манифесте
        /// отвергался бы навсегда.
        /// </summary>
        [Fact]
        public void НулевойРазмерВМанифестеНеВключаетРаннийВыход() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");

            Assert.True(FileHasher.Matches(path, 0, TestHash.Sha256OfFile(path), string.Empty, out var reason), reason);
        }

        /// <summary>
        /// ГЛАВНЫЙ тест задачи Р5. Слева — путь файлов игры (SimpleSyncService.PlanAsync),
        /// справа — путь самообновления (UpdateWindow.LocalFileMatches, который теперь
        /// целиком делегирует в FileHasher.Matches). На одинаковых входах вердикты обязаны
        /// совпадать до последнего случая: расхождение здесь означает, что один код считает
        /// файл целым, а другой — битым, и лаунчер бесконечно предлагает одно и то же обновление.
        /// </summary>
        [Fact]
        public async Task ОбаПутиДаютОдинаковыйВердикт() {
            using var dir = new TempDir();
            var path = dir.WriteFile("game.exe", "содержимое игры");
            var size = new FileInfo(path).Length;
            var sha = TestHash.Sha256OfFile(path);
            var b3 = TestHash.Blake3OfFile(path);

            // Каждый случай — «как файл описан в манифесте». Ожидания намеренно не задаём:
            // тест сравнивает две реализации друг с другом, а не с зашитой таблицей.
            var cases = new[] {
                PlanTestData.File("game.exe", size, sha),
                PlanTestData.File("game.exe", size, sha, b3),
                PlanTestData.File("game.exe", size, sha256: null, blake3: b3),
                PlanTestData.File("game.exe", size, sha.ToUpperInvariant()),

                // Размер врет — тот самый ранний выход в пути самообновления.
                PlanTestData.File("game.exe", size + 777, sha),
                PlanTestData.File("game.exe", 0, sha),

                // Хеш чужой: файл обязан быть отвергнут обоими путями.
                PlanTestData.File("game.exe", size, new string('a', 64)),
                PlanTestData.File("game.exe", size, sha, new string('b', 64)),

                // Файла нет на диске.
                PlanTestData.File("нет-такого.bin", 42, sha),
            };

            foreach (var mf in cases) {
                var plan = await PlanTestData.PlanAsync(PlanTestData.Manifest(mf), dir.Root);
                var planSaysOk = !plan.Downloads.Any();

                var localPath = dir.PathTo(mf.Path!);
                var hasherSaysOk = FileHasher.Matches(localPath, mf.Size, mf.Sha256, mf.Blake3, out var reason);

                Assert.True(
                    planSaysOk == hasherSaysOk,
                    $"Вердикты разошлись для '{mf.Path}' size={mf.Size} sha='{mf.Sha256}' b3='{mf.Blake3}': " +
                    $"PlanAsync={planSaysOk}, FileHasher={hasherSaysOk} ({reason})");
            }
        }
    }
}
