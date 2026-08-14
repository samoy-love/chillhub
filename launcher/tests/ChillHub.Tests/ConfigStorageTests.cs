// <copyright file="ConfigStorageTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Конфиг на диске: чтение, запись, восстановление после порчи и перенос со старого места.
    /// <para>
    /// config.json — единственное место, где живёт выбор пользователя: путь к играм (а значит
    /// и десятки гигабайт уже скачанных сборок), адрес сервера, тумблеры приватности. Потеря
    /// этого файла выглядит как «игры не установлены» и стоит человеку повторной закачки,
    /// поэтому здесь проверяется не «прочиталось ли», а КОГДА лаунчер имеет право затереть
    /// сохранённые настройки, и когда не имеет.
    /// </para>
    /// <para>
    /// Каталоги конфига подменяются на временные: тест, пишущий в настоящий
    /// %APPDATA%\ChillHub\config.json, затёр бы рабочие настройки разработчика.
    /// </para>
    /// </summary>
    [Collection(ConfigStorageCollection.Name)]
    public class ConfigStorageTests {
        /// <summary>
        /// Сохранённые настройки поднимаются с диска в том же виде. Базовый круг: если
        /// он сломан, всё остальное — выбор диска, адрес сервера, отказ от телеметрии —
        /// сбрасывается при каждом перезапуске лаунчера.
        /// </summary>
        [Fact]
        public void СохранённыйКонфигЧитаетсяОбратно() {
            using var cfgDir = new ConfigDirsScope();

            var saved = new AppConfig {
                GamesPath = @"E:\Мои игры",
                DownloadThreads = 12,
                LastGameId = "lethal-company",
                AutoErrorReports = false,
                SendUsageMetrics = false,

            };
            Assert.True(ConfigService.Save(saved));
            Assert.True(File.Exists(cfgDir.ConfigPath), "config.json не появился на диске");

            var back = ConfigService.Load();

            Assert.Equal(@"E:\Мои игры", back.GamesPath);
            Assert.Equal(12, back.DownloadThreads);
            Assert.Equal("lethal-company", back.LastGameId);
            Assert.False(back.AutoErrorReports);
            Assert.False(back.SendUsageMetrics);
        }

        /// <summary>
        /// Неудачная запись не должна менять то, что лаунчер считает текущими настройками.
        /// Иначе в памяти живут значения, которых на диске нет: пользователь видит свой
        /// выбор до перезапуска, а после перезапуска тот молча откатывается.
        /// </summary>
        [Fact]
        public void КешОбновляетсяТолькоПослеУдачнойЗаписи() {
            using var cfgDir = new ConfigDirsScope();
            Assert.True(ConfigService.Save(new AppConfig { GamesPath = @"E:\Сохранилось" }));

            cfgDir.BlockAppDir();

            Assert.False(ConfigService.Save(new AppConfig { GamesPath = @"E:\Не сохранилось" }));
            Assert.Equal(@"E:\Сохранилось", ConfigService.Current.GamesPath);
        }

        /// <summary>
        /// Провал записи виден вызывающему вместе с причиной. Раньше страница настроек
        /// рапортовала об успехе даже когда файл записать не удалось, и человек узнавал
        /// о потере настроек только при следующем запуске.
        /// </summary>
        [Fact]
        public void НеудачнаяЗаписьСообщаетОбОшибке() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.BlockAppDir();

            Assert.False(ConfigService.TrySave(new AppConfig(), out var error));
            Assert.False(string.IsNullOrWhiteSpace(error), "текст ошибки пуст — показать пользователю нечего");
        }

        /// <summary>
        /// Повреждённое содержимое чинить нечем, но и выбрасывать его нельзя: копия
        /// откладывается рядом, чтобы человек мог достать оттуда хотя бы путь к играм
        /// и не качать сборки заново.
        /// </summary>
        [Fact]
        public void БитыйКонфигОткладываетсяВКопиюИСменяетсяДефолтами() {
            using var cfgDir = new ConfigDirsScope();
            const string garbage = "{ это не json";
            cfgDir.WriteConfig(garbage);

            var cfg = ConfigService.Load();

            Assert.Equal(AppConfig.DefaultGamesPath(), cfg.GamesPath);
            Assert.True(File.Exists(cfgDir.CorruptedPath), "копия повреждённого конфига не сохранена");
            Assert.Equal(garbage, File.ReadAllText(cfgDir.CorruptedPath));
            Assert.Equal(AppConfig.DefaultGamesPath(), cfgDir.ReadConfigFromDisk().GamesPath);
        }

        /// <summary>
        /// Ключевая регрессия: занятый файл — это НЕ повреждённый файл.
        /// Пока обе беды ловил один пустой catch, конфиг тут же перезаписывался
        /// умолчаниями, и достаточно было антивирусу подержать config.json открытым,
        /// чтобы пользователь потерял путь к играм и увидел «игры не установлены».
        /// </summary>
        [Fact]
        public void ЗанятыйКонфигНеПерезаписываетсяДефолтами() {
            using var cfgDir = new ConfigDirsScope();
            Assert.True(ConfigService.Save(new AppConfig { GamesPath = @"E:\Игры пользователя", DownloadThreads = 12 }));
            var onDisk = File.ReadAllText(cfgDir.ConfigPath);

            using (new FileStream(cfgDir.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None)) {
                var cfg = ConfigService.Load();

                Assert.Equal(@"E:\Игры пользователя", cfg.GamesPath);
                Assert.Equal(12, cfg.DownloadThreads);
            }

            Assert.Equal(onDisk, File.ReadAllText(cfgDir.ConfigPath));
        }

        /// <summary>
        /// Тот же случай в самом опасном виде: файл занят, а в памяти ещё ничего нет —
        /// именно так выглядит запуск лаунчера при работающем антивирусе. В памяти
        /// оказываются умолчания, но на диск они не уезжают: следующая попытка прочитает
        /// настоящие настройки.
        /// </summary>
        [Fact]
        public void ЗанятыйКонфигБезКешаОстаётсяНаДискеНетронутым() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.WriteConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"E:\Игры пользователя" }));

            using (new FileStream(cfgDir.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None)) {
                Assert.Equal(AppConfig.DefaultGamesPath(), ConfigService.Load().GamesPath);
            }

            Assert.Equal(@"E:\Игры пользователя", cfgDir.ReadConfigFromDisk().GamesPath);
        }

        /// <summary>
        /// Первый запуск: конфига нет вовсе. Умолчания не только возвращаются, но и
        /// ложатся на диск — иначе следующий запуск снова считает лаунчер ненастроенным.
        /// </summary>
        [Fact]
        public void ОтсутствующийКонфигСоздаётсяСДефолтами() {
            using var cfgDir = new ConfigDirsScope();
            Assert.False(File.Exists(cfgDir.ConfigPath));

            var cfg = ConfigService.Load();

            Assert.True(File.Exists(cfgDir.ConfigPath), "умолчания не сохранены на диск");
            Assert.Equal(AppConfig.DefaultGamesPath(), cfg.GamesPath);
            Assert.Equal(AppConfig.DefaultApiBaseUrl, cfgDir.ReadConfigFromDisk().ApiBaseUrl);
        }

        /// <summary>
        /// Настройки из %LOCALAPPDATA% переезжают в %APPDATA%. Пока конфиг лежал в каталоге
        /// установки, он попадал в манифест обновления, и версии 1.0.2/1.0.3 уходили в
        /// вечный цикл самообновления. Обновившийся пользователь не должен при этом
        /// потерять выбранный диск.
        /// </summary>
        [Fact]
        public void КонфигПереноситсяИзКаталогаУстановки() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.WriteLegacyConfig(JsonSerializer.Serialize(
                new AppConfig { GamesPath = @"E:\Старые игры", DownloadThreads = 12 }));

            var cfg = ConfigService.Load();

            Assert.Equal(@"E:\Старые игры", cfg.GamesPath);
            Assert.Equal(12, cfg.DownloadThreads);
            Assert.Equal(@"E:\Старые игры", cfgDir.ReadConfigFromDisk().GamesPath);
        }

        /// <summary>
        /// Старый файл после переноса остаётся на месте: его ещё читает не обновившаяся
        /// версия лаунчера, и апдейтер держит config.json в списке --preserve. Удаление
        /// сделало бы откат на предыдущую сборку работой с чистого листа.
        /// </summary>
        [Fact]
        public void СтарыйКонфигПослеПереносаНеУдаляется() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.WriteLegacyConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"E:\Старые игры" }));

            ConfigService.Load();

            Assert.True(File.Exists(cfgDir.LegacyConfigPath), "старое расположение конфига вычищено — откат сломан");
        }

        /// <summary>
        /// Перенос одноразовый. Если бы он повторялся при каждом чтении, любое изменение
        /// настроек откатывалось бы к тому, что человек выбирал до обновления лаунчера.
        /// </summary>
        [Fact]
        public void ПовторныйПереносНеВозвращаетСтарыеНастройки() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.WriteLegacyConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"E:\Старые игры" }));
            ConfigService.Load();

            Assert.True(ConfigService.Save(new AppConfig { GamesPath = @"E:\Новые игры" }));

            Assert.Equal(@"E:\Новые игры", ConfigService.Load().GamesPath);
            Assert.Equal(@"E:\Новые игры", cfgDir.ReadConfigFromDisk().GamesPath);
        }

        /// <summary>
        /// Уже существующий конфиг перенос не трогает: на новом месте лежит то, что человек
        /// выбрал ПОСЛЕ обновления, и затирать это содержимым из каталога установки нельзя.
        /// </summary>
        [Fact]
        public void ПереносНеТрогаетУжеСуществующийКонфиг() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.WriteConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"E:\Текущие игры" }));
            cfgDir.WriteLegacyConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"E:\Старые игры" }));

            Assert.Equal(@"E:\Текущие игры", ConfigService.Load().GamesPath);
        }

        /// <summary>
        /// Мусор из старого места не переносится: перенести нечего, а сорванный разбор
        /// не должен помешать запуску — лаунчер просто начинает с умолчаний.
        /// </summary>
        [Fact]
        public void МусорИзКаталогаУстановкиНеПереносится() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.WriteLegacyConfig("не json вовсе");

            var cfg = ConfigService.Load();

            Assert.Equal(AppConfig.DefaultGamesPath(), cfg.GamesPath);
            Assert.Equal(AppConfig.DefaultApiBaseUrl, cfgDir.ReadConfigFromDisk().ApiBaseUrl);
        }

        /// <summary>
        /// Сорвавшийся перенос обязан оставить след в журнале.
        /// <para>
        /// Это единственный случай, когда настройки пользователя теряются целиком: старый
        /// config.json остался на прежнем месте, новый не написан, лаунчер разворачивает
        /// умолчания — человек видит «игры не установлены» и заново качает десятки гигабайт.
        /// Пока перенос гасил свои ошибки сам, в client.log об этом не было ни строки,
        /// и разбирать жалобу было не по чему.
        /// </para>
        /// <para>
        /// Запись делается невозможной так же, как при отсутствии прав: на месте config.json
        /// оказывается каталог.
        /// </para>
        /// </summary>
        [Fact]
        public void СорвавшийсяПереносКонфигаПопадаетВЖурнал() {
            using var cfgDir = new ConfigDirsScope();
            using var logs = new TempDir();
            cfgDir.WriteLegacyConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"E:\Старые игры" }));
            Directory.CreateDirectory(cfgDir.ConfigPath);

            using (ChillHub.Core.Logging.Logger.OverrideForTests(logs.Root)) {
                // Запуск ломаться не должен: перенос — не условие работоспособности.
                Assert.Equal(AppConfig.DefaultGamesPath(), ConfigService.Load().GamesPath);
            }

            Assert.Contains(
                "миграция не выполнена",
                File.ReadAllText(Path.Combine(logs.Root, "client.log")),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Правки руками нормализуются при чтении: config.json лежит в %APPDATA% и правится
        /// чем угодно, работающим от имени пользователя, а по ApiBaseUrl лаунчер берёт
        /// файлы самообновления.
        /// </summary>
        [Fact]
        public void НормализацияПрименяетсяПриЧтении() {
            using var cfgDir = new ConfigDirsScope();
            cfgDir.WriteConfig("""
                { "GamesPath": "E:\\Игры", "DownloadThreads": 99, "ApiBaseUrl": "http://attacker.invalid" }
                """);

            var cfg = ConfigService.Load();

            Assert.Equal(16, cfg.DownloadThreads);
            Assert.Equal(AppConfig.DefaultApiBaseUrl, cfg.ApiBaseUrl);
            Assert.Equal(@"E:\Игры", cfg.GamesPath);
        }

        /// <summary>
        /// И при записи тоже: негодное значение не должно долежать на диске до следующего
        /// запуска, когда его прочитает уже другая сборка лаунчера.
        /// </summary>
        [Fact]
        public void НормализацияПрименяетсяПриЗаписи() {
            using var cfgDir = new ConfigDirsScope();

            Assert.True(ConfigService.Save(new AppConfig {
                DownloadThreads = 99,
                ApiBaseUrl = "http://attacker.invalid",
                GamesPath = "   ",
            }));

            var disk = cfgDir.ReadConfigFromDisk();
            Assert.Equal(16, disk.DownloadThreads);
            Assert.Equal(AppConfig.DefaultApiBaseUrl, disk.ApiBaseUrl);
            Assert.Equal(AppConfig.DefaultGamesPath(), disk.GamesPath);
        }

        /// <summary>
        /// Прочитанное держится в памяти и на диск за ним не ходят: конфиг спрашивают
        /// и фоновые задачи, и UI — чтение файла на каждое обращение было бы заметно.
        /// Сброс кеша заставляет перечитать файл.
        /// </summary>
        [Fact]
        public void ТекущиеНастройкиБерутсяИзПамятиПокаКешНеСброшен() {
            using var cfgDir = new ConfigDirsScope();
            Assert.True(ConfigService.Save(new AppConfig { GamesPath = @"E:\Первое" }));

            cfgDir.WriteConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"E:\Второе" }));
            Assert.Equal(@"E:\Первое", ConfigService.Current.GamesPath);

            ConfigService.InvalidateCache();
            Assert.Equal(@"E:\Второе", ConfigService.Current.GamesPath);
        }

        /// <summary>
        /// Сам шов не должен переживать тест: после него лаунчер обязан снова смотреть
        /// в настоящий %APPDATA%, иначе следующие тесты (и разработчик) получат конфиг
        /// из чужого временного каталога.
        /// </summary>
        [Fact]
        public void ПодменаКаталоговЖивётТолькоВнутриТеста() {
            var real = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "config.json");

            using (var cfgDir = new ConfigDirsScope()) {
                Assert.Equal(cfgDir.ConfigPath, ConfigService.ConfigFilePath);
            }

            Assert.Equal(real, ConfigService.ConfigFilePath);
        }
    }

    /// <summary>
    /// Тесты, подменяющие каталоги конфига, идут в одной коллекции: пути и кеш —
    /// состояние процесса, а классы xUnit по умолчанию выполняются параллельно.
    /// </summary>
    [CollectionDefinition(Name)]
    public class ConfigStorageCollection {
        internal const string Name = "config-storage";
    }

    /// <summary>
    /// Уводит конфиг во временные каталоги (роуминг и каталог установки) на время теста
    /// и возвращает на место в Dispose.
    /// </summary>
    internal sealed class ConfigDirsScope : IDisposable {
        private readonly TempDir dir = new TempDir();
        private readonly IDisposable seam;
        private readonly string defaultGamesParent;
        private readonly bool defaultGamesParentExisted;

        internal ConfigDirsScope() {
            this.AppDir = this.dir.PathTo("roaming");
            this.LegacyAppDir = this.dir.PathTo("install");
            Directory.CreateDirectory(this.AppDir);
            Directory.CreateDirectory(this.LegacyAppDir);

            // Разворачивание умолчаний создаёт каталог игр по умолчанию (D:\Games или
            // C:\Games). Если его на машине не было, тест обязан убрать за собой.
            this.defaultGamesParent = Path.GetDirectoryName(AppConfig.DefaultGamesPath())!;
            this.defaultGamesParentExisted = Directory.Exists(this.defaultGamesParent);

            this.seam = ConfigService.OverrideForTests(this.AppDir, this.LegacyAppDir);
        }

        /// <summary>Каталог, играющий роль %APPDATA%\ChillHub.</summary>
        internal string AppDir { get; }

        /// <summary>Каталог, играющий роль %LOCALAPPDATA%\ChillHub.</summary>
        internal string LegacyAppDir { get; }

        internal string ConfigPath => Path.Combine(this.AppDir, "config.json");

        internal string CorruptedPath => Path.Combine(this.AppDir, "config.corrupted.json");

        internal string LegacyConfigPath => Path.Combine(this.LegacyAppDir, "config.json");

        internal void WriteConfig(string content)
            => File.WriteAllText(this.ConfigPath, content, new UTF8Encoding(false));

        internal void WriteLegacyConfig(string content)
            => File.WriteAllText(this.LegacyConfigPath, content, new UTF8Encoding(false));

        /// <summary>Читает то, что реально лежит на диске, минуя кеш конфига.</summary>
        internal AppConfig ReadConfigFromDisk()
            => JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(this.ConfigPath))!;

        /// <summary>
        /// Делает запись невозможной: на месте каталога конфига оказывается файл,
        /// поэтому создание каталога и запись падают — так же, как при отсутствии прав.
        /// </summary>
        internal void BlockAppDir() {
            Directory.Delete(this.AppDir, recursive: true);
            File.WriteAllText(this.AppDir, "здесь файл, а не каталог");
        }

        public void Dispose() {
            this.seam.Dispose();
            this.dir.Dispose();

            try {
                if (!this.defaultGamesParentExisted
                    && Directory.Exists(this.defaultGamesParent)
                    && Directory.GetFileSystemEntries(this.defaultGamesParent).Length == 0) {
                    Directory.Delete(this.defaultGamesParent);
                }
            }
            catch {
                // Уборка каталога игр — best effort, валить из-за неё тест не нужно.
            }
        }
    }
}
