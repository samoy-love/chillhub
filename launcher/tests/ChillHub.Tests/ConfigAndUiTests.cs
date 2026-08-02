// <copyright file="ConfigAndUiTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading.Tasks;

    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.UI;

    using Xunit;

    /// <summary>
    /// Конфигурация и мелкие помощники интерфейса.
    /// <para>
    /// Запись конфига здесь намеренно НЕ проверяется: <see cref="ConfigService"/> пишет в
    /// настоящий %APPDATA%\ChillHub\config.json, и тест затёр бы рабочие настройки
    /// разработчика. Проверяется то, что можно проверить без записи: значения по умолчанию
    /// и расположение файла.
    /// </para>
    /// </summary>
    public class ConfigAndUiTests {
        /// <summary>
        /// Конфиг обязан лежать в %APPDATA%, а не в %LOCALAPPDATA%.
        /// <para>
        /// %LOCALAPPDATA%\ChillHub — это КАТАЛОГ УСТАНОВКИ. Пока config.json лежал там, он
        /// попадал в пакет сборки и в манифест обновления, а апдейтер отказывался его
        /// перезаписывать (файл в --preserve) — лаунчер видел вечное расхождение хешей и
        /// предлагал обновление при каждом запуске. Версии 1.0.2 и 1.0.3 именно так и вели себя.
        /// </para>
        /// </summary>
        [Fact]
        public void КонфигЛежитВРоумингеАНеВКаталогеУстановки() {
            var path = ConfigService.ConfigFilePath;
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            Assert.StartsWith(roaming, path, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                path.StartsWith(local, StringComparison.OrdinalIgnoreCase),
                "конфиг в каталоге установки снова запустит вечный цикл самообновления");
            Assert.Equal("config.json", Path.GetFileName(path));
        }

        /// <summary>Значения по умолчанию пригодны к работе сразу после установки.</summary>
        [Fact]
        public void ЗначенияПоУмолчаниюПригодныКРаботе() {
            var cfg = new AppConfig();

            Assert.False(string.IsNullOrWhiteSpace(cfg.GamesPath));
            Assert.False(string.IsNullOrWhiteSpace(cfg.ApiBaseUrl));
            Assert.StartsWith("https://", cfg.ApiBaseUrl, StringComparison.Ordinal);
            Assert.InRange(cfg.DownloadThreads, 2, 16);
        }

        /// <summary>Путь по умолчанию — абсолютный и с корнем диска, иначе игры уедут в каталог запуска.</summary>
        [Fact]
        public void ПутьКИграмПоУмолчаниюАбсолютный() {
            var path = AppConfig.DefaultGamesPath();
            Assert.True(Path.IsPathRooted(path), $"'{path}' не абсолютный");
        }

        /// <summary>Подсказка о месте показывается только когда есть что качать.</summary>
        [Fact]
        public void ПодсказкаОМестеПоявляетсяТолькоКогдаЕстьЧтоКачать() {
            Assert.Equal(string.Empty, SpaceHint.BuildText(0, 100_000_000_000));
            Assert.Equal(string.Empty, SpaceHint.BuildText(-1, 100_000_000_000));
            Assert.False(string.IsNullOrWhiteSpace(SpaceHint.BuildText(1_000_000, 100_000_000_000)));
        }

        /// <summary>В подсказке видно и нужный объём, и доступный — иначе цифра ничего не значит.</summary>
        [Fact]
        public void ПодсказкаПоказываетИНужноеИДоступное() {
            var text = SpaceHint.BuildText(1_000_000, 2_000_000_000);
            Assert.Contains("Нужно", text, StringComparison.Ordinal);
            Assert.Contains("доступно", text, StringComparison.Ordinal);
        }

        /// <summary>Кеш оценок отдаёт ровно то, что в него положили.</summary>
        [Fact]
        public void КешОценокВозвращаетПоложенное() {
            var hint = new SpaceHint();
            Assert.False(hint.TryGet("g", out _));

            hint.Remember("g", 42);
            Assert.True(hint.TryGet("g", out var need));
            Assert.Equal(42, need);
        }

        /// <summary>Идентификатор игры регистронезависим — он приходит и из API, и из путей.</summary>
        [Fact]
        public void КешОценокНеРазличаетРегистр() {
            var hint = new SpaceHint();
            hint.Remember("Lethal-Company", 7);
            Assert.True(hint.TryGet("lethal-company", out var need));
            Assert.Equal(7, need);
        }

        /// <summary>Пустой идентификатор в кеш не попадает и оттуда не достаётся.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ПустойИдентификаторВКешНеПопадает(string? gameId) {
            var hint = new SpaceHint();
            hint.Remember(gameId, 100);
            Assert.False(hint.TryGet(gameId, out _));
        }

        /// <summary>
        /// Смена папки для игр обязана сбрасывать оценки: они посчитаны для другого диска,
        /// и показать «40 ГБ доступно» от прежнего диска — прямой обман.
        /// </summary>
        [Fact]
        public void СбросКешаЗабываетВсеОценки() {
            var hint = new SpaceHint();
            hint.Remember("a", 1);
            hint.Remember("b", 2);

            hint.Clear();

            Assert.False(hint.TryGet("a", out _));
            Assert.False(hint.TryGet("b", out _));
        }

        /// <summary>Кеш читают и пишут из фоновых задач одновременно — гонка не должна ронять UI.</summary>
        [Fact]
        public async Task КешОценокВыдерживаетОдновременныйДоступ() {
            var hint = new SpaceHint();
            var tasks = new Task[8];
            for (var i = 0; i < tasks.Length; i++) {
                var n = i;
                tasks[i] = Task.Run(() => {
                    for (var j = 0; j < 200; j++) {
                        hint.Remember("game-" + (j % 5), n);
                        hint.TryGet("game-" + (j % 5), out _);
                    }
                });
            }

            await Task.WhenAll(tasks);
            Assert.True(hint.TryGet("game-0", out _));
        }

        /// <summary>Дата новости показывается по-русски: «5 января 2026», а не «01/05/2026».</summary>
        [Fact]
        public void ДатаНовостиПоказываетсяПоРусски() {
            var text = (string)new RuDateConverter().Convert(
                new DateTime(2026, 1, 5), typeof(string), null!, CultureInfo.InvariantCulture);

            Assert.Contains("января", text, StringComparison.Ordinal);
            Assert.Contains("2026", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Дата с сервера приходит строкой в UTC. Брать компонент даты обязательно: иначе
        /// новость, опубликованная в 23:30 UTC, у московского читателя съезжает на день вперёд.
        /// </summary>
        [Theory]
        [InlineData("2026-01-05T23:30:00Z")]
        [InlineData("2026-01-05")]
        [InlineData("2026-01-05T00:00:00Z")]
        public void ДатаИзСтрокиНеСъезжаетНаСоседнийДень(string iso) {
            var text = (string)new RuDateConverter().Convert(
                iso, typeof(string), null!, CultureInfo.InvariantCulture);

            Assert.Contains("5 января 2026", text, StringComparison.Ordinal);
        }

        /// <summary>Неразбираемая дата показывается как есть — лучше сырой текст, чем пустое место.</summary>
        [Fact]
        public void НеразбираемаяДатаПоказываетсяКакЕсть() {
            const string raw = "когда-то давно";
            Assert.Equal(raw, new RuDateConverter().Convert(raw, typeof(string), null!, CultureInfo.InvariantCulture));
        }

        /// <summary>Пустое значение даёт пустую строку, а не «01.01.0001».</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(42)]
        public void ПустоеЗначениеДаётПустуюСтроку(object? value) {
            Assert.Equal(
                string.Empty,
                new RuDateConverter().Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture));
        }

        /// <summary>DateTimeOffset тоже разбирается — так дата приходит из System.Text.Json.</summary>
        [Fact]
        public void СмещениеВремениРазбирается() {
            var text = (string)new RuDateConverter().Convert(
                new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero), typeof(string), null!, CultureInfo.InvariantCulture);
            Assert.Contains("5 января 2026", text, StringComparison.Ordinal);
        }
    }
}
