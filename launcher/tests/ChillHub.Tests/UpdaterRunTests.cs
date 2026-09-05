// <copyright file="UpdaterRunTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Прогон апдейтера целиком: от аргументов командной строки до записи исхода
    /// и перезапуска лаунчера.
    /// <para>
    /// Настоящие процессы здесь не запускаются и не ожидаются — они за швом
    /// (<c>UpdaterHost</c>). Проверяется именно то, что делает сам апдейтер:
    /// разбирает аргументы, ждёт родителя ДО работы с файлами, применяет обновление
    /// и в любом исходе оставляет запись на диске и поднимает лаунчер обратно.
    /// </para>
    /// </summary>
    public class UpdaterRunTests {
        /// <summary>
        /// Штатный прогон: файлы применены, маркер записан, исход лежит на диске,
        /// лаунчер поднят обратно. Это ровно то, что видит пользователь при обновлении.
        /// </summary>
        [Fact]
        public async Task ПолныйПрогонПрименяетОбновлениеИПоднимаетЛаунчер() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var started = new List<ProcessStartInfo>();
            var host = Host(started);

            var exit = await global::Program.RunMainAsync(
                Args(dir, exe, "--files", files, "--version", "1.2.3"),
                host);

            Assert.Equal(0, exit);
            Assert.Equal("новая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
            Assert.Equal("1.2.3", File.ReadAllText(dir.PathTo("dst/launcher.version")));

            var status = UpdateStatus.TryRead(dir.PathTo("dst"));
            Assert.NotNull(status);
            Assert.True(status!.IsSuccess);
            Assert.Equal(0, status.ExitCode);
            Assert.Equal("1.2.3", status.Version);

            Assert.Single(started);
            Assert.Equal(exe, started[0].FileName);
        }

        /// <summary>
        /// После удачного обновления оболочке говорят перечитать значки.
        /// <para>
        /// Ярлыки лаунчера своей иконки не задают и берут её из ресурса exe, но Windows
        /// держит разобранные значки в кеше. Без этого толчка смена значка выглядит как
        /// «обновилось, а иконка прежняя» — иногда до перезагрузки.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПослеОбновленияОболочкеГоворятПеречитатьЗначки() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var started = new List<ProcessStartInfo>();
            var icons = new List<string>();

            var exit = await global::Program.RunMainAsync(
                Args(dir, exe, "--files", files, "--version", "1.2.3"),
                Host(started, icons));

            Assert.Equal(0, exit);
            Assert.Single(icons);
        }

        /// <summary>
        /// Значки перечитываются ДО того, как лаунчер вернётся на экран.
        /// <para>
        /// Порядок здесь и есть смысл: если толкнуть оболочку после перезапуска, окно
        /// уже нарисовано со старым значком, и в панели задач он таким и останется до
        /// следующего запуска.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ЗначкиОбновляютсяДоПерезапускаЛаунчера() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var order = new List<string>();
            var host = new global::Program.UpdaterHost {
                WaitForParent = (_, _) => { },
                StartProcess = _ => {
                    order.Add("restart");
                    return 4242;
                },
                Sleep = _ => { },
                RefreshIconCache = _ => order.Add("icons"),
            };

            await global::Program.RunMainAsync(
                Args(dir, exe, "--files", files, "--version", "1.2.3"),
                host);

            Assert.Equal(new[] { "icons", "restart" }, order);
        }

        /// <summary>
        /// Родителя ждём ДО первой записи в папку установки. Скопировать поверх живого
        /// лаунчера нельзя: его exe и dll заблокированы, половина файлов не встанет,
        /// и обновление развалится без внятной причины.
        /// </summary>
        [Fact]
        public async Task РодительДожидаетсяДоПервойЗаписи() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var contentWhenWaited = string.Empty;
            var host = Host(new List<ProcessStartInfo>());
            host.WaitForParent = (_, _) => contentWhenWaited = File.ReadAllText(dir.PathTo("dst/ChillHub.dll"));

            var exit = await global::Program.RunMainAsync(Args(dir, exe, "--files", files), host);

            Assert.Equal(0, exit);
            Assert.Equal("старая сборка", contentWhenWaited);
        }

        /// <summary>
        /// Идентификатор родителя доезжает до ожидания как есть: подставить сюда ноль
        /// значит не ждать никого и начать копировать поверх работающего лаунчера.
        /// </summary>
        [Fact]
        public async Task ИдентификаторРодителяДоезжаетДоОжидания() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var waited = -1;
            var host = Host(new List<ProcessStartInfo>());
            host.WaitForParent = (pid, _) => waited = pid;

            await global::Program.RunMainAsync(Args(dir, exe, "--parent", "4242"), host);

            Assert.Equal(4242, waited);
        }

        /// <summary>
        /// Непонятый --parent останавливает обновление до единой записи в папку
        /// установки. Молча превратить мусор в ноль означало бы копирование поверх
        /// живого лаунчера, а «--parent --dst C:\app» (потерянное значение) внешне
        /// ничем не отличается от нормальной команды.
        /// </summary>
        [Theory]
        [InlineData("мусор")]
        [InlineData("-1")]
        public async Task НепонятыйParentОстанавливаетОбновление(string parent) {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");

            var host = Host(new List<ProcessStartInfo>());
            var exit = await global::Program.RunMainAsync(
                Args(dir, exe, "--parent", parent, "--version", "1.2.3"),
                host);

            Assert.Equal(3, exit);
            Assert.Equal("старая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
            Assert.False(File.Exists(dir.PathTo("dst/launcher.version")));

            var status = UpdateStatus.TryRead(dir.PathTo("dst"));
            Assert.NotNull(status);
            Assert.False(status!.IsSuccess);
        }

        /// <summary>
        /// Отсутствие обязательного аргумента — не повод оставить пользователя без
        /// приложения. Лаунчер закрыл себя сам, чтобы освободить файлы; если апдейтер
        /// после этого просто умрёт, окна не будет вообще ни у кого.
        /// <para>
        /// Ровно так и было: каталог установки с путём к exe запоминались ПОСЛЕ
        /// разбора всех обязательных аргументов, поэтому пропущенный --src оставлял
        /// перезапуск без единого кандидата.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ПропущенныйАргументНеОтменяетПерезапускЛаунчера() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var started = new List<ProcessStartInfo>();
            var host = Host(started);

            var exit = await global::Program.RunMainAsync(
                new[] { "--dst", dir.PathTo("dst"), "--exe", exe, "--parent", "0", "--log", dir.PathTo("update.log") },
                host);

            Assert.Equal(3, exit);
            Assert.Single(started);
            Assert.Equal(exe, started[0].FileName);
        }

        /// <summary>
        /// Исход фатального прогона тоже уезжает на диск: код возврата апдейтера
        /// не читает никто (родителя к этому моменту уже нет), и запись рядом с
        /// маркером версии — единственный способ объяснить пользователю, что случилось.
        /// </summary>
        [Fact]
        public async Task ФатальныйИсходДоезжаетДоФайлаСостояния() {
            using var dir = new TempDir();
            Directory.CreateDirectory(dir.PathTo("dst"));
            var exe = dir.PathTo("dst/ChillHub.exe");

            var exit = await global::Program.RunMainAsync(
                new[] { "--dst", dir.PathTo("dst"), "--exe", exe, "--parent", "0", "--log", dir.PathTo("update.log") },
                Host(new List<ProcessStartInfo>()));

            var status = UpdateStatus.TryRead(dir.PathTo("dst"));
            Assert.NotNull(status);
            Assert.Equal(exit, status!.ExitCode);
            Assert.Equal("fatal", status.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(status.Message));
            Assert.Contains("fatal:", File.ReadAllText(dir.PathTo("update.log")), StringComparison.Ordinal);
        }

        /// <summary>
        /// Аргументы, с которыми лаунчер был запущен, восстанавливаются дословно:
        /// иначе после обновления он поднимется «не той» игрой или без нужного режима,
        /// а пути с пробелами развалятся на части.
        /// </summary>
        [Fact]
        public async Task ИсходныеАргументыЛаунчераВосстанавливаютсяДословно() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var argsFile = dir.WriteFile("exe-args.txt", "--game\r\nC:\\Program Files\\Игра с пробелами\r\n");

            var started = new List<ProcessStartInfo>();
            var host = Host(started);

            await global::Program.RunMainAsync(Args(dir, exe, "--exe-args-file", argsFile), host);

            Assert.Single(started);
            Assert.Equal(new[] { "--game", "C:\\Program Files\\Игра с пробелами" }, started[0].ArgumentList);
        }

        // Базовый набор аргументов: временные каталоги src/dst и родитель, которого ждать не надо.
        private static string[] Args(TempDir dir, string exe, params string[] extra) {
            Directory.CreateDirectory(dir.PathTo("src"));
            var args = new List<string> {
                "--src", dir.PathTo("src"),
                "--dst", dir.PathTo("dst"),
                "--exe", exe,
                "--log", dir.PathTo("update.log"),
            };
            if (Array.IndexOf(extra, "--parent") < 0) {
                args.Add("--parent");
                args.Add("0");
            }

            args.AddRange(extra);
            return args.ToArray();
        }

        // Шов, в котором ничего не запускается и никто не ждёт.
        private static global::Program.UpdaterHost Host(List<ProcessStartInfo> started, List<string>? icons = null)
            => new global::Program.UpdaterHost {
                WaitForParent = (_, _) => { },
                StartProcess = psi => {
                    started.Add(psi);
                    return 4242;
                },
                Sleep = _ => { },
                RefreshIconCache = _ => icons?.Add("refreshed"),
            };
    }

    /// <summary>
    /// Перезапуск лаунчера после обновления.
    /// <para>
    /// Лаунчер завершил себя сам, чтобы освободить файлы. Если апдейтер его не
    /// поднимет, у пользователя не останется ничего: окно закрылось, новое не
    /// открылось, а причина видна только в логе, который он не найдёт. Поэтому
    /// перезапуск идёт при ЛЮБОМ исходе и по нескольким кандидатам.
    /// </para>
    /// </summary>
    public class UpdaterRestartTests {
        /// <summary>
        /// Кандидатов несколько и порядок у них не случайный: сначала тот exe, из
        /// которого лаунчер был запущен, затем файл с тем же именем в обновлённой
        /// папке, и только потом имя по умолчанию.
        /// </summary>
        [Fact]
        public void КандидатыИдутОтТочногоПутиКИмениПоУмолчанию() {
            var ctx = new global::Program.RunContext {
                Exe = @"C:\Temp\Старая копия\ChillHub.exe",
                Dst = @"C:\Install",
            };

            Assert.Equal(
                new[] {
                    @"C:\Temp\Старая копия\ChillHub.exe",
                    @"C:\Install\ChillHub.exe",
                    @"C:\Install\ChillHub.exe",
                },
                global::Program.RestartCandidates(ctx));
        }

        /// <summary>
        /// Когда лаунчер запущен прямо из папки установки, «тот же файл в обновлённой
        /// папке» — это он сам, и отдельным кандидатом он не становится.
        /// </summary>
        [Fact]
        public void ExeВПапкеУстановкиНеДобавляетТретийКандидат() {
            var ctx = new global::Program.RunContext {
                Exe = @"C:\Install\ChillHub.exe",
                Dst = @"C:\Install",
            };

            Assert.Equal(
                new[] { @"C:\Install\ChillHub.exe", @"C:\Install\ChillHub.exe" },
                global::Program.RestartCandidates(ctx));
        }

        /// <summary>Без каталога установки и без exe кандидатов нет — придумывать путь неоткуда.</summary>
        [Fact]
        public void БезПутейКандидатовНет() {
            Assert.Empty(global::Program.RestartCandidates(new global::Program.RunContext()));
        }

        /// <summary>
        /// Запускается ПЕРВЫЙ существующий кандидат, и ровно один раз: запустить
        /// вдобавок второй значит поднять два лаунчера сразу.
        /// </summary>
        [Fact]
        public void ЗапускаетсяПервыйСуществующийКандидатИТолькоОдин() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var ctx = new global::Program.RunContext { Exe = exe, Dst = dir.PathTo("dst") };
            var started = new List<ProcessStartInfo>();

            global::Program.Restart(ctx, new UpdateLog(), Host(started, _ => 4242));

            Assert.Single(started);
            Assert.Equal(exe, started[0].FileName);
            Assert.Equal(dir.PathTo("dst"), started[0].WorkingDirectory);
        }

        /// <summary>
        /// Несуществующий exe пропускается в пользу следующего кандидата: после
        /// обновления лаунчер мог переехать, и упереться в старый путь означало бы
        /// не поднять его вовсе.
        /// </summary>
        [Fact]
        public void НесуществующийКандидатПропускаетсяВПользуСледующего() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var ctx = new global::Program.RunContext {
                Exe = dir.PathTo("временная копия/ChillHub.exe"),
                Dst = dir.PathTo("dst"),
            };
            var started = new List<ProcessStartInfo>();

            global::Program.Restart(ctx, new UpdateLog(), Host(started, _ => 4242));

            Assert.Single(started);
            Assert.Equal(dir.PathTo("dst/ChillHub.exe"), started[0].FileName);
        }

        /// <summary>
        /// Сорвавшийся запуск повторяется: типичная причина отказа — антивирус,
        /// который ещё держит только что подменённый exe, и через секунду запуск
        /// проходит. Сдаться с первой попытки значит оставить пользователя ни с чем.
        /// </summary>
        [Fact]
        public void СорвавшийсяЗапускПовторяется() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var ctx = new global::Program.RunContext { Exe = exe, Dst = dir.PathTo("dst") };
            var started = new List<ProcessStartInfo>();
            var attempts = 0;
            var log = new UpdateLog();
            log.Open(dir.PathTo("update.log"));

            global::Program.Restart(ctx, log, Host(started, _ => ++attempts < 3 ? throw new InvalidOperationException("занят антивирусом") : 4242));

            Assert.Equal(3, attempts);
            var text = File.ReadAllText(dir.PathTo("update.log"));
            Assert.Contains("launcher restarted", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CRITICAL", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Отказ запуска, вернувший «ничего», тоже не считается успехом: иначе
        /// апдейтер отчитается о перезапуске, которого не было.
        /// </summary>
        [Fact]
        public void ЗапускВернувшийНичегоНеСчитаетсяУспехом() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var ctx = new global::Program.RunContext { Exe = exe, Dst = dir.PathTo("dst") };
            var started = new List<ProcessStartInfo>();
            var log = new UpdateLog();
            log.Open(dir.PathTo("update.log"));

            global::Program.Restart(ctx, log, Host(started, _ => null));

            // Два кандидата на трёх попытках — шесть обращений, и ни одно не засчитано.
            Assert.Equal(6, started.Count);
            Assert.Contains("CRITICAL", File.ReadAllText(dir.PathTo("update.log")), StringComparison.Ordinal);
        }

        /// <summary>
        /// Перезапуск, отменённый по замыслу (обновление применяет другой апдейтер),
        /// не поднимает лаунчер: иначе два процесса начали бы работать в одной папке.
        /// </summary>
        [Fact]
        public void ОтменённыйПерезапускНичегоНеЗапускает() {
            using var dir = new TempDir();
            var exe = dir.WriteFile("dst/ChillHub.exe", "лаунчер");
            var ctx = new global::Program.RunContext { Exe = exe, Dst = dir.PathTo("dst"), Restart = false };
            var started = new List<ProcessStartInfo>();

            global::Program.Restart(ctx, new UpdateLog(), Host(started, _ => 4242));

            Assert.Empty(started);
        }

        private static global::Program.UpdaterHost Host(List<ProcessStartInfo> started, Func<ProcessStartInfo, int?> start)
            => new global::Program.UpdaterHost {
                WaitForParent = (_, _) => { },
                StartProcess = psi => {
                    started.Add(psi);
                    return start(psi);
                },
                Sleep = _ => { },
            };
    }

    /// <summary>
    /// Запись исхода обновления рядом с маркером версии.
    /// <para>
    /// Код возврата апдейтера не читает никто: родительский лаунчер к моменту
    /// завершения уже закрыт. Файл состояния — единственное, по чему лаунчер при
    /// следующем старте может объяснить пользователю, почему обновление не приехало.
    /// Поэтому важно не «что-то записать», а записать ровно то, что лаунчер читает.
    /// </para>
    /// </summary>
    public class UpdaterWriteStatusTests {
        /// <summary>Записанный исход обязан читаться лаунчером — поле в поле.</summary>
        [Fact]
        public void ЗаписанныйИсходЧитаетсяЛаунчером() {
            using var dir = new TempDir();
            var log = new UpdateLog();
            log.Open(dir.PathTo("update.log"));
            var ctx = new global::Program.RunContext {
                Dst = dir.Root,
                Outcome = "copy-errors",
                Version = "1.2.3",
                Message = "Обновление не применено: файл занят.",
            };

            global::Program.WriteStatus(ctx, 2, log);

            var status = UpdateStatus.TryRead(dir.Root);
            Assert.NotNull(status);
            Assert.Equal("copy-errors", status!.Outcome);
            Assert.Equal(2, status.ExitCode);
            Assert.Equal("1.2.3", status.Version);
            Assert.Equal("Обновление не применено: файл занят.", status.Message);
            Assert.Equal(dir.PathTo("update.log"), status.LogPath);
            Assert.False(status.IsSuccess);
        }

        /// <summary>
        /// Пустой исход записывается как фатальный: «пусто» лаунчер прочитал бы как
        /// «не успех, но и не ошибка» и промолчал бы, хотя обновление не доехало.
        /// </summary>
        [Fact]
        public void ПустойИсходЗаписываетсяКакФатальный() {
            using var dir = new TempDir();
            var ctx = new global::Program.RunContext { Dst = dir.Root, Outcome = string.Empty };

            global::Program.WriteStatus(ctx, 3, new UpdateLog());

            Assert.Equal("fatal", UpdateStatus.TryRead(dir.Root)!.Outcome);
        }

        /// <summary>
        /// Многострочное сообщение не разваливает файл состояния: одна запись обязана
        /// остаться одной записью, иначе лаунчер прочитает обрывок вместо объяснения.
        /// </summary>
        [Fact]
        public void МногострочноеСообщениеНеРазваливаетФайл() {
            using var dir = new TempDir();
            var ctx = new global::Program.RunContext {
                Dst = dir.Root,
                Outcome = "fatal",
                Message = "Первая строка\nвторая строка",
            };

            global::Program.WriteStatus(ctx, 3, new UpdateLog());

            var status = UpdateStatus.TryRead(dir.Root);
            Assert.Equal("Первая строка\nвторая строка", status!.Message);
            Assert.Equal("fatal", status.Outcome);
        }

        /// <summary>
        /// Без известного каталога установки писать некуда, и это объясняется в
        /// журнале, а не проглатывается: иначе пропавший файл состояния выглядит
        /// как сбой записи на диск.
        /// </summary>
        [Fact]
        public void БезКаталогаУстановкиПричинаПопадаетВЖурнал() {
            using var dir = new TempDir();
            var log = new UpdateLog();
            log.Open(dir.PathTo("update.log"));

            global::Program.WriteStatus(new global::Program.RunContext(), 3, log);

            Assert.Contains("update status not written", File.ReadAllText(dir.PathTo("update.log")), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Выкладка списков обновления в журнал.
    /// <para>
    /// Временные списки после прогона удаляются, и журнал остаётся единственным
    /// свидетельством того, что именно апдейтер собирался сделать. При этом сама
    /// выкладка — диагностика: она не имеет права уронить обновление, что бы ни
    /// лежало по указанным путям.
    /// </para>
    /// </summary>
    public class UpdaterLogListsTests {
        /// <summary>Каждая строка списков попадает в журнал: без этого разбор чужого сбоя невозможен.</summary>
        [Fact]
        public void СодержимоеСписковПопадаетВЖурнал() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\nsub/lib.dll\r\n");
            var dirs = dir.WriteFile("emptydirs.txt", "data/cache\r\n");
            var del = dir.WriteFile("deletelist.txt", "старое.dll\r\n");
            var log = new UpdateLog();
            log.Open(dir.PathTo("update.log"));

            global::Program.LogLists(files, dirs, del, log);

            var text = File.ReadAllText(dir.PathTo("update.log"));
            Assert.Contains("FILES: ChillHub.dll", text, StringComparison.Ordinal);
            Assert.Contains("FILES: sub/lib.dll", text, StringComparison.Ordinal);
            Assert.Contains("DIRS: data/cache", text, StringComparison.Ordinal);
            Assert.Contains("DEL: старое.dll", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Отсутствующий список отмечается в журнале, но прогон не роняет: диффовое
        /// обновление штатно приходит без deletelist, и падать тут не на чем.
        /// </summary>
        [Fact]
        public void ОтсутствующийСписокОтмечаетсяИНеРоняетПрогон() {
            using var dir = new TempDir();
            var log = new UpdateLog();
            log.Open(dir.PathTo("update.log"));

            global::Program.LogLists(dir.PathTo("нет-такого.txt"), string.Empty, "   ", log);

            Assert.Contains("FILES list missing", File.ReadAllText(dir.PathTo("update.log")), StringComparison.Ordinal);
        }

        /// <summary>
        /// Нечитаемый список не срывает обновление: это диагностика, а сам список
        /// уже прочитан и проверен другими шагами. Уронить прогон здесь значило бы
        /// отказать в обновлении из-за записи в лог.
        /// </summary>
        [Fact]
        public void НечитаемыйСписокНеРоняетПрогон() {
            using var dir = new TempDir();
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");
            var log = new UpdateLog();
            log.Open(dir.PathTo("update.log"));

            using var hold = new FileStream(files, FileMode.Open, FileAccess.Read, FileShare.None);

            global::Program.LogLists(files, string.Empty, string.Empty, log);

            Assert.Contains("lists log error", File.ReadAllText(dir.PathTo("update.log")), StringComparison.Ordinal);
        }
    }
}
