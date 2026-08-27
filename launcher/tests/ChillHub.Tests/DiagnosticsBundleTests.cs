// <copyright file="DiagnosticsBundleTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;

    using ChillHub.Core;
    using ChillHub.Core.Logging;

    using Xunit;

    /// <summary>
    /// Сборка бандла диагностики целиком.
    /// <para>
    /// Бандл уезжает на сервер вместе с отчётом об ошибке и обратной связью, и это самый
    /// приватный кусок лаунчера: в нём конфиг пользователя, список его игр, пути к его
    /// файлам и хвосты журналов. Две вещи здесь обязаны выполняться всегда — бандл
    /// собирается даже когда половины каталогов нет (иначе жалоба уходит пустой ровно
    /// тогда, когда что-то сломалось), и из него вычищено имя пользователя Windows.
    /// </para>
    /// <para>
    /// Тесты подменяют каталог логов (подмена процессная, см. <c>Logger.OverrideForTests</c>),
    /// каталоги конфига и папку игр — настоящие данные разработчика при этом не читаются
    /// и не пишутся.
    /// </para>
    /// </summary>
    [Collection(GamesPathCollection.Name)]
    public class DiagnosticsBundleTests {
        /// <summary>Потолок всего бандла в байтах UTF-8; согласован с сервером.</summary>
        private const int BundleMaxBytes = 1024 * 1024;

        /// <summary>
        /// Бандл собирается и содержит все разделы, на которые смотрит человек при разборе.
        /// Пропавший заголовок означает, что секция молча выпала из отчёта — заметить это
        /// по одной жалобе невозможно.
        /// </summary>
        [Fact]
        public void БандлСодержитВсеРазделы() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();
            using (Logger.OverrideForTests(logs.Root)) {
                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains("# Chill Hub Diagnostics Bundle", text);
                Assert.Contains("## Config", text);
                Assert.Contains("## Launcher Files (SHA-256)", text);
                Assert.Contains("## Games Root Listing", text);
                Assert.Contains("## Mods", text);
                Assert.Contains("## Logs", text);
                Assert.Contains("## SelfUpdate Logs", text);
            }
        }

        /// <summary>
        /// Содержимое config.json попадает в бандл: без него непонятно, на каком сервере
        /// и с какими настройками воспроизводить жалобу.
        /// </summary>
        [Fact]
        public void КонфигПопадаетВБандл() {
            using var cfg = new ConfigDirsScope();
            using var logs = new TempDir();
            // Маркер латиницей: System.Text.Json по умолчанию экранирует не-ASCII,
            // и кириллица легла бы в файл как \u04XX.
            cfg.WriteConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = @"Z:\marker-config-games" }));

            using (Logger.OverrideForTests(logs.Root)) {
                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains("marker-config-games", text);
                Assert.Contains("```json", text);
            }
        }

        /// <summary>
        /// Конфига на диске может не быть (первый запуск, снесённый профиль) — это повод
        /// написать строчку в бандл, а не остаться без бандла.
        /// </summary>
        [Fact]
        public void ОтсутствующийКонфигНеРоняетСбор() {
            using var cfg = new ConfigDirsScope();
            using var logs = new TempDir();

            using (Logger.OverrideForTests(logs.Root)) {
                // ConfigService.Current трогать до сборки нельзя: он развернёт умолчания
                // и запишет config.json, а проверяется здесь именно его отсутствие.
                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains("(config.json not found)", text);
                Assert.Contains("## Logs", text);
            }
        }

        /// <summary>
        /// Папки игр может не быть (диск отключили, путь правили руками). Сбор диагностики
        /// обязан это пережить: отчёт нужен как раз в таких случаях.
        /// </summary>
        [Fact]
        public void ОтсутствующаяПапкаИгрНеРоняетСбор() {
            using var games = new GamesPathScope(@"Q:\нет-такого-диска\games");
            using var logs = new TempDir();

            using (Logger.OverrideForTests(logs.Root)) {
                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains("(games root not found)", text);
                Assert.Contains("## Logs", text);
            }
        }

        /// <summary>
        /// Дерево папки игр обрывается по глубине.
        /// <para>
        /// Пользователь волен назначить папкой игр что угодно — хоть корень диска, хоть
        /// «Документы». Прежняя глубина 10 выкладывала в отчёт всё дерево его файлов
        /// целиком, а для разбора хватает того, какие игры установлены.
        /// </para>
        /// </summary>
        [Fact]
        public void ДеревоПапокИгрОбрываетсяПоГлубине() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();
            Directory.CreateDirectory(
                Path.Combine(games.Root, "играА", "уровеньБ", "уровеньВ", "уровеньГ"));

            using (Logger.OverrideForTests(logs.Root)) {
                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains("играА", text);
                Assert.Contains("уровеньБ", text);
                Assert.Contains("уровеньВ", text);

                // Глубина 2: четвёртый уровень наружу уже не выкладывается.
                Assert.DoesNotContain("уровеньГ", text);
            }
        }

        /// <summary>
        /// Хеши файлов лаунчера считаются: по ним видно, что у пользователя лежит
        /// недокачанная или подменённая сборка, — иначе такую жалобу не разобрать.
        /// </summary>
        [Fact]
        public void ХешиФайловЛаунчераСчитаются() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();

            using (Logger.OverrideForTests(logs.Root)) {
                var text = Diagnostics.Build().LogsMarkdown;
                var start = text.IndexOf("## Launcher Files (SHA-256)", StringComparison.Ordinal);
                Assert.True(start >= 0, "раздел с хешами файлов лаунчера пропал из бандла");

                var section = text[start..];
                Assert.Contains("Root: ", section);
                Assert.Matches("[0-9a-f]{64}", section[..Math.Min(section.Length, 60000)]);
            }
        }

        /// <summary>
        /// Архивы после ротации попадают в бандл наравне с активным файлом: момент отказа
        /// часто оказывается ровно за границей ротации, и без архива его не видно.
        /// </summary>
        [Fact]
        public void АрхивныеЛогиПопадаютВБандл() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();

            using (Logger.OverrideForTests(logs.Root)) {
                var active = "маркер-активного-" + Guid.NewGuid().ToString("N");
                var archived = "маркер-архивного-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(Path.Combine(logs.Root, "client.1.log"), archived, new UTF8Encoding(false));
                Logger.Info(active);

                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains(active, text);
                Assert.Contains(archived, text);
            }
        }

        /// <summary>
        /// Один болтливый файл не съедает бандл целиком.
        /// <para>
        /// Сервер отвергает слишком большое тело ЦЕЛИКОМ: отчёт не обрежется, а пропадёт.
        /// Поэтому у логов свой суммарный бюджет, у каждого файла — свой хвост, и обрезка
        /// помечается в тексте, чтобы читатель не принял огрызок за полный журнал.
        /// </para>
        /// </summary>
        [Fact]
        public void БолтливыеЛогиРежутсяПоБюджету() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();

            using (Logger.OverrideForTests(logs.Root)) {
                // Бюджет на все логи — 512 КиБ, хвост одного файла — 128 КиБ.
                // Пять файлов по 200 КиБ гарантированно упираются и в тот, и в другой.
                var noise = new string('x', 200 * 1024);
                foreach (var name in new[] { "client.1.log", "client.2.log", "client.3.log", "client.4.log", "boot.log" }) {
                    File.WriteAllText(Path.Combine(logs.Root, name), noise, new UTF8Encoding(false));
                }

                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains("(tail only)", text);
                Assert.Contains("(log budget exhausted", text);
            }
        }

        /// <summary>
        /// Бандл укладывается в потолок, согласованный с сервером и nginx. Превышение —
        /// это не обрезанный отчёт, а отвергнутый запрос: жалоба теряется молча.
        /// </summary>
        [Fact]
        public void БандлУкладываетсяВБюджет() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();

            using (Logger.OverrideForTests(logs.Root)) {
                var noise = new string('ы', 400 * 1024);
                foreach (var name in new[] { "client.1.log", "client.2.log", "client.3.log", "boot.log" }) {
                    File.WriteAllText(Path.Combine(logs.Root, name), noise, new UTF8Encoding(false));
                }

                var text = Diagnostics.Build().LogsMarkdown;

                Assert.True(
                    Encoding.UTF8.GetByteCount(text) <= BundleMaxBytes,
                    $"бандл {Encoding.UTF8.GetByteCount(text)} байт при потолке {BundleMaxBytes}");
            }
        }

        /// <summary>
        /// Редакция применяется ко ВСЕМУ бандлу, а не только к секции логов.
        /// <para>
        /// Имя пользователя Windows — это его настоящие имя и фамилия чаще, чем кажется,
        /// а в бандле оно лезет отовсюду: из пути к конфигу, из путей к файлам лаунчера,
        /// из папки игр. Достаточно одной неотредактированной секции, чтобы оно уехало
        /// на сервер вместе с жалобой.
        /// </para>
        /// </summary>
        [Fact]
        public void РедакцияПрименяетсяКоВсемуБандлу() {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(string.IsNullOrWhiteSpace(profile), "без пути к профилю проверять нечего");

            using var cfg = new ConfigDirsScope();
            using var logs = new TempDir();

            // Путь под профилем — но НЕ существующий: создавать что-либо в профиле
            // пользователя тест не имеет права.
            var marked = Path.Combine(profile, "маркер-приватности");
            cfg.WriteConfig(JsonSerializer.Serialize(new AppConfig { GamesPath = marked }));

            using (Logger.OverrideForTests(logs.Root)) {
                var bundle = Diagnostics.Build();

                Assert.Contains("%USERPROFILE%", bundle.LogsMarkdown);
                Assert.DoesNotContain(profile, bundle.LogsMarkdown);

                // Подсказки редактируются отдельным проходом — и его тоже легко потерять.
                Assert.DoesNotContain(profile, bundle.SystemHints["gamesRoot"]);
                Assert.DoesNotContain(profile, bundle.SystemHints["configPath"]);
                Assert.DoesNotContain(profile, bundle.SystemHints["logsDir"]);
            }
        }

        /// <summary>
        /// Состояние модов попадает в бандл целиком: метка версии набора, размер манифеста,
        /// конфиг доорстопа, дерево BepInEx и хвост его журнала.
        /// <para>
        /// «Игра не запускается после модов» — самая частая жалоба, которую невозможно
        /// разобрать по одному тексту обращения: причина всегда в одном из этих пяти мест,
        /// и пропавшая секция означает переписку на несколько дней вместо ответа сразу.
        /// </para>
        /// </summary>
        [Fact]
        public void СостояниеМодовПопадаетВБандл() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();

            var gameDir = Path.Combine(games.Root, "играСМодами");
            var plugins = Path.Combine(gameDir, "BepInEx", "plugins");
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(gameDir, ".mods.version"), "маркер-версии-модов");
            File.WriteAllText(Path.Combine(gameDir, ".mods.manifest.json"), "{\"files\":[]}");
            File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), "enabled=маркер-доорстопа");
            File.WriteAllText(Path.Combine(plugins, "МойПлагин.dll"), "не-настоящая-dll");
            File.WriteAllText(
                Path.Combine(gameDir, "BepInEx", "LogOutput.log"), "маркер-журнала-модов");

            using (Logger.OverrideForTests(logs.Root)) {
                var text = Diagnostics.Build().LogsMarkdown;

                Assert.Contains("## Mods", text);
                Assert.Contains("играСМодами", text);
                Assert.Contains("маркер-версии-модов", text);
                Assert.Contains("маркер-доорстопа", text);
                Assert.Contains("маркер-журнала-модов", text);

                // Плагины видно по дереву: задвоенный или чужой dll ищут именно здесь.
                Assert.Contains("МойПлагин.dll", text);

                // Манифест — только имя и размер: внутри перечень всех файлов набора,
                // он не помещается в бандл и для разбора не нужен.
                Assert.Contains(".mods.manifest.json [size=", text);
                Assert.DoesNotContain("{\"files\":[]}", text);
            }
        }

        /// <summary>
        /// Ванильные игры секцию модов не раздувают: их папки уже перечислены в дереве
        /// выше, а бюджет бандла общий — то, что уйдёт на пересказ библиотеки, отнимется
        /// у журналов.
        /// </summary>
        [Fact]
        public void ИгрыБезМодовВСекциюМодовНеПопадают() {
            using var games = new GamesPathScope();
            using var logs = new TempDir();
            Directory.CreateDirectory(Path.Combine(games.Root, "ванильнаяИгра"));

            using (Logger.OverrideForTests(logs.Root)) {
                var text = Diagnostics.Build().LogsMarkdown;
                var start = text.IndexOf("## Mods", StringComparison.Ordinal);
                Assert.True(start >= 0, "секция модов пропала из бандла");

                var section = text[start..text.IndexOf("## Logs", StringComparison.Ordinal)];
                Assert.Contains("(no modded games found)", section);
                Assert.DoesNotContain("ванильнаяИгра", section);
            }
        }
    }
}
