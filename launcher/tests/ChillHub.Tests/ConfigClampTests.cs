// <copyright file="ConfigClampTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Net.Http.Json;
    using System.Text;
    using System.Text.Json;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Нормализация прочитанного config.json.
    /// <para>
    /// Файл лежит в %APPDATA% и правится чем угодно, работающим от имени пользователя.
    /// Проверка адреса сервера отдельно уже покрыта, но она отвечает лишь на вопрос
    /// «годится ли значение». Здесь проверяется, что негодное значение действительно
    /// ЗАМЕНЯЕТСЯ на умолчание: именно по этому адресу лаунчер берёт манифест
    /// самообновления и кладёт полученные файлы поверх ChillHub.exe.
    /// </para>
    /// </summary>
    public class ConfigClampTests {
        /// <summary>Адрес не по https заменяется умолчанием, а не остаётся как есть.</summary>
        [Theory]
        [InlineData("http://attacker.invalid")]
        [InlineData("http://launcher.samoy.love")]
        [InlineData("ftp://launcher.samoy.love")]
        [InlineData("launcher.samoy.love")]
        [InlineData("")]
        [InlineData("   ")]
        public void НеприемлемыйАдресЗаменяетсяУмолчанием(string url) {
            var cfg = new AppConfig { ApiBaseUrl = url };

            ConfigService.Clamp(cfg);

            Assert.Equal(AppConfig.DefaultApiBaseUrl, cfg.ApiBaseUrl);
        }

        /// <summary>Годный адрес остаётся нетронутым — иначе локальная разработка станет невозможной.</summary>
        [Theory]
        [InlineData("https://launcher.samoy.love")]
        [InlineData("https://staging.samoy.love")]
        [InlineData("http://localhost:8080")]
        [InlineData("http://127.0.0.1:55777")]
        public void ГодныйАдресОстаётсяНетронутым(string url) {
            var cfg = new AppConfig { ApiBaseUrl = url };

            ConfigService.Clamp(cfg);

            Assert.Equal(url, cfg.ApiBaseUrl);
        }

        /// <summary>
        /// Число потоков загрузки зажимается в 2..16: ноль остановил бы закачку совсем,
        /// а несколько сотен — открыли бы столько соединений, что сервер начал бы отказывать.
        /// </summary>
        [Theory]
        [InlineData(0, 2)]
        [InlineData(-5, 2)]
        [InlineData(1, 2)]
        [InlineData(2, 2)]
        [InlineData(8, 8)]
        [InlineData(16, 16)]
        [InlineData(17, 16)]
        [InlineData(int.MaxValue, 16)]
        public void ЧислоПотоковЗажимаетсяВДопустимыеПределы(int given, int expected) {
            var cfg = new AppConfig { DownloadThreads = given };

            ConfigService.Clamp(cfg);

            Assert.Equal(expected, cfg.DownloadThreads);
        }

        /// <summary>Пустой путь к играм заменяется умолчанием, иначе игры уедут в каталог запуска.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ПустойПутьКИграмЗаменяетсяУмолчанием(string? path) {
            var cfg = new AppConfig { GamesPath = path! };

            ConfigService.Clamp(cfg);

            Assert.False(string.IsNullOrWhiteSpace(cfg.GamesPath));
            Assert.Equal(AppConfig.DefaultGamesPath(), cfg.GamesPath);
        }

        /// <summary>Заданный пользователем путь к играм не трогаем: это его выбор диска.</summary>
        [Fact]
        public void ЗаданныйПутьКИграмСохраняется() {
            var cfg = new AppConfig { GamesPath = @"E:\Мои игры" };

            ConfigService.Clamp(cfg);

            Assert.Equal(@"E:\Мои игры", cfg.GamesPath);
        }

        /// <summary>Тумблеры приватности нормализация не трогает — их значение задаёт только пользователь.</summary>
        [Fact]
        public void ТумблерыПриватностиНеСбрасываются() {
            var cfg = new AppConfig { AutoErrorReports = false, SendUsageMetrics = false };

            ConfigService.Clamp(cfg);

            Assert.False(cfg.AutoErrorReports);
            Assert.False(cfg.SendUsageMetrics);
        }

        /// <summary>
        /// Конфиг переживает круг «сериализация — разбор»: именно так он ложится на диск
        /// и поднимается оттуда при следующем запуске.
        /// </summary>
        [Fact]
        public void КонфигПереживаетКругЧерезJson() {
            var cfg = new AppConfig {
                GamesPath = @"D:\Games\ChillHub",
                DownloadThreads = 12,
                ApiBaseUrl = "https://launcher.samoy.love",
                LastGameId = "lethal-company",
                AutoErrorReports = false,
                SendUsageMetrics = false,

            };

            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;

            Assert.Equal(cfg.GamesPath, back.GamesPath);
            Assert.Equal(cfg.DownloadThreads, back.DownloadThreads);
            Assert.Equal(cfg.ApiBaseUrl, back.ApiBaseUrl);
            Assert.Equal(cfg.LastGameId, back.LastGameId);
            Assert.False(back.AutoErrorReports);
            Assert.False(back.SendUsageMetrics);
        }

        /// <summary>
        /// Незнакомые поля из конфига более новой версии не роняют разбор: пользователь
        /// мог откатиться на предыдущую сборку, и его настройки должны пережить откат.
        /// </summary>
        [Fact]
        public void НезнакомыеПоляНеРоняютРазбор() {
            const string json = """
                { "GamesPath": "D:\\Games", "DownloadThreads": 4, "Theme": "dark", "БудущаяНастройка": 42 }
                """;

            var cfg = JsonSerializer.Deserialize<AppConfig>(json)!;
            ConfigService.Clamp(cfg);

            Assert.Equal(@"D:\Games", cfg.GamesPath);
            Assert.Equal(4, cfg.DownloadThreads);
        }

        /// <summary>
        /// Список игр с сервера приходит в camelCase. Разбор обязан оставаться
        /// регистронезависимым: при переходе на строгий разбор поля молча стали бы
        /// пустыми, и лаунчер показал бы список игр без идентификаторов и версий.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task СписокИгрРазбираетсяИзCamelCase() {
            const string json = """
                {"items":[{"gameId":"lethal-company","title":"Lethal Company",
                "hasLatest":true,"latestVersion":"1.4.2","manifestUrl":"https://example.test/m.json",
                "exeRelativePath":"Lethal Company.exe","iconUrl":"https://example.test/i.png"}]}
                """;

            using var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
            var resp = await content.ReadFromJsonAsync<GamesResponse>();

            var game = Assert.Single(resp!.Items);
            Assert.Equal("lethal-company", game.GameId);
            Assert.Equal("Lethal Company", game.Title);
            Assert.True(game.HasLatest);
            Assert.Equal("1.4.2", game.LatestVersion);
            Assert.Equal("Lethal Company.exe", game.ExeRelativePath);
        }

        /// <summary>Игра показывается пользователю по названию, а не по имени класса.</summary>
        [Fact]
        public void ИграПоказываетсяПоНазванию() {
            Assert.Equal("Lethal Company", new GameInfo { Title = "Lethal Company" }.ToString());
        }

        /// <summary>Свежеустановленный лаунчер сразу пригоден к работе: умолчания проходят нормализацию без изменений.</summary>
        [Fact]
        public void УмолчанияПереживаютНормализациюБезИзменений() {
            var cfg = new AppConfig();
            var path = cfg.GamesPath;
            var url = cfg.ApiBaseUrl;
            var threads = cfg.DownloadThreads;

            ConfigService.Clamp(cfg);

            Assert.Equal(path, cfg.GamesPath);
            Assert.Equal(url, cfg.ApiBaseUrl);
            Assert.Equal(threads, cfg.DownloadThreads);
        }
    }
}
