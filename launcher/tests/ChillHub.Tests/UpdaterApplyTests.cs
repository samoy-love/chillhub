// <copyright file="UpdaterApplyTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Применение обновления к папке установки.
    /// <para>
    /// Апдейтер пишет поверх БОЕВОЙ установки — единственной копии лаунчера на
    /// машине. Ошибка здесь оставляет пользователя с приложением, которое уже не
    /// запускается, а значит и обновиться само не может: чинится только
    /// переустановкой. Поэтому файловая часть проверяется целиком и на настоящих
    /// каталогах: копирование, сверка содержимого, откат, удаления, маркер версии.
    /// </para>
    /// </summary>
    public class UpdaterApplyTests {
        /// <summary>
        /// Базовый сценарий: файлы из списка встают на место, маркер версии
        /// обновляется, исход — «ok». Если это перестанет работать, обновления
        /// не применяются вообще ни у кого.
        /// </summary>
        [Fact]
        public async Task ОбновлениеСтавитФайлыИПишетМаркерВерсии() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            dir.WriteFile("src/sub/lib.dll", "новая библиотека");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\nsub/lib.dll\r\n");

            var (exit, ctx, _) = await Apply(dir, r => {
                r.Files = files;
                r.Version = "1.2.3";
            });

            Assert.Equal(0, exit);
            Assert.Equal("ok", ctx.Outcome);
            Assert.Equal("новая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
            Assert.Equal("новая библиотека", File.ReadAllText(dir.PathTo("dst/sub/lib.dll")));
            Assert.Equal("1.2.3", File.ReadAllText(dir.PathTo("dst/launcher.version")));
        }

        /// <summary>
        /// Маркер версии пишется БЕЗ BOM и без перевода строки: лаунчер сверяет его
        /// с версией сервера как есть, и три лишних байта превращают «1.2.3»
        /// в «не совпадает» — обновление предлагалось бы при каждом старте.
        /// </summary>
        [Fact]
        public async Task МаркерВерсииПишетсяБезBOM() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            await Apply(dir, r => {
                r.Files = files;
                r.Version = "  1.2.3  ";
            });

            Assert.Equal(
                Encoding.UTF8.GetBytes("1.2.3"),
                File.ReadAllBytes(dir.PathTo("dst/launcher.version")));
        }

        /// <summary>
        /// Файл, обещанный списком, но отсутствующий в пакете, — это сбой, а не
        /// «нечего копировать». Раньше запись пропускалась, прогон доходил до маркера
        /// версии со старыми сборками на диске, и лаунчер считал такую установку
        /// исправной навсегда: предохранитель «версия совпала» выходит из проверки
        /// до сверки хешей.
        /// </summary>
        [Fact]
        public async Task ФайлОтсутствующийВПакетеСрываетОбновление() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\nпропал.dll\r\n");

            var (exit, ctx, _) = await Apply(dir, r => {
                r.Files = files;
                r.Version = "1.2.3";
            });

            Assert.Equal(2, exit);
            Assert.Equal("copy-errors", ctx.Outcome);
            Assert.False(File.Exists(dir.PathTo("dst/launcher.version")));
        }

        /// <summary>
        /// Сорванное обновление возвращает установку в ИСХОДНОЕ состояние целиком.
        /// Смесь старых и новых файлов — это и есть неработающий лаунчер: половина
        /// сборки ушла вперёд, половина осталась, и восстановиться сам он уже не может.
        /// </summary>
        [Fact]
        public async Task СорванноеОбновлениеОткатываетУжеПоложенныеФайлы() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\nпропал.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => {
                r.Files = files;
                r.Version = "1.2.3";
            });

            Assert.Equal(2, exit);
            Assert.Equal("старая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
        }

        /// <summary>
        /// Откат не оставляет за собой служебных файлов транзакции: .chbak рядом
        /// с восстановленным файлом попадёт в сверку с сервером как лишний файл,
        /// и лаунчер начнёт «чинить» установку при каждом запуске.
        /// </summary>
        [Fact]
        public async Task ОткатНеОставляетБэкаповВУстановке() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\nпропал.dll\r\n");

            await Apply(dir, r => r.Files = files);

            Assert.Empty(Directory.GetFiles(dir.PathTo("dst"), "*" + AtomicFile.BackupSuffix, SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(dir.PathTo("dst"), "*" + AtomicFile.TempSuffix, SearchOption.AllDirectories));
        }

        /// <summary>
        /// Файл из preserve не перезаписывается, даже если он есть и в пакете, и в списке.
        /// Перезапись config.json стирает настройки пользователя, а перезапись
        /// launcher.version — та самая причина, по которой лаунчер уходил в вечный
        /// цикл самообновления.
        /// </summary>
        [Fact]
        public async Task ФайлИзPreserveНеПерезаписывается() {
            using var dir = new TempDir();
            dir.WriteFile("dst/config.json", "{\"настройки\":\"пользователя\"}");
            dir.WriteFile("src/config.json", "{\"настройки\":\"из пакета\"}");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "config.json\r\nChillHub.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Files = files);

            Assert.Equal(0, exit);
            Assert.Equal("{\"настройки\":\"пользователя\"}", File.ReadAllText(dir.PathTo("dst/config.json")));
        }

        /// <summary>
        /// Файл из preserve не удаляется по deletelist: сервер не знает о машинном
        /// состоянии пользователя, и снести config.json по списку — то же самое,
        /// что стереть его настройки.
        /// </summary>
        [Fact]
        public async Task ФайлИзPreserveНеУдаляется() {
            using var dir = new TempDir();
            dir.WriteFile("dst/config.json", "{\"настройки\":\"пользователя\"}");
            var del = dir.WriteFile("deletelist.txt", "config.json\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Del = del);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(dir.PathTo("dst/config.json")));
        }

        /// <summary>
        /// Служебные файлы апдейтера из пакета в установку не переносятся: попав туда,
        /// они смешиваются с артефактами текущего прогона и остаются в папке навсегда.
        /// </summary>
        [Fact]
        public async Task СлужебныеФайлыАпдейтераИзПакетаНеКопируются() {
            using var dir = new TempDir();
            dir.WriteFile("src/filelist.txt", "мусор");
            dir.WriteFile("src/updater/helper.dll", "мусор");
            var files = dir.WriteFile("filelist.txt", "filelist.txt\r\nupdater/helper.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Files = files);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(dir.PathTo("dst/filelist.txt")));
            Assert.False(File.Exists(dir.PathTo("dst/updater/helper.dll")));
        }

        /// <summary>
        /// Удаления выполняются ровно по списку и не трогают ничего сверх него:
        /// лишний удалённый файл — это дыра в установке, из которой лаунчер не стартует.
        /// </summary>
        [Fact]
        public async Task DeletelistУдаляетТолькоПрошенное() {
            using var dir = new TempDir();
            dir.WriteFile("dst/старое.dll", "ушло");
            dir.WriteFile("dst/sub/тоже-старое.dll", "ушло");
            dir.WriteFile("dst/нужное.dll", "осталось");
            var del = dir.WriteFile("deletelist.txt", "старое.dll\r\nsub/тоже-старое.dll\r\nникогда-не-было.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Del = del);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(dir.PathTo("dst/старое.dll")));
            Assert.False(File.Exists(dir.PathTo("dst/sub/тоже-старое.dll")));
            Assert.True(File.Exists(dir.PathTo("dst/нужное.dll")));
        }

        /// <summary>
        /// Файл «только для чтения» всё равно удаляется: иначе он остаётся в установке
        /// навсегда и вечно расходится со сверкой по манифесту.
        /// </summary>
        [Fact]
        public async Task ФайлТолькоДляЧтенияУдаляется() {
            using var dir = new TempDir();
            var target = dir.WriteFile("dst/старое.dll", "ушло");
            new FileInfo(target).IsReadOnly = true;
            var del = dir.WriteFile("deletelist.txt", "старое.dll\r\n");

            await Apply(dir, r => r.Del = del);

            Assert.False(File.Exists(target));
        }

        /// <summary>
        /// Путь из списка, уводящий за пределы папки установки, отменяет обновление
        /// ЦЕЛИКОМ и до первой операции. Апдейтер работает с правами пользователя, а
        /// после UAC — и выше: строка «../../» в deletelist сносит чужой файл, а в
        /// filelist кладёт исполняемый файл в автозагрузку.
        /// </summary>
        [Fact]
        public async Task ВыходЗаПределыУстановкиОтменяетОбновлениеЦеликом() {
            using var dir = new TempDir();
            var outside = dir.WriteFile("чужое.txt", "не трогать");
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");
            var del = dir.WriteFile("deletelist.txt", "../чужое.txt\r\n");

            var (exit, ctx, _) = await Apply(dir, r => {
                r.Files = files;
                r.Del = del;
                r.Version = "1.2.3";
            });

            Assert.Equal(3, exit);
            Assert.Equal("fatal", ctx.Outcome);
            Assert.True(File.Exists(outside));
            Assert.Equal("старая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
            Assert.False(File.Exists(dir.PathTo("dst/launcher.version")));
        }

        /// <summary>
        /// Пустые каталоги из списка создаются: лаунчер рассчитывает на их наличие,
        /// а в пакете их нет — каталог без файлов в архив не попадает.
        /// </summary>
        [Fact]
        public async Task ПустыеКаталогиСоздаются() {
            using var dir = new TempDir();
            var dirs = dir.WriteFile("emptydirs.txt", "data/cache\r\nlogs\r\n\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Dirs = dirs);

            Assert.Equal(0, exit);
            Assert.True(Directory.Exists(dir.PathTo("dst/data/cache")));
            Assert.True(Directory.Exists(dir.PathTo("dst/logs")));
        }

        /// <summary>
        /// Обёртка архива снимается с путей. Не снимешь — вся новая сборка ляжет в
        /// ПОДПАПКУ установки, запускаемый лаунчер останется старым, и обновление
        /// будет предлагаться при каждом старте, навсегда.
        /// </summary>
        [Fact]
        public async Task ПрефиксАрхиваСнимаетсяСПутей() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub-1.2.3/ChillHub.dll", "новая сборка");
            dir.WriteFile("src/ChillHub-1.2.3/sub/lib.dll", "новая библиотека");
            var files = dir.WriteFile("filelist.txt", "ChillHub-1.2.3/ChillHub.dll\r\nChillHub-1.2.3/sub/lib.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => {
                r.Files = files;
                r.Strip = "ChillHub-1.2.3";
            });

            Assert.Equal(0, exit);
            Assert.Equal("новая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
            Assert.Equal("новая библиотека", File.ReadAllText(dir.PathTo("dst/sub/lib.dll")));
            Assert.False(Directory.Exists(dir.PathTo("dst/ChillHub-1.2.3")));
        }

        /// <summary>
        /// Обёртка определяется сама, когда её не передали: лаунчер вычисляет префикс
        /// не во всех сценариях, а промах здесь стоит вечного предложения обновиться.
        /// </summary>
        [Fact]
        public async Task ПрефиксАрхиваОпределяетсяСам() {
            using var dir = new TempDir();
            dir.WriteFile("src/pkg/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "pkg/ChillHub.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Files = files);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(dir.PathTo("dst/ChillHub.dll")));
        }

        /// <summary>
        /// Явный запрет автоопределения обязан соблюдаться: лаунчер считает префикс
        /// по манифесту сам, и «догадка» апдейтера поверх его решения раскладывает
        /// файлы на уровень мимо папки установки.
        /// </summary>
        [Fact]
        public async Task ЗапретАвтоопределенияПрефиксаСоблюдается() {
            using var dir = new TempDir();
            dir.WriteFile("src/pkg/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "pkg/ChillHub.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => {
                r.Files = files;
                r.AutoStrip = false;
            });

            Assert.Equal(0, exit);
            Assert.True(File.Exists(dir.PathTo("dst/pkg/ChillHub.dll")));
            Assert.False(File.Exists(dir.PathTo("dst/ChillHub.dll")));
        }

        /// <summary>
        /// Хвосты прерванного прогона убираются ДО начала работы: иначе чужой .chbak
        /// смешается с бэкапами текущей транзакции, и откат «восстановит» файл
        /// из позапрошлой версии.
        /// </summary>
        [Fact]
        public async Task ХвостыПрошлогоПрогонаУбираютсяДоНачала() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll" + AtomicFile.BackupSuffix, "бэкап позапрошлого прогона");
            dir.WriteFile("dst/sub/lib.dll" + AtomicFile.TempSuffix, "недописанный файл");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Files = files);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(dir.PathTo("dst/ChillHub.dll" + AtomicFile.BackupSuffix)));
            Assert.False(File.Exists(dir.PathTo("dst/sub/lib.dll" + AtomicFile.TempSuffix)));
        }

        /// <summary>
        /// Служебные файлы, которые прошлые версии апдейтера копировали прямо в папку
        /// установки, вычищаются. Для сверки с сервером это лишние файлы, и лаунчер
        /// считает установку испорченной, пока они там лежат.
        /// </summary>
        [Fact]
        public async Task СтарыеАртефактыАпдейтераВычищаютсяИзУстановки() {
            using var dir = new TempDir();
            dir.WriteFile("dst/filelist.txt", "мусор прошлой версии");
            dir.WriteFile("dst/apply-update.log", "мусор прошлой версии");
            var readOnly = dir.WriteFile("dst/updater/helper.dll", "мусор прошлой версии");
            new FileInfo(readOnly).IsReadOnly = true;

            var (exit, _, _) = await Apply(dir);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(dir.PathTo("dst/filelist.txt")));
            Assert.False(File.Exists(dir.PathTo("dst/apply-update.log")));
            Assert.False(Directory.Exists(dir.PathTo("dst/updater")));
        }

        /// <summary>
        /// Пропуск «по совпадению размера» чинится сверкой: файл того же размера с
        /// другим содержимым — обычное дело для сборок, и без починки установка
        /// осталась бы со старым содержимым под новым номером версии.
        /// </summary>
        [Fact]
        public async Task ПропущенныйПоРазмеруФайлПочиняетсяСверкой() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "AAA");
            dir.WriteFile("src/ChillHub.dll", "BBB");

            var (exit, ctx, _) = await Apply(dir, r => r.Version = "1.2.3");

            Assert.Equal(0, exit);
            Assert.Equal("ok", ctx.Outcome);
            Assert.Equal("BBB", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
        }

        /// <summary>
        /// Непрочитанный источник — это ошибка, а не «в порядке». Типичная причина —
        /// антивирус, держащий только что распакованный файл: сравнить содержимое не с
        /// чем, и объявить установку исправной значит записать маркер версии поверх
        /// старых файлов.
        /// </summary>
        [Fact]
        public async Task НепрочитанныйИсточникСрываетОбновлениеИОткатываетЕго() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "AAA");
            var locked = dir.WriteFile("src/ChillHub.dll", "BBB");
            dir.WriteFile("dst/lib.dll", "старая библиотека");
            dir.WriteFile("src/lib.dll", "новая библиотека");

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
                var (exit, ctx, _) = await Apply(dir, r => r.Version = "1.2.3");

                Assert.Equal(2, exit);
                Assert.Equal("copy-errors", ctx.Outcome);
                Assert.False(File.Exists(dir.PathTo("dst/launcher.version")));
                Assert.Equal("старая библиотека", File.ReadAllText(dir.PathTo("dst/lib.dll")));
            }
        }

        /// <summary>
        /// Занятый файл в папке установки даёт честный код ошибки и объяснение,
        /// а не молчаливый успех: если бы прогон досчитался до маркера версии,
        /// установка со старым залоченным файлом считалась бы новой навсегда.
        /// </summary>
        [Fact]
        public async Task ЗанятыйФайлВУстановкеДаётКодОшибкиИОбъяснение() {
            using var dir = new TempDir();
            var busy = dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None)) {
                var (exit, ctx, _) = await Apply(dir, r => {
                    r.Files = files;
                    r.Version = "1.2.3";
                });

                Assert.Equal(2, exit);
                Assert.Equal("copy-errors", ctx.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(ctx.Message));
                Assert.Contains("1.2.3", ctx.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(dir.PathTo("dst/launcher.version")));
            }

            Assert.Equal("старая сборка", File.ReadAllText(busy));
        }

        /// <summary>
        /// Без номера версии маркер не трогается вовсе: перезаписать его пустым
        /// значением значит навсегда лишить установку возможности обновиться.
        /// </summary>
        [Fact]
        public async Task БезНомераВерсииМаркерНеТрогается() {
            using var dir = new TempDir();
            dir.WriteFile("dst/launcher.version", "1.0.0");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Files = files);

            Assert.Equal(0, exit);
            Assert.Equal("1.0.0", File.ReadAllText(dir.PathTo("dst/launcher.version")));
        }

        /// <summary>
        /// Полный пакет (без filelist) переносится целиком: так обновляются
        /// runtimes/ и prereqs/, которых нет ни в одном диффе.
        /// </summary>
        [Fact]
        public async Task ПолныйПакетБезСпискаПереноситсяЦеликом() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            dir.WriteFile("src/runtimes/win-x64/native.dll", "нативная библиотека");

            var (exit, _, _) = await Apply(dir, r => r.Version = "1.2.3");

            Assert.Equal(0, exit);
            Assert.Equal("новая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
            Assert.Equal("нативная библиотека", File.ReadAllText(dir.PathTo("dst/runtimes/win-x64/native.dll")));
        }

        /// <summary>
        /// Файл из пакета, не упомянутый в списке копирования, остаётся в журнале
        /// отдельной строкой: расхождение лаунчера и апдейтера иначе не видно ничем,
        /// а означает оно, что часть новой сборки на диск не доехала.
        /// </summary>
        [Fact]
        public async Task ЛишнийФайлПакетаПопадаетВЖурнал() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            dir.WriteFile("src/забытый.dll", "в список не попал");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var (exit, _, log) = await Apply(dir, r => {
                r.Files = files;
                r.AutoStrip = false;
            });

            Assert.Equal(0, exit);
            Assert.Contains("забытый.dll", log(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Каталог установки создаётся, если его нет: обновление после переезда папки
        /// не должно падать на пустом месте.
        /// </summary>
        [Fact]
        public async Task ОтсутствующийКаталогУстановкиСоздаётся() {
            using var dir = new TempDir();
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var (exit, _, _) = await Apply(dir, r => r.Files = files);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(dir.PathTo("dst/ChillHub.dll")));
        }

        /// <summary>
        /// Недоступная для записи папка установки — отказ ДО первой операции.
        /// Раньше отказ в доступе ретраился по десять раз с бэкоффом на КАЖДЫЙ файл:
        /// на сотне файлов это десятки минут тишины, после которых обновление
        /// всё равно не применялось.
        /// </summary>
        [Fact]
        public async Task НедоступнаяПапкаУстановкиОтвергаетсяСразу() {
            using var dir = new TempDir();

            // Файл на месте каталога установки: запись невозможна в принципе.
            dir.WriteFile("dst", "это файл, а не папка");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            var (exit, ctx, _) = await Apply(dir, r => {
                r.Files = files;
                r.Version = "1.2.3";
            });

            Assert.Equal(3, exit);
            Assert.Equal("access-denied", ctx.Outcome);
        }

        /// <summary>
        /// Отказ в доступе посреди копирования прерывает ВЕСЬ проход и откатывает
        /// сделанное: права не появятся от повторов, а продолжать значит оставить
        /// установку смесью версий.
        /// </summary>
        [Fact]
        public async Task ОтказВДоступеПрерываетПроходИОткатываетСделанное() {
            using var dir = new TempDir();
            dir.WriteFile("dst/a.dll", "старая a");
            dir.WriteFile("dst/b.dll", "старая b");
            dir.WriteFile("src/a.dll", "новая a");
            dir.WriteFile("src/b.dll", "новая b");
            var files = dir.WriteFile("filelist.txt", "a.dll\r\nb.dll\r\n");

            // Каталог на месте временного файла: запись по этому пути не пройдёт никогда.
            Directory.CreateDirectory(dir.PathTo("dst/b.dll" + AtomicFile.TempSuffix));

            var (exit, ctx, _) = await Apply(dir, r => {
                r.Files = files;
                r.Version = "1.2.3";
            });

            Assert.Equal(3, exit);
            Assert.Equal("access-denied", ctx.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(ctx.Message));
            Assert.Equal("старая a", File.ReadAllText(dir.PathTo("dst/a.dll")));
            Assert.Equal("старая b", File.ReadAllText(dir.PathTo("dst/b.dll")));
            Assert.False(File.Exists(dir.PathTo("dst/launcher.version")));
        }

        /// <summary>
        /// Незаписанный маркер версии — не порча данных, а расхождение: файлы новые,
        /// а лаунчер считает установку старой. Транзакция подтверждается (откатывать
        /// исправные файлы незачем), но исход обязан отличаться от «ok», иначе
        /// повторное обновление никто не предложит.
        /// </summary>
        [Fact]
        public async Task НезаписанныйМаркерНеВыдаётсяЗаУспех() {
            using var dir = new TempDir();
            dir.WriteFile("dst/ChillHub.dll", "старая сборка");
            dir.WriteFile("src/ChillHub.dll", "новая сборка");
            var files = dir.WriteFile("filelist.txt", "ChillHub.dll\r\n");

            // Каталог на месте временного файла маркера: атомарная запись не пройдёт.
            Directory.CreateDirectory(dir.PathTo("dst/launcher.version" + AtomicFile.TempSuffix));

            var (exit, ctx, _) = await Apply(dir, r => {
                r.Files = files;
                r.Version = "1.2.3";
            });

            Assert.Equal(2, exit);
            Assert.Equal("marker-failed", ctx.Outcome);
            Assert.Equal("новая сборка", File.ReadAllText(dir.PathTo("dst/ChillHub.dll")));
            Assert.False(File.Exists(dir.PathTo("dst/launcher.version")));
        }

        // Прогоняет файловую часть обновления на временных каталогах src/ и dst/.
        // Возвращает код возврата, состояние прогона и способ прочитать журнал.
        private static async Task<(int Exit, global::Program.RunContext Ctx, Func<string> Log)> Apply(
            TempDir dir,
            Action<global::Program.ApplyRequest>? tune = null) {
            var log = new UpdateLog();
            var logPath = dir.PathTo("update.log");
            log.Open(logPath);

            Directory.CreateDirectory(dir.PathTo("src"));
            var req = new global::Program.ApplyRequest {
                Src = dir.PathTo("src"),
                Dst = dir.PathTo("dst"),
            };
            tune?.Invoke(req);

            var ctx = new global::Program.RunContext();
            var exit = await global::Program.ApplyAsync(req, log, ctx);
            return (exit, ctx, () => File.ReadAllText(logPath));
        }
    }
}
