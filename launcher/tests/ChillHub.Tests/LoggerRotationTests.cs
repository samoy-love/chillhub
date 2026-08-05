// <copyright file="LoggerRotationTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;

    using ChillHub.Core.Logging;

    using Xunit;

    /// <summary>
    /// Ротация журнала, устойчивость записи и решение «поднимать ли отчёт об ошибке».
    /// <para>
    /// Журнал — единственный источник правды при разборе жалоб, и у него две беды.
    /// Первая: он растёт вечно, если ротация не работает или после неё запись уходит
    /// в архив. Вторая: логгер держит файл открытым между записями, поэтому любое
    /// изменение файла со стороны (ротация соседним экземпляром, обрезка, удаление)
    /// способно тихо увести последующие строки в никуда.
    /// </para>
    /// <para>
    /// Ротация проверяется вызовом <c>Logger.Rotate</c> напрямую: добраться до неё через
    /// запись можно только пятью мегабайтами строк, и такой тест зависел бы от объёма
    /// ввода-вывода, а не от логики.
    /// </para>
    /// </summary>
    public class LoggerRotationTests {
        /// <summary>
        /// Ротация сдвигает архивы и выбрасывает самый старый: иначе каталог логов растёт
        /// без предела и однажды забивает диск пользователя.
        /// </summary>
        [Fact]
        public void РотацияСдвигаетАрхивыИУдаляетСамыйСтарый() {
            using var dir = new TempDir();
            var active = Path.Combine(dir.Root, "client.log");
            Write(active, "активный");
            Write(Path.Combine(dir.Root, "client.1.log"), "архив-1");
            Write(Path.Combine(dir.Root, "client.2.log"), "архив-2");

            // Храним три архива: client.3.log должен исчезнуть, а не уехать в client.4.log.
            Write(Path.Combine(dir.Root, "client.3.log"), "архив-3");

            Logger.Rotate(active);

            Assert.False(File.Exists(active), "активный файл обязан уехать в архив");
            Assert.Equal("активный", File.ReadAllText(Path.Combine(dir.Root, "client.1.log")));
            Assert.Equal("архив-1", File.ReadAllText(Path.Combine(dir.Root, "client.2.log")));
            Assert.Equal("архив-2", File.ReadAllText(Path.Combine(dir.Root, "client.3.log")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "client.4.log")), "четвёртый архив не хранится");
        }

        /// <summary>
        /// Ротация без файла и без архивов — не ошибка: так выглядит первый запуск,
        /// и падение здесь уронило бы запись первой же строки.
        /// </summary>
        [Fact]
        public void РотацияБезФайловНеПадает() {
            using var dir = new TempDir();

            Logger.Rotate(Path.Combine(dir.Root, "client.log"));

            Assert.False(File.Exists(Path.Combine(dir.Root, "client.1.log")));
        }

        /// <summary>
        /// Обрезка файла из-под открытого потока не теряет последующие записи.
        /// <para>
        /// Логгер держит дескриптор открытым и помнит, сколько он в файл написал. Если
        /// файл под ним укоротили (соседний экземпляр ротировал, чистилка обнулила),
        /// позиция дескриптора уезжает за конец файла, и новые строки ложатся за дырой
        /// из нулей — либо не находятся вовсе.
        /// </para>
        /// </summary>
        [Fact]
        public void ОбрезкаФайлаИзПодПотокаНеТеряетЗаписи() {
            using var dir = new TempDir();
            using (Logger.OverrideForTests(dir.Root)) {
                Logger.Info("строка до обрезки");

                using (var fs = new FileStream(
                    Logger.LogFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) {
                    fs.SetLength(0);
                }

                var marker = "строка после обрезки " + Guid.NewGuid().ToString("N");
                Logger.Info(marker);

                Assert.Contains(marker, ReadShared(Logger.LogFilePath));
            }
        }

        /// <summary>
        /// Удаление файла из-под открытого потока не теряет последующие записи.
        /// <para>
        /// Файл лога сносят чистилки диска, антивирусы и сам пользователь. Дескриптор
        /// переживает удаление, и логгер продолжал бы писать в файл, которого больше нет
        /// в каталоге: журнал выглядел бы пустым ровно тогда, когда его читают.
        /// </para>
        /// </summary>
        [Fact]
        public void УдалениеФайлаИзПодПотокаНеТеряетЗаписи() {
            using var dir = new TempDir();
            using (Logger.OverrideForTests(dir.Root)) {
                Logger.Info("строка до удаления");

                File.Delete(Logger.LogFilePath);

                var marker = "строка после удаления " + Guid.NewGuid().ToString("N");
                Logger.Info(marker);

                Assert.Contains(marker, ReadShared(Logger.LogFilePath));
            }
        }

        /// <summary>
        /// Пропавший каталог логов не роняет приложение, и запись возобновляется, когда
        /// каталог возвращается. Логгер зовут отовсюду, включая обработчики ошибок:
        /// исключение отсюда превратило бы мелкий сбой диска в падение лаунчера.
        /// </summary>
        [Fact]
        public void ПропавшийКаталогНеРоняетЛоггер() {
            using var dir = new TempDir();
            var missing = Path.Combine(dir.Root, "нет-каталога");
            using (Logger.OverrideForTests(missing)) {
                Directory.Delete(missing, recursive: true);

                // Ни исключения, ни файла: писать некуда, но приложение живёт дальше.
                Logger.Info("строка в никуда");
                Assert.False(File.Exists(Logger.LogFilePath));

                Directory.CreateDirectory(missing);
                var marker = "строка после возврата каталога " + Guid.NewGuid().ToString("N");
                Logger.Info(marker);

                Assert.Contains(marker, ReadShared(Logger.LogFilePath));
            }
        }

        /// <summary>
        /// Запись включена по умолчанию и выключается только явным отказом.
        /// <para>
        /// Дефолт здесь дороже, чем кажется: без журналов обратная связь и авто-отчёты
        /// приходят пустыми, то есть жалобы становятся неразбираемыми. Поэтому всё, кроме
        /// перечисленных «нет», означает «писать» — в том числе мусор в переменной.
        /// </para>
        /// </summary>
        /// <param name="raw">Значение CHILLHUB_CLIENT_LOG.</param>
        /// <param name="expected">Ожидаемое решение.</param>
        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("on", true)]
        [InlineData("yes", true)]
        [InlineData("какой-то мусор", true)]
        [InlineData("0", false)]
        [InlineData("false", false)]
        [InlineData("off", false)]
        [InlineData("no", false)]
        [InlineData(" OFF ", false)]
        [InlineData("False", false)]
        public void ПеременнаяОкруженияРешаетПисатьЛиЖурнал(string? raw, bool expected) {
            var previous = Environment.GetEnvironmentVariable("CHILLHUB_CLIENT_LOG");
            try {
                Environment.SetEnvironmentVariable("CHILLHUB_CLIENT_LOG", raw);

                Assert.Equal(expected, Logger.ResolveEnabled());
            }
            finally {
                Environment.SetEnvironmentVariable("CHILLHUB_CLIENT_LOG", previous);
            }
        }

        /// <summary>
        /// Журнал ложится в роуминг, а не в каталог установки.
        /// <para>
        /// %LOCALAPPDATA%\ChillHub — это папка, куда установлен сам лаунчер, и
        /// самообновление сносит её содержимое вместе со старыми файлами. Журнал,
        /// положенный туда, исчезал бы ровно при том обновлении, разбирать которое
        /// он и нужен.
        /// </para>
        /// </summary>
        [Fact]
        public void ЖурналЛожитсяВРоумингАНеВКаталогУстановки() {
            var dir = Logger.ResolveLogDirectory();

            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var install = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            Assert.StartsWith(roaming, dir, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.Combine(install, "ChillHub"), dir, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(dir), "каталог логов должен существовать после выбора");
        }

        /// <summary>
        /// Каталог по умолчанию — тот же, куда логгер пишет без подмены. Разъезд означал бы,
        /// что диагностика собирает логи не из того места, куда они ложатся.
        /// </summary>
        [Fact]
        public void КаталогПоУмолчаниюСовпадаетСТемКудаПишетЛоггер() {
            Assert.Equal(Logger.ResolveLogDirectory(), Logger.LogDirectory, ignoreCase: true);
            Assert.Equal(Path.Combine(Logger.LogDirectory, "client.log"), Logger.LogFilePath);
        }

        /// <summary>
        /// Сбой связи отличается от дефекта кода, в том числе завёрнутый.
        /// <para>
        /// На этом решении держится защита от петли: авто-отчёт уходит по сети, и если
        /// считать обрыв связи дефектом, то каждая неудачная отправка отчёта порождала бы
        /// новый отчёт. Плюс запуск без интернета показывал пользователю «Произошла
        /// ошибка. Отчёт автоматически отправлен» на ровном месте.
        /// </para>
        /// </summary>
        [Fact]
        public void СетевойСбойОтличаетсяОтДефекта() {
            var network = new Exception[] {
                new HttpRequestException("нет связи"),
                new SocketException(10061),
                new System.Net.WebException("отказ"),
                new TimeoutException("не дождались"),
                new TaskCanceledException("отменено"),
                new OperationCanceledException("отменено"),

                // HttpClient заворачивает сокетные ошибки, поэтому вложенные тоже сетевые.
                new InvalidOperationException("обёртка", new SocketException(10060)),
                new AggregateException(new HttpRequestException("вложенная")),
            };

            var defects = new Exception[] {
                new InvalidOperationException("дефект кода"),
                new NullReferenceException(),
                new IOException("диск переполнен"),
                new UnauthorizedAccessException("нет прав"),
            };

            Assert.All(network, ex => Assert.True(Logger.IsNetworkFailure(ex), ex.GetType().Name));
            Assert.All(defects, ex => Assert.False(Logger.IsNetworkFailure(ex), ex.GetType().Name));
        }

        /// <summary>Отсутствие исключения сетевым сбоем не считается.</summary>
        [Fact]
        public void ОтсутствиеИсключенияНеСетевойСбой() {
            Assert.False(Logger.IsNetworkFailure(null));
        }

        /// <summary>
        /// ErrorNoReport пишет в журнал полный текст исключения: он и есть весь материал
        /// для разбора, раз отчёт по этому пути не отправляется.
        /// </summary>
        [Fact]
        public void ErrorNoReportПишетИсключениеВЖурнал() {
            using var dir = new TempDir();
            using (Logger.OverrideForTests(dir.Root)) {
                var marker = "контекст-" + Guid.NewGuid().ToString("N");

                Logger.ErrorNoReport(new InvalidOperationException("подробность отказа"), marker);

                var text = ReadShared(Logger.LogFilePath);
                Assert.Contains("ERROR", text);
                Assert.Contains(marker, text);
                Assert.Contains("подробность отказа", text);
            }
        }

        /// <summary>
        /// Сетевое исключение, пришедшее в обычный Error, всё равно попадает в журнал —
        /// уход по «тихой» ветке не должен превращаться в потерю записи.
        /// </summary>
        [Fact]
        public void СетевоеИсключениеВОбычномErrorВсёРавноПопадаетВЖурнал() {
            using var dir = new TempDir();
            using (Logger.OverrideForTests(dir.Root)) {
                var marker = "сеть-" + Guid.NewGuid().ToString("N");
                var ex = new HttpRequestException("сервер недоступен");

                Assert.True(Logger.IsNetworkFailure(ex), "иначе этот путь поднял бы авто-отчёт");
                Logger.Error(ex, marker);

                var text = ReadShared(Logger.LogFilePath);
                Assert.Contains(marker, text);
                Assert.Contains("сервер недоступен", text);
            }
        }

        /// <summary>Чтение файла, который кто-то держит открытым на запись.</summary>
        /// <param name="path">Путь к файлу.</param>
        /// <returns>Содержимое файла.</returns>
        private static string ReadShared(string path) {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, new UTF8Encoding(false));
            return reader.ReadToEnd();
        }

        private static void Write(string path, string content)
            => File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
