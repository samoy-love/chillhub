// <copyright file="SelfUpdateDownloadTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Подготовка пакета самообновления: что качаем, что удаляем и что уезжает апдейтеру
    /// в служебных списках.
    /// <para>
    /// Три файла (filelist.txt / emptydirs.txt / deletelist.txt) — ЕДИНСТВЕННОЕ, из чего
    /// апдейтер узнаёт, что делать с папкой установки. Лишняя строка в deletelist.txt
    /// сносит файл пользователя, пропущенная в filelist.txt оставляет установку смесью
    /// версий. Проверять это после выкатки уже поздно.
    /// </para>
    /// </summary>
    public class SelfUpdateDownloadTests {
        /// <summary>В план попадают только реально изменившиеся файлы, а не весь манифест.</summary>
        [Fact]
        public void ВПланПопадаютТолькоИзменившиесяФайлы() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("same.dll", "не менялось");
            stand.Install.WriteFile("old.dll", "старое");
            var manifest = SelfUpdateManifest.Of(
                SelfUpdateManifest.Matching(stand.Install.Root, "same.dll"),
                SelfUpdateManifest.Different("old.dll", size: 42),
                SelfUpdateManifest.Different("new.dll", size: 7));

            var plan = NewDownloader(stand, new FakeSync(), out _)
                .BuildSelfUpdatePlan(manifest, string.Empty, stand.Temp.Root, "https://example.test/content");

            Assert.Equal(new[] { "old.dll", "new.dll" }, plan.Downloads.Select(d => d.RelativePath).ToArray());
            Assert.Equal(2, plan.TotalFilesToDownload);
            Assert.Equal(49, plan.TotalDownloadBytes);
        }

        /// <summary>
        /// Качаем в %TEMP%, а применяем в каталог установки — это может быть другой диск.
        /// Без ApplyRoot проверка свободного места смотрела бы только на TEMP и пропускала
        /// случай «в TEMP место есть, а на системном диске нет».
        /// </summary>
        [Fact]
        public void ПланЗнаетИОбаКаталогаДляПроверкиМеста() {
            using var stand = new SelfUpdateStand();
            var manifest = SelfUpdateManifest.Of(SelfUpdateManifest.Different("new.dll", size: 100));

            var plan = NewDownloader(stand, new FakeSync(), out _)
                .BuildSelfUpdatePlan(manifest, string.Empty, stand.Temp.Root, "https://example.test/content");

            Assert.Equal(stand.Temp.Root, plan.LocalRoot);
            Assert.Equal(stand.Install.Root, plan.ApplyRoot);
            Assert.Equal(100, plan.TotalDownloadBytes);
        }

        /// <summary>Preserve-файлы и мусор апдейтера в загрузку не попадают: апдейтер их не тронет.</summary>
        [Theory]
        [InlineData("config.json")]
        [InlineData("launcher.version")]
        [InlineData("Uninstall.exe")]
        [InlineData("apply-update.log")]
        [InlineData("updater/YourLauncher.Updater.exe")]
        public void PreserveФайлыВЗагрузкуНеПопадают(string rel) {
            using var stand = new SelfUpdateStand();
            var manifest = SelfUpdateManifest.Of(SelfUpdateManifest.Different(rel));

            var plan = NewDownloader(stand, new FakeSync(), out _)
                .BuildSelfUpdatePlan(manifest, string.Empty, stand.Temp.Root, "https://example.test/content");

            Assert.Empty(plan.Downloads);
        }

        /// <summary>
        /// Удаления и пустые каталоги в самом плане обязаны остаться пустыми: их LocalRoot —
        /// временный каталог, и ExecuteAsync применил бы их к нему, а не к папке установки.
        /// </summary>
        [Fact]
        public void ПланНеУдаляетНичегоСам() {
            using var stand = new SelfUpdateStand();
            var manifest = SelfUpdateManifest.Of(SelfUpdateManifest.Different("new.dll"));

            var plan = NewDownloader(stand, new FakeSync(), out _)
                .BuildSelfUpdatePlan(manifest, string.Empty, stand.Temp.Root, "https://example.test/content");

            Assert.Empty(plan.ToDelete);
            Assert.Empty(plan.EmptyDirsToCreate);
        }

        /// <summary>Файл, которого нет в манифесте, уезжает в список удалений.</summary>
        [Fact]
        public void ЛишнийФайлПопадаетВСписокУдалений() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("stale.dll", "от старой версии");
            stand.Install.WriteFile("keep.dll", "нужный");
            var manifest = SelfUpdateManifest.Of(SelfUpdateManifest.Different("keep.dll"));

            var toDelete = NewDownloader(stand, new FakeSync(), out _).BuildDeleteList(manifest, string.Empty);

            Assert.Equal(new[] { "stale.dll" }, toDelete);
        }

        /// <summary>
        /// Preserve-файлы и служебный мусор апдейтера из списка удалений исключены:
        /// иначе обновление стирало бы config.json пользователя.
        /// </summary>
        [Theory]
        [InlineData("config.json")]
        [InlineData("launcher.version")]
        [InlineData("launcher.update-status")]
        [InlineData("Uninstall.exe")]
        [InlineData("filelist.txt")]
        [InlineData("apply-update.log")]
        public void PreserveФайлыНикогдаНеУдаляются(string rel) {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile(rel, "местное состояние");
            var manifest = SelfUpdateManifest.Of(SelfUpdateManifest.Different("keep.dll"));

            Assert.Empty(NewDownloader(stand, new FakeSync(), out _).BuildDeleteList(manifest, string.Empty));
        }

        /// <summary>
        /// Пустой манифест не даёт списка удалений: иначе апдейтер снёс бы всю установку.
        /// Страховка, поставленная ровно на этот случай.
        /// </summary>
        [Fact]
        public void ПустойМанифестНичегоНеУдаляет() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("ChillHub.exe", "лаунчер");
            stand.Install.WriteFile("data/x.pak", "данные");

            Assert.Empty(NewDownloader(stand, new FakeSync(), out _).BuildDeleteList(new Manifest(), string.Empty));
        }

        /// <summary>
        /// A10. При упакованной корневой папке пути манифеста приводятся к путям
        /// относительно установки — иначе в список удалений попадает ВСЯ папка установки.
        /// </summary>
        [Fact]
        public void УпакованнаяКорневаяПапкаНеСноситУстановку() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("ChillHub.exe", "лаунчер");
            stand.Install.WriteFile("stale.dll", "лишнее");
            var manifest = SelfUpdateManifest.Of(
                SelfUpdateManifest.Different("ChillHub/ChillHub.exe"),
                SelfUpdateManifest.Different("ChillHub/data/x.pak"));

            var toDelete = NewDownloader(stand, new FakeSync(), out _).BuildDeleteList(manifest, "ChillHub");

            Assert.Equal(new[] { "stale.dll" }, toDelete);
        }

        /// <summary>Без версии шаг загрузки не делает вообще ничего — окно трогать не за что.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task БезВерсииЗагрузкаНеНачинается(string? version) {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync();

            var result = await NewDownloader(stand, sync, out var ui).DownloadAsync(version);

            Assert.Equal(SelfUpdateDownloadResult.Nothing, result.Result);
            Assert.Empty(ui.States);
            Assert.Empty(sync.ManifestUrls);
        }

        /// <summary>
        /// A6. Версию проверяем ПОВТОРНО перед использованием: между проверкой при старте
        /// и нажатием кнопки поле могло быть переприсвоено, а отсюда оно едет в путь и в URL.
        /// </summary>
        [Fact]
        public async Task НедопустимаяВерсияОтменяетЗагрузку() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync();

            var result = await NewDownloader(stand, sync, out var ui).DownloadAsync(@"..\..\Startup");

            Assert.Equal(SelfUpdateDownloadResult.InvalidVersion, result.Result);
            Assert.Empty(sync.ManifestUrls);
            Assert.Equal("Недопустимый номер версии — обновление отменено.", ui.LastStatus);
            Assert.False(ui.LastButtonEnabled);
        }

        /// <summary>Успешная загрузка оставляет пакет и служебный каталог там, где их ждёт применение.</summary>
        [Fact]
        public async Task УспешнаяЗагрузкаГотовитПакетИСписки() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("stale.dll", "лишнее");
            var sync = new FakeSync {
                OnManifest = _ => new Manifest {
                    Version = "1.2.4",
                    Files = { SelfUpdateManifest.Different("ChillHub.dll", size: 5) },
                    EmptyDirs = { "logs" },
                },
            };

            var result = await NewDownloader(stand, sync, out var ui).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.Ready, result.Result);
            Assert.True(result.Downloaded);
            Assert.Equal(stand.Paths.PayloadDir("1.2.4"), result.TempRoot);
            Assert.Equal(stand.Paths.WorkDir("1.2.4"), result.WorkDir);
            Assert.True(Directory.Exists(result.TempRoot));

            var work = result.WorkDir!;
            Assert.Equal(new[] { "ChillHub.dll" }, File.ReadAllLines(Path.Combine(work, "filelist.txt")));
            Assert.Equal(new[] { "logs" }, File.ReadAllLines(Path.Combine(work, "emptydirs.txt")));
            Assert.Equal(new[] { "stale.dll" }, File.ReadAllLines(Path.Combine(work, "deletelist.txt")));
            Assert.Equal("Обновление загружено. Применяем и перезапускаем...", ui.LastStatus);
        }

        /// <summary>Служебные списки пишутся без BOM: BOM ломает разбор первой строки апдейтером.</summary>
        [Fact]
        public async Task СлужебныеСпискиПишутсяБезBom() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
            };

            var result = await NewDownloader(stand, sync, out _).DownloadAsync("1.2.4");

            var bytes = File.ReadAllBytes(Path.Combine(result.WorkDir!, "filelist.txt"));
            Assert.StartsWith("ChillHub.dll", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        /// <summary>
        /// Полезная нагрузка и служебные файлы лежат в РАЗНЫХ подкаталогах: иначе
        /// «остаточное зеркалирование» апдейтера копирует filelist.txt и журнал
        /// прямо в папку установки.
        /// </summary>
        [Fact]
        public async Task НагрузкаИСлужебныеФайлыРазведеныПоКаталогам() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
            };

            var result = await NewDownloader(stand, sync, out _).DownloadAsync("1.2.4");

            Assert.NotEqual(result.TempRoot, result.WorkDir);
            Assert.False(File.Exists(Path.Combine(result.TempRoot!, "filelist.txt")));
        }

        /// <summary>
        /// Каталог сессии чистится целиком перед загрузкой: нулевые файлы от прошлой
        /// оборванной попытки нельзя выдавать за скачанный пакет.
        /// </summary>
        [Fact]
        public async Task ОстаткиПрошлойПопыткиУдаляются() {
            using var stand = new SelfUpdateStand();
            var stale = Path.Combine(stand.Paths.PayloadDir("1.2.4"), "обрывок.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
            File.WriteAllText(stale, string.Empty);
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
            };

            await NewDownloader(stand, sync, out _).DownloadAsync("1.2.4");

            Assert.False(File.Exists(stale));
        }

        /// <summary>
        /// A12. Копировать и удалять нечего — апдейтер не запускаем вообще: иначе
        /// получаем полный цикл «останов лаунчера → апдейтер → перезапуск» впустую.
        /// Маркер при этом обновляется, иначе окно всплывает при каждом запуске.
        /// </summary>
        [Fact]
        public async Task НечегоДелатьЗначитБезАпдейтера() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("ChillHub.dll", "новое");
            var attempts = stand.Attempts;
            attempts.Register("1.2.4");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Matching(stand.Install.Root, "ChillHub.dll")),
            };

            var result = await NewDownloader(stand, sync, out var ui, attempts).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.AlreadyUpToDate, result.Result);
            Assert.False(result.Downloaded);
            Assert.Null(sync.LastPlan);
            Assert.Equal("1.2.4", SelfUpdateVersions.ReadLocalVersion(stand.Install.Root));
            Assert.Equal(0, attempts.Get("1.2.4"));
            Assert.Equal("Продолжить", ui.LastButtonContent);
        }

        /// <summary>
        /// A8. Маркер записать не удалось — это НЕУДАЧА: счётчик не сбрасываем, попытку
        /// засчитываем и объясняем пользователю причину. Пока ошибка проглатывалась,
        /// окно обновления всплывало при каждом запуске, а защита от петли была обезврежена.
        /// </summary>
        [Fact]
        public async Task НеудачаЗаписиМаркераЗасчитываетПопытку() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("ChillHub.dll", "новое");
            Directory.CreateDirectory(stand.Install.PathTo("launcher.version"));
            var attempts = stand.Attempts;
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Matching(stand.Install.Root, "ChillHub.dll")),
            };

            var result = await NewDownloader(stand, sync, out var ui, attempts).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.AlreadyUpToDate, result.Result);
            Assert.Equal(1, attempts.Get("1.2.4"));
            Assert.Contains("записать отметку о версии не удалось", ui.LastStatus!, StringComparison.Ordinal);
            Assert.True(ui.LastButtonEnabled);
        }

        /// <summary>
        /// Манифест отклонён проверкой структуры — ни одного байта не скачано и не будет:
        /// манифест определяет, что именно ляжет на диск вместо ChillHub.exe.
        /// </summary>
        [Fact]
        public async Task ОтклонённыйМанифестОтменяетЗагрузку() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync { OnManifest = _ => throw new ManifestValidationException("дубликат записи") };

            var result = await NewDownloader(stand, sync, out var ui).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.ManifestRejected, result.Result);
            Assert.False(result.Downloaded);
            Assert.Null(sync.LastPlan);
            Assert.Contains("Обновление отменено: дубликат записи", ui.LastStatus!, StringComparison.Ordinal);
            Assert.False(ui.LastButtonEnabled);
        }

        /// <summary>
        /// Скачанный файл не сошёлся по хешу — пакет не готов, но повторить можно:
        /// кнопка остаётся доступной.
        /// </summary>
        [Fact]
        public async Task РасхождениеХешаОставляетВозможностьПовтора() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
                OnExecute = _ => throw new InvalidDataException("ChillHub.dll не прошёл проверку хеша"),
            };

            var result = await NewDownloader(stand, sync, out var ui).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.IntegrityFailed, result.Result);
            Assert.False(result.Downloaded);
            Assert.Contains("Проверка целостности не пройдена", ui.LastStatus!, StringComparison.Ordinal);
            Assert.True(ui.LastButtonEnabled);
        }

        /// <summary>
        /// Не хватило места на диске — обновление не применяется, а пользователь видит,
        /// откуда качали. Раньше такой отказ выглядел как «ничего не произошло».
        /// </summary>
        [Fact]
        public async Task НехваткаМестаОстанавливаетОбновление() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
                OnExecute = _ => throw new IOException("Недостаточно свободного места на диске C:\\"),
            };

            var result = await NewDownloader(stand, sync, out var ui).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.Failed, result.Result);
            Assert.False(result.Downloaded);
            Assert.Contains("Недостаточно свободного места", ui.LastStatus!, StringComparison.Ordinal);
            Assert.Contains("manifests/launcher/1.2.4.json", ui.LastStatus!, StringComparison.Ordinal);
            Assert.True(ui.LastButtonEnabled);
        }

        /// <summary>Обрыв связи посреди загрузки — тот же честный отказ, а не «пакет готов».</summary>
        [Fact]
        public async Task ОбрывЗагрузкиНеСчитаетсяУспехом() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
                OnExecute = _ => throw new HttpRequestException("соединение разорвано"),
            };

            var result = await NewDownloader(stand, sync, out _).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.Failed, result.Result);
            Assert.False(result.Downloaded);
        }

        /// <summary>
        /// Отмена пользователем на этапе загрузки — не успех: пакета нет, применять нечего.
        /// Иначе апдейтер запустился бы поверх недокачанного каталога.
        /// </summary>
        [Fact]
        public async Task ОтменаЗагрузкиНеДаётПакета() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
                OnExecute = _ => throw new OperationCanceledException(),
            };

            var result = await NewDownloader(stand, sync, out _).DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.Failed, result.Result);
            Assert.False(result.Downloaded);
        }

        /// <summary>
        /// Списки для апдейтера не записались (нет места, каталог занят) — останавливаемся
        /// ЗДЕСЬ, до остановки лаунчера. Без filelist.txt апдейтер переходит в режим
        /// полного зеркалирования, и это выглядело бы как обычное обновление.
        /// </summary>
        [Fact]
        public async Task НезаписанныеСпискиОстанавливаютОбновление() {
            using var stand = new SelfUpdateStand();
            var paths = stand.Paths;

            // Каталог с именем filelist.txt делает запись списка невозможной, а открытый
            // файл внутри сессии не даёт уборке снести его вместе с каталогом сессии.
            var blocker = Path.Combine(paths.WorkDir("1.2.4"), "filelist.txt");
            Directory.CreateDirectory(blocker);
            using var busy = new FileStream(
                Path.Combine(blocker, "busy.bin"), FileMode.Create, FileAccess.Write, FileShare.None);

            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
            };
            var downloader = new SelfUpdateDownloader(
                sync, () => "https://example.test", paths, stand.Attempts, _ => { });

            var result = await downloader.DownloadAsync("1.2.4");

            Assert.Equal(SelfUpdateDownloadResult.Failed, result.Result);
            Assert.False(result.Downloaded);
            Assert.Null(sync.LastPlan);
        }

        /// <summary>
        /// Загрузка сначала переводит полосу в «неизвестно сколько» — до этого окно
        /// показывало нулевой прогресс и выглядело зависшим.
        /// </summary>
        [Fact]
        public async Task ЗагрузкаПереводитПолосуВНеопределённыйРежим() {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
            };

            await NewDownloader(stand, sync, out var ui).DownloadAsync("1.2.4");

            Assert.Contains(ui.States, s => s.Indeterminate == true);
            Assert.Contains(ui.States, s => s.StatusText == "Запрос манифеста лаунчера...");
            Assert.Contains(ui.States, s => s.StatusText == "Подготовка каталога загрузки...");
        }

        /// <summary>Отчёт о прогрессе превращается в проценты и переводит полосу в определённый режим.</summary>
        [Fact]
        public async Task ОтчётОПрогрессеПревращаетсяВПроценты() {
            using var stand = new SelfUpdateStand();
            using var seen = new System.Threading.ManualResetEventSlim(false);
            double? percent = null;
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
                Emit = new SyncProgress { BytesDownloaded = 50, TotalBytes = 200 },
            };
            var downloader = new SelfUpdateDownloader(
                sync,
                () => "https://example.test",
                stand.Paths,
                stand.Attempts,
                s => {
                    if (s.ProgressValue.HasValue && s.Indeterminate == false) {
                        percent = s.ProgressValue;
                        seen.Set();
                    }
                });

            await downloader.DownloadAsync("1.2.4");

            // Progress<T> без контекста синхронизации отдаёт отчёт в пул потоков.
            Assert.True(seen.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(25.0, percent);
        }

        private static SelfUpdateDownloader NewDownloader(
            SelfUpdateStand stand, FakeSync sync, out UiRecorder ui, UpdateAttemptsStore? attempts = null) {
            ui = new UiRecorder();
            return new SelfUpdateDownloader(
                sync, () => "https://example.test", stand.Paths, attempts ?? stand.Attempts, ui.Apply);
        }
    }
}
