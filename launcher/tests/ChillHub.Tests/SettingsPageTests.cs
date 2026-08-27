// <copyright file="SettingsPageTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;

    using ChillHub.Core;
    using ChillHub.Core.Settings;

    using Xunit;

    /// <summary>
    /// Страница настроек: папка для игр, число потоков, тумблеры приватности.
    /// <para>
    /// Здесь живёт единственный выбор пользователя, который лаунчер обязан пережить:
    /// путь к играм (а с ним — уже скачанные гигабайты) и согласие на отправку данных.
    /// Проверяется не «записалось ли поле», а что происходит, когда записать НЕ вышло:
    /// страница раньше рапортовала об успехе, и настройки молча терялись до перезапуска.
    /// </para>
    /// <para>
    /// Ни один тест не открывает окно: показ диалогов уведён за швы
    /// <see cref="SettingsDialogs"/>. Модальное окно в прогоне — это повисший CI.
    /// </para>
    /// </summary>
    [Collection(ConfigStorageCollection.Name)]
    public class SettingsPageTests : IDisposable {
        public void Dispose() => SettingsDialogs.ResetDialogsForTests();

        // ---- Страница применяет настройки сразу ----

        /// <summary>
        /// Открытие страницы не переписывает конфиг. Настройки применяются по каждой правке
        /// (кнопки «Сохранить» больше нет), а ползунок потоков стреляет ValueChanged уже
        /// внутри InitializeComponent, когда Minimum=2 подтягивает Value с нуля до двух, — без
        /// защиты это записывало «2 потока» поверх настоящих 16 ещё до показа страницы.
        /// </summary>
        [Fact]
        public void ОткрытиеСтраницыНеПереписываетКонфиг() {
            using var cfgDir = new ConfigDirsScope();
            _ = new DialogLog();
            var cfg = ConfigService.Current;
            cfg.DownloadThreads = 16;
            cfg.SpeedLimitMbps = 7;
            Assert.True(ConfigService.TrySave(cfg, out _));
            var before = File.GetLastWriteTimeUtc(cfgDir.ConfigPath);

            UiThread.Run(() => {
                var page = new ChillHub.Pages.SettingsPage();
                typeof(ChillHub.Pages.SettingsPage)
                    .GetMethod("LoadConfigToUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(page, null);
            });

            Assert.Equal(16, cfgDir.ReadConfigFromDisk().DownloadThreads);
            Assert.Equal(7, cfgDir.ReadConfigFromDisk().SpeedLimitMbps);
            Assert.Equal(before, File.GetLastWriteTimeUtc(cfgDir.ConfigPath));
        }

        /// <summary>
        /// Правка контрола уезжает на диск сама: ползунок потоков, тумблер лимита с числом,
        /// число потоков из поля (с обрезкой до диапазона), Enter в поле пути. Именно это и
        /// значит «без кнопки Сохранить».
        /// </summary>
        [Fact]
        public void ПравкаКонтролаСохраняетсяСразу() {
            using var cfgDir = new ConfigDirsScope();
            using var games = new TempDir();
            _ = new DialogLog();
            var cfg = ConfigService.Current;
            cfg.GamesPath = games.Root;
            cfg.DownloadThreads = 16;
            Assert.True(ConfigService.TrySave(cfg, out _));

            UiThread.Run(() => {
                var page = new ChillHub.Pages.SettingsPage();
                Call(page, "LoadConfigToUi");

                Field<System.Windows.Controls.Slider>(page, "ThreadsSlider").Value = 4;
                Assert.Equal(4, cfgDir.ReadConfigFromDisk().DownloadThreads);
                Assert.Equal("4", Field<System.Windows.Controls.TextBox>(page, "ThreadsBox").Text);

                Field<System.Windows.Controls.TextBox>(page, "ThreadsBox").Text = "99";
                Call(page, "ThreadsBox_LostKeyboardFocus", null!, null!);
                Assert.Equal(16, cfgDir.ReadConfigFromDisk().DownloadThreads);

                Field<System.Windows.Controls.CheckBox>(page, "SpeedLimitCheck").IsChecked = true;
                Field<System.Windows.Controls.TextBox>(page, "SpeedLimitBox").Text = "42";
                Call(page, "SpeedLimitBox_LostKeyboardFocus", null!, null!);
                Assert.Equal(10, cfgDir.ReadConfigFromDisk().SpeedLimitMbps);
                Assert.Equal("10 МБ/с", Field<System.Windows.Controls.TextBlock>(page, "SpeedLimitValueText").Text);

                Field<System.Windows.Controls.CheckBox>(page, "SpeedLimitCheck").IsChecked = false;
                Call(page, "SpeedLimitCheck_Click", null!, null!);
                Assert.Equal(0, cfgDir.ReadConfigFromDisk().SpeedLimitMbps);

                Field<System.Windows.Controls.CheckBox>(page, "MinimizeToTrayCheck").IsChecked = false;
                Call(page, "Toggle_Click", null!, null!);
                Assert.False(cfgDir.ReadConfigFromDisk().MinimizeToTray);

                var sub = Path.Combine(games.Root, "sub");
                Field<System.Windows.Controls.TextBox>(page, "GamesPathBox").Text = sub;
                Call(page, "GamesPathBox_LostKeyboardFocus", null!, null!);
                Assert.Equal(sub, cfgDir.ReadConfigFromDisk().GamesPath);

                Assert.Equal("Сохранено", Field<System.Windows.Controls.TextBlock>(page, "SaveStatusText").Text);
            });
        }

        private static void Call(object target, string method, params object[] args)
            => target.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(target, args.Length == 0 ? null : args);

        private static T Field<T>(object target, string name)
            => (T)target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;

        // ---- Папка для игр ----

        /// <summary>
        /// Обычное сохранение: путь уезжает и в память, и на диск. Без записи на диск
        /// выбор жил бы до перезапуска, а потом лаунчер снова искал бы игры не там.
        /// </summary>
        [Fact]
        public void ПапкаДляИгрСохраняетсяНаДиск() {
            using var cfgDir = new ConfigDirsScope();
            using var games = new TempDir();
            var log = new DialogLog();

            Assert.True(SettingsActions.Save(Input(games.Root)));

            Assert.Equal(games.Root, ConfigService.Current.GamesPath);
            Assert.Equal(games.Root, cfgDir.ReadConfigFromDisk().GamesPath);
            Assert.Empty(log.Errors);
        }

        /// <summary>
        /// Пустое поле означает «верните как было по умолчанию», а не «сохраните пустоту»:
        /// пустой путь превратил бы папку игр в рабочий каталог процесса.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ПустоеПолеПутиЗаменяетсяЗначениемПоУмолчанию(string? text) {
            using var cfgDir = new ConfigDirsScope();
            _ = new DialogLog();

            Assert.True(SettingsActions.Save(Input(text)));

            Assert.Equal(AppConfig.DefaultGamesPath(), ConfigService.Current.GamesPath);
        }

        /// <summary>
        /// Задвоенные слеши приходят из склейки путей и из вставки буфера обмена.
        /// В конфиг они попадать не должны: путь потом показывается пользователю и
        /// сравнивается со строкой на диске.
        /// </summary>
        [Fact]
        public void ПутьСДвойнымиСлешамиНормализуетсяПередЗаписью() {
            using var cfgDir = new ConfigDirsScope();
            using var games = new TempDir();
            _ = new DialogLog();
            var doubled = games.Root.Replace(@"\", @"\\", StringComparison.Ordinal);

            Assert.True(SettingsActions.Save(Input(doubled)));

            Assert.Equal(games.Root, ConfigService.Current.GamesPath);
        }

        /// <summary>
        /// Сетевая шара задаётся как \\nas\games, и ведущая пара слешей — часть адреса,
        /// а не задвоение. Съев её, лаунчер искал бы игры в \nas\games на локальном диске.
        /// </summary>
        [Fact]
        public void СетевойПутьНеТеряетВедущиеСлеши() {
            using var cfgDir = new ConfigDirsScope();
            _ = new DialogLog();

            Assert.True(SettingsActions.Save(Input(@"\\nas\games\ChillHub")));

            Assert.Equal(@"\\nas\games\ChillHub", ConfigService.Current.GamesPath);
        }

        /// <summary>
        /// Папка на диске, которого нет, — настройку всё равно сохраняем: диск могут
        /// подключить позже, а отказ заставил бы пользователя выбирать что-то другое
        /// и заново качать игры на диск, который его не устраивает.
        /// </summary>
        [Fact]
        public void ПапкаНаОтсутствующемДискеСохраняетсяВсёРавно() {
            using var cfgDir = new ConfigDirsScope();
            var log = new DialogLog();
            var missingDrive = FirstMissingDrivePath();

            Assert.True(SettingsActions.Save(Input(missingDrive)));

            Assert.Equal(missingDrive, cfgDir.ReadConfigFromDisk().GamesPath);
            Assert.Empty(log.Errors);
        }

        /// <summary>
        /// Недоступная папка (на её месте файл) — тот же случай: путь сохраняется,
        /// а создание каталога остаётся заботой установки.
        /// </summary>
        [Fact]
        public void НедоступнаяПапкаНеОтменяетСохранение() {
            using var cfgDir = new ConfigDirsScope();
            using var games = new TempDir();
            var asFile = games.WriteFile("занято.dat", "не папка");
            var log = new DialogLog();

            Assert.True(SettingsActions.Save(Input(Path.Combine(asFile, "игры"))));

            Assert.Empty(log.Errors);
        }

        /// <summary>
        /// Главное, ради чего сохранение вообще возвращает результат: запись не удалась.
        /// Промолчать нельзя — пользователь уйдёт со страницы уверенный, что настройки
        /// сохранены, и обнаружит пропажу только после перезапуска.
        /// </summary>
        [Fact]
        public void СбойЗаписиНастроекПоказываетсяПользователю() {
            using var cfgDir = new ConfigDirsScope();
            using var games = new TempDir();
            var log = new DialogLog();
            cfgDir.BlockAppDir();

            Assert.False(SettingsActions.Save(Input(games.Root)));

            var shown = Assert.Single(log.Errors);
            Assert.Contains("Не удалось сохранить настройки", shown, StringComparison.Ordinal);
        }

        // ---- Число потоков и адрес сервера ----

        /// <summary>
        /// Ползунок отдаёт дробное значение, а в конфиге живёт целое число потоков.
        /// Зажим в допустимые границы делает ConfigService (см. ConfigClampTests),
        /// но страница обязана хотя бы не потерять выбранное значение по дороге.
        /// </summary>
        [Theory]
        [InlineData(2.0, 2)]
        [InlineData(7.6, 7)]
        [InlineData(16.0, 16)]
        public void ЧислоПотоковСПолзункаДоезжаетДоКонфига(double slider, int expected) {
            using var cfgDir = new ConfigDirsScope();
            _ = new DialogLog();

            Assert.True(SettingsActions.Save(new SettingsInput { GamesPathText = null, DownloadThreads = slider }));

            Assert.Equal(expected, cfgDir.ReadConfigFromDisk().DownloadThreads);
        }

        /// <summary>
        /// Значение за границей ползунка (испорченный конфиг, чужая сборка) не должно
        /// уехать на диск как есть: сотня потоков — это сотня одновременных запросов
        /// к серверу раздачи с одной машины.
        /// </summary>
        [Theory]
        [InlineData(0.0, 2)]
        [InlineData(99.0, 16)]
        public void ЧислоПотоковЗаГраницамиЗажимаетсяПриСохранении(double slider, int expected) {
            using var cfgDir = new ConfigDirsScope();
            _ = new DialogLog();

            Assert.True(SettingsActions.Save(new SettingsInput { GamesPathText = null, DownloadThreads = slider }));

            Assert.Equal(expected, cfgDir.ReadConfigFromDisk().DownloadThreads);
        }

        /// <summary>
        /// Адрес сервера страница не показывает и не трогает, но сохранение проходит через
        /// нормализацию конфига. Неприемлемый адрес (http:// не на петлевой) обязан быть
        /// заменён именно здесь: по нему лаунчер берёт манифест самообновления и кладёт
        /// полученные файлы поверх ChillHub.exe.
        /// </summary>
        [Fact]
        public void СохранениеОтклоняетНебезопасныйАдресСервера() {
            using var cfgDir = new ConfigDirsScope();
            _ = new DialogLog();
            ConfigService.Current.ApiBaseUrl = "http://evil.invalid";

            Assert.True(SettingsActions.Save(Input(null)));

            Assert.Equal(AppConfig.DefaultApiBaseUrl, cfgDir.ReadConfigFromDisk().ApiBaseUrl);
        }

        /// <summary>Локальный сервер разработки остаётся рабочим: без него отладку не провести.</summary>
        [Fact]
        public void ЛокальныйАдресСервераСохранениеПереживает() {
            using var cfgDir = new ConfigDirsScope();
            _ = new DialogLog();
            ConfigService.Current.ApiBaseUrl = "http://localhost:8080";

            Assert.True(SettingsActions.Save(Input(null)));

            Assert.Equal("http://localhost:8080", cfgDir.ReadConfigFromDisk().ApiBaseUrl);
        }

        // ---- Тумблеры приватности ----

        /// <summary>
        /// Отказ от телеметрии обязан доехать до диска: это согласие на отправку данных,
        // ---- Выбор папки диалогом ----

        /// <summary>
        /// Выбранная в диалоге папка попадает в поле в нормальном виде. Задвоенные слеши
        /// из диалога прийти могут, а пользователь потом сравнивает путь глазами.
        /// </summary>
        [Fact]
        public void ВыбраннаяВДиалогеПапкаПопадаетВПолеВНормальномВиде() {
            SettingsDialogs.PickFolder = _ => @"D:\Games\\ChillHub";

            Assert.Equal(@"D:\Games\ChillHub", SettingsActions.ChooseGamesFolder(@"D:\старая"));
        }

        /// <summary>
        /// Пустое поле — диалог открывается с папкой по умолчанию, а не из корня диска:
        /// иначе пользователь каждый раз ищет нужное место с нуля.
        /// </summary>
        [Fact]
        public void ПустоеПолеОткрываетДиалогСПапкойПоУмолчанию() {
            string? asked = null;
            SettingsDialogs.PickFolder = initial => { asked = initial; return null; };

            SettingsActions.ChooseGamesFolder("   ");

            Assert.Equal(AppConfig.DefaultGamesPath(), asked);
        }

        /// <summary>Отказ от выбора оставляет поле как было: null значит «не трогать».</summary>
        [Fact]
        public void ОтказОтВыбораПапкиОставляетПолеБезИзменений() {
            SettingsDialogs.PickFolder = _ => null;

            Assert.Null(SettingsActions.ChooseGamesFolder(@"D:\старая"));
        }

        /// <summary>
        /// Диалог может не открыться (нет прав, сбой оболочки). Путь всегда можно ввести
        /// руками, поэтому сбой диалога не должен ни ронять страницу, ни затирать поле.
        /// </summary>
        [Fact]
        public void СбойДиалогаВыбораПапкиНеЗатираетПоле() {
            SettingsDialogs.PickFolder = _ => throw new InvalidOperationException("диалог не поднялся");

            Assert.Null(SettingsActions.ChooseGamesFolder(@"D:\старая"));
        }

        // ---- Наполнение страницы ----

        /// <summary>Открытая страница показывает то, что реально лежит в конфиге.</summary>
        [Fact]
        public void СтраницаПоказываетСохранённыеНастройки() {
            var view = SettingsView.Build(new AppConfig {
                GamesPath = @"E:\Мои игры",
                DownloadThreads = 12,
            });

            Assert.Equal(@"E:\Мои игры", view.GamesPath);
            Assert.Equal(12, view.DownloadThreads);
            Assert.Equal("12", view.DownloadThreadsText);
        }

        /// <summary>
        /// Путь показывается с одинарными слешами, но сетевой префикс уцелевает:
        /// «\nas\games» вместо «\\nas\games» читается как совсем другая папка.
        /// </summary>
        [Theory]
        [InlineData(@"D:\Games\\ChillHub", @"D:\Games\ChillHub")]
        [InlineData(@"\\nas\games", @"\\nas\games")]
        [InlineData(@"\\nas\\games\\ChillHub", @"\\nas\games\ChillHub")]
        public void ПутьПоказываетсяВЧитаемомВиде(string stored, string shown) {
            Assert.Equal(shown, SettingsView.Build(new AppConfig { GamesPath = stored }).GamesPath);
        }

        /// <summary>Конфига может не быть вовсе — страница показывает умолчания, а не падает.</summary>
        [Fact]
        public void БезКонфигаСтраницаПоказываетУмолчания() {
            var view = SettingsView.Build(null);

            Assert.Equal(new AppConfig().DownloadThreads, view.DownloadThreads);
            Assert.False(string.IsNullOrWhiteSpace(view.VersionText));
        }

        // ---- Открытие логов ----

        /// <summary>
        /// Кнопка «Открыть логи» ведёт в каталог логов клиента. Проверяется не сам
        /// проводник, а то, что путь берётся у Logger: логи переехали из %TEMP%,
        /// и вторая копия пути разъехалась бы молча.
        /// </summary>
        [Fact]
        public void ОткрытиеЛоговВедётВКаталогЛоговКлиента() {
            var opened = new List<string>();
            SettingsDialogs.OpenFolder = opened.Add;
            SettingsDialogs.ShowError = (_, _) => Assert.Fail("открытие логов не должно жаловаться");

            SettingsActions.OpenLogsFolder();

            Assert.Equal(ChillHub.Core.Logging.Logger.LogDirectory, Assert.Single(opened));
        }

        /// <summary>
        /// Проводник может не подняться. Молчать нельзя: пользователь нажал кнопку
        /// и обязан понять, что папка не открылась, а не решить, что кнопка сломана.
        /// </summary>
        [Fact]
        public void СбойОткрытияЛоговПоказываетсяПользователю() {
            var log = new DialogLog();
            SettingsDialogs.OpenFolder = _ => throw new InvalidOperationException("проводник не поднялся");

            SettingsActions.OpenLogsFolder();

            Assert.Contains("Не удалось открыть папку с логами", Assert.Single(log.Errors), StringComparison.Ordinal);
        }

        /// <summary>Значения со страницы, где важен только путь.</summary>
        private static SettingsInput Input(string? gamesPath) => new SettingsInput {
            GamesPathText = gamesPath,
            DownloadThreads = 8,
        };

        /// <summary>Путь на первой букве диска, которой на машине нет.</summary>
        private static string FirstMissingDrivePath() {
            for (var letter = 'Z'; letter >= 'E'; letter--) {
                var root = letter + @":\";
                if (!Directory.Exists(root)) {
                    return root + @"Games\ChillHub";
                }
            }

            return @"Z:\Games\ChillHub";
        }

        /// <summary>
        /// Подставной показ окон: запоминает сказанное пользователю и отвечает заранее
        /// заданным. Настоящие окна в прогоне не поднимаются — модальное повесило бы его.
        /// </summary>
        private sealed class DialogLog {
            internal DialogLog() {
                SettingsDialogs.ShowError = (message, caption) => this.Errors.Add(message);
                SettingsDialogs.Confirm = (message, caption) => {
                    this.Asked.Add(message);
                    return this.Answer;
                };
                SettingsDialogs.PickFolder = _ => this.Folder;
                SettingsDialogs.OpenFolder = this.Opened.Add;
            }

            /// <summary>Ответ пользователя на вопрос «продолжить?».</summary>
            internal bool Answer { get; init; }

            /// <summary>Что вернёт диалог выбора папки; null — отказался.</summary>
            internal string? Folder { get; init; }

            /// <summary>Показанные сообщения об ошибке.</summary>
            internal List<string> Errors { get; } = new();

            /// <summary>Заданные вопросы.</summary>
            internal List<string> Asked { get; } = new();

            /// <summary>Открытые папки.</summary>
            internal List<string> Opened { get; } = new();
        }
    }
}
