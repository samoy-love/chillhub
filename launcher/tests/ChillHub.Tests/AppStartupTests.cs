// <copyright file="AppStartupTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Windows;

    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Запуск и выход лаунчера: порядок шагов и журнал запуска.
    /// <para>
    /// Порядок здесь — это и есть поведение. Замок единственного экземпляра обязан
    /// браться раньше любого другого шага: две копии лаунчера синхронизируют одну
    /// папку игры независимо друг от друга — один качает файл, второй считает его лишним
    /// и удаляет, а маркер незавершённого обновления снимает тот, кто закончил первым.
    /// Шаг, уехавший выше замка, начинает выполняться и во второй копии тоже.
    /// </para>
    /// <para>
    /// Настоящий замок тесты не берут: каждый шаг запуска подменён, поэтому прогон
    /// не мешает ни соседним тестам, ни запущенному лаунчеру разработчика.
    /// </para>
    /// </summary>
    [Collection(ConfigStorageCollection.Name)]
    public class AppStartupTests : IDisposable {
        public void Dispose() => BootLog.ResetPathForTests();

        /// <summary>
        /// Шаги идут в том порядке, в котором их писал автор запуска: замок, тема,
        /// обработчики ошибок, уборка, метрика. Перестановка любого из них ломает
        /// либо защиту от второй копии, либо отчёты о падениях на самом раннем этапе.
        /// </summary>
        [Fact]
        public void ШагиЗапускаИдутВЗаданномПорядке() {
            var log = new List<string>();
            var startup = Recording(log, lockTaken: true);

            Assert.True(startup.Run());

            Assert.Equal(
                new[] { "замок", "тема", "обработчики", "уборка", "метрика" },
                log);
        }

        /// <summary>
        /// Главное свойство запуска: замок берётся ДО всего остального. Если бы применение
        /// темы, уборка каталога WebView2 или отправка метрики шли выше замка, их выполняла
        /// бы и вторая копия — та самая, которую лаунчер отказывается запускать.
        /// </summary>
        [Fact]
        public void ЗамокБерётсяРаньшеВсехРабочихШагов() {
            var log = new List<string>();
            var startup = Recording(log, lockTaken: true);

            startup.Run();

            var lockAt = log.IndexOf("замок");
            Assert.True(lockAt >= 0, "замок должен браться");
            Assert.True(lockAt < log.IndexOf("тема"));
            Assert.True(lockAt < log.IndexOf("обработчики"));
            Assert.True(lockAt < log.IndexOf("уборка"));
            Assert.True(lockAt < log.IndexOf("метрика"));
        }

        /// <summary>
        /// До замка лаунчер не трогает диск.
        /// <para>
        /// Порядок шагов проверяется подставными шагами, и они одинаково молчаливы —
        /// сам по себе список не показывает, что «тема» на первом запуске означает запись
        /// в файловую систему. А означает: чтение конфига разворачивает умолчания, то есть
        /// создаёт %APPDATA%\ChillHub\config.json и корень каталога игр. Пока это шло выше
        /// замка, копия, которой запускаться не разрешат, успевала создать каталог игр
        /// и вклиниться в неатомарную запись config.json той копии, которая замок взяла.
        /// </para>
        /// <para>
        /// Тема здесь настоящая (чтение конфига), а каталоги конфига уведены во временные:
        /// иначе тест писал бы в %APPDATA% разработчика.
        /// </para>
        /// </summary>
        [Fact]
        public void ДоЗамкаКонфигНаДискеНеСоздаётся() {
            using var cfgDir = new ConfigDirsScope();
            var configAtLock = true;
            var startup = new StartupSequence {
                ApplyTheme = () => _ = ChillHub.Core.ConfigService.Current,
                AcquireSingleInstance = () => {
                    configAtLock = File.Exists(cfgDir.ConfigPath);
                    return true;
                },
                InstallGlobalHandlers = () => { },
                CleanupLegacyWebViewFolder = () => { },
                SendStartMetric = () => { },
            };

            Assert.True(startup.Run());

            Assert.False(configAtLock, "config.json создан ДО взятия замка — до-замочный шаг пишет на диск");
            Assert.True(File.Exists(cfgDir.ConfigPath), "после запуска конфиг обязан появиться — иначе тема не выполнялась вовсе");
        }

        /// <summary>
        /// Занятый замок обрывает запуск немедленно: ни один следующий шаг не выполняется,
        /// и вызывающему сказано завершаться. Продолженный запуск означал бы вторую копию,
        /// портящую папку игры первой.
        /// </summary>
        [Fact]
        public void ЗанятыйЗамокОбрываетЗапускДоОстальныхШагов() {
            var log = new List<string>();
            var startup = Recording(log, lockTaken: false);

            Assert.False(startup.Run());

            Assert.Equal(new[] { "замок" }, log);
        }

        /// <summary>
        /// Сбой установки глобальных обработчиков не должен ронять запуск: без них лаунчер
        /// работоспособен, просто ошибки не попадут в отчёты. Уронив запуск здесь, мы бы
        /// сделали диагностику дороже самой болезни.
        /// </summary>
        [Fact]
        public void СбойОбработчиковНеМешаетЗапуску() {
            using var dir = new TempDir();
            BootLog.PathProvider = () => dir.PathTo("boot.log");
            var log = new List<string>();
            var startup = Recording(log, lockTaken: true);
            startup.InstallGlobalHandlers = () => throw new InvalidOperationException("обработчики не встали");

            Assert.True(startup.Run());

            Assert.Contains("метрика", log);
        }

        /// <summary>
        /// Уборка старого каталога WebView2 лезет в файловую систему и может не удаться
        /// (файлы заняты, нет прав). Запуск из-за неё не отменяется — иначе лаунчер
        /// переставал бы открываться из-за мусора от прошлых версий.
        /// </summary>
        [Fact]
        public void СбойУборкиНеМешаетЗапуску() {
            var log = new List<string>();
            var startup = Recording(log, lockTaken: true);
            startup.CleanupLegacyWebViewFolder = () => throw new IOException("каталог занят");

            Assert.True(startup.Run());

            Assert.Contains("метрика", log);
        }

        /// <summary>
        /// Метрика запуска — «выстрелил и забыл». Недоступная сеть не повод не показать
        /// пользователю лаунчер.
        /// </summary>
        [Fact]
        public void СбойМетрикиНеМешаетЗапуску() {
            var log = new List<string>();
            var startup = Recording(log, lockTaken: true);
            startup.SendStartMetric = () => throw new InvalidOperationException("сеть недоступна");

            Assert.True(startup.Run());
        }

        // ---- Инициализация типа приложения ----

        /// <summary>
        /// Тип <c>App</c> обязан инициализироваться без исключения.
        /// <para>
        /// Статический конструктор выполняется раньше <c>Main</c>, раньше любого нашего
        /// перехватчика ошибок и раньше первой записи в boot.log: упавший здесь лаунчер
        /// не оставляет о себе вообще ничего, кроме APPCRASH в журнале Windows. Так
        /// умерла версия 1.5.11 — попытка отключить прямоугольник фокуса через
        /// <c>FrameworkElement.FocusVisualStyleProperty.OverrideMetadata</c> падала с
        /// ArgumentException: свойство объявлено самим FrameworkElement, и метаданные
        /// для типа-владельца уже зарегистрированы.
        /// </para>
        /// </summary>
        [Fact]
        public void ТипПриложенияИнициализируетсяБезОшибки()
            => UiThread.Run(() => RuntimeHelpers.RunClassConstructor(typeof(ChillHub.App).TypeHandle));

        /// <summary>
        /// Прямоугольник фокуса снимается стилем по ключу
        /// <see cref="SystemParameters.FocusVisualStyleKey"/> — единственный способ достать
        /// элементы без собственного стиля, не трогая метаданные FrameworkElement.
        /// </summary>
        [Fact]
        public void ТемаОтключаетПрямоугольникФокусаГлобально() => UiThread.Run(() => {
            var theme = (ResourceDictionary)Application.LoadComponent(
                new Uri("/ChillHub;component/Themes/Theme.Dark.xaml", UriKind.Relative));

            Assert.True(
                theme.Contains(SystemParameters.FocusVisualStyleKey),
                "в теме нет стиля фокуса по системному ключу — рамка вернётся на элементы без своего стиля");
        });

        // ---- Окно обновления ----

        /// <summary>
        /// Решение «пускать ли дальше окна обновления». Диалог закрывают крестиком
        /// (результата нет), а отдельным признаком окно сообщает, что обновление
        /// не требуется. Перепутать их — либо не пустить человека в лаунчер, либо
        /// запустить его в обход обязательного обновления.
        /// </summary>
        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        [InlineData(false, true, true)]
        [InlineData(null, true, true)]
        [InlineData(false, false, false)]
        [InlineData(null, false, false)]
        public void ГлавноеОкноПоказываетсяТолькоСРазрешенияОкнаОбновления(bool? dialogResult, bool proceed, bool expected)
            => Assert.Equal(expected, StartupSequence.ShouldShowMainWindow(dialogResult, proceed));

        // ---- Выход ----

        /// <summary>
        /// При выходе останавливается опрос техработ: иначе фоновый таймер продолжает
        /// ходить на сервер уже после закрытия лаунчера.
        /// </summary>
        [Fact]
        public void ПриВыходеОстанавливаетсяОпрос() {
            var log = new List<string>();
            var shutdown = new ShutdownSequence {
                StopMaintenancePoll = () => log.Add("опрос"),
            };

            shutdown.Run();

            Assert.Equal(new[] { "опрос" }, log);
        }

        /// <summary>Сбой шага выхода не выпускает исключение наружу: выход обязан состояться.</summary>
        [Fact]
        public void СбойШагаВыходаНеРоняетВыход() {
            var shutdown = new ShutdownSequence {
                StopMaintenancePoll = () => throw new InvalidOperationException("опрос не остановился"),
            };

            shutdown.Run();
        }

        // ---- Журнал запуска ----

        /// <summary>
        /// Запись в boot.log — единственное, что объясняет, на каком шаге встал лаунчер,
        /// который до окна не дошёл. Формат «[время] текст» разбирает человек в отчёте.
        /// </summary>
        [Fact]
        public void ЗаписьВЖурналЗапускаСодержитВремяИТекст() {
            using var dir = new TempDir();
            var path = dir.PathTo("boot.log");

            BootLog.AppendTo(path, "Showing UpdateWindow");

            var line = File.ReadAllText(path);
            Assert.StartsWith("[", line, StringComparison.Ordinal);
            Assert.Contains("] Showing UpdateWindow", line, StringComparison.Ordinal);
            Assert.EndsWith("\r\n", line, StringComparison.Ordinal);
        }

        /// <summary>Записи копятся, а не затирают друг друга — иначе виден только последний шаг.</summary>
        [Fact]
        public void ЗаписиЖурналаЗапускаНакапливаются() {
            using var dir = new TempDir();
            var path = dir.PathTo("boot.log");

            BootLog.AppendTo(path, "шаг 1");
            BootLog.AppendTo(path, "шаг 2");

            var text = File.ReadAllText(path);
            Assert.Contains("шаг 1", text, StringComparison.Ordinal);
            Assert.Contains("шаг 2", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Недоступный файл журнала не должен ронять запуск: это журнал о запуске,
        /// а не сам запуск. Молча пропускаем запись.
        /// </summary>
        [Fact]
        public void НедоступныйФайлЖурналаНеРоняетЗапуск() {
            using var dir = new TempDir();
            var asDir = dir.PathTo("boot.log");
            Directory.CreateDirectory(asDir);

            BootLog.AppendTo(asDir, "шаг");

            Assert.True(Directory.Exists(asDir));
        }

        /// <summary>
        /// Маленький журнал не трогаем: обрезка каждой записи стоила бы полного чтения
        /// файла на каждом шаге запуска.
        /// </summary>
        [Fact]
        public void КороткийЖурналНеОбрезается() {
            using var dir = new TempDir();
            var path = dir.WriteFile("boot.log", "первая строка\r\nвторая строка\r\n");

            BootLog.Trim(path, new UTF8Encoding(false));

            Assert.Contains("первая строка", File.ReadAllText(path), StringComparison.Ordinal);
        }

        /// <summary>
        /// Разросшийся журнал обрезается до хвоста: без потолка boot.log растёт с каждым
        /// запуском вечно и однажды занимает диск, на который человек ставит игры.
        /// Хвост сохраняется — интересен последний запуск, а не первый.
        /// </summary>
        [Fact]
        public void РазросшийсяЖурналОбрезаетсяДоХвоста() {
            using var dir = new TempDir();
            var path = dir.PathTo("boot.log");
            var utf8 = new UTF8Encoding(false);
            var filler = new StringBuilder();
            for (var i = 0; i < 20000; i++) {
                filler.Append("строка мусора номер ").Append(i).Append("\r\n");
            }

            filler.Append("последняя строка перед обрезкой\r\n");
            File.WriteAllText(path, filler.ToString(), utf8);
            Assert.True(new FileInfo(path).Length > BootLog.MaxBytes, "журнал должен перерасти потолок");

            BootLog.Trim(path, utf8);

            var after = File.ReadAllText(path, utf8);
            Assert.True(new FileInfo(path).Length <= BootLog.KeepBytes + 200, "после обрезки журнал обязан уместиться в хвост");
            Assert.Contains("последняя строка перед обрезкой", after, StringComparison.Ordinal);
            Assert.Contains("boot.log truncated", after, StringComparison.Ordinal);
            Assert.DoesNotContain("строка мусора номер 0\r\n", after, StringComparison.Ordinal);
        }

        /// <summary>
        /// Первая строка после обрезки почти наверняка обрублена посередине — её
        /// выбрасывают, иначе в журнале появляется мусор, который читают как событие.
        /// </summary>
        [Fact]
        public void ОбрезкаНеОставляетОбрубленнуюСтроку() {
            using var dir = new TempDir();
            var path = dir.PathTo("boot.log");
            var utf8 = new UTF8Encoding(false);
            var filler = new StringBuilder();
            for (var i = 0; i < 20000; i++) {
                filler.Append("строка мусора номер ").Append(i).Append("\r\n");
            }

            File.WriteAllText(path, filler.ToString(), utf8);

            BootLog.Trim(path, utf8);

            foreach (var line in File.ReadAllLines(path, utf8)) {
                if (line.Length == 0) {
                    continue;
                }

                Assert.True(
                    line.StartsWith("[", StringComparison.Ordinal) || line.StartsWith("строка мусора номер ", StringComparison.Ordinal),
                    $"обрубок в журнале: '{line}'");
            }
        }

        /// <summary>Отсутствующий журнал обрезать нечего — и это не ошибка.</summary>
        [Fact]
        public void ОбрезкаОтсутствующегоЖурналаНичегоНеДелает() {
            using var dir = new TempDir();

            BootLog.Trim(dir.PathTo("нет-такого.log"), new UTF8Encoding(false));

            Assert.False(File.Exists(dir.PathTo("нет-такого.log")));
        }

        /// <summary>Последовательность запуска, каждый шаг которой отмечается в списке.</summary>
        private static StartupSequence Recording(List<string> log, bool lockTaken) => new StartupSequence {
            ApplyTheme = () => log.Add("тема"),
            AcquireSingleInstance = () => {
                log.Add("замок");
                return lockTaken;
            },
            InstallGlobalHandlers = () => log.Add("обработчики"),
            CleanupLegacyWebViewFolder = () => log.Add("уборка"),
            SendStartMetric = () => log.Add("метрика"),
        };
    }
}
