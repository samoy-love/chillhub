// <copyright file="ChangelogTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;

    using ChillHub.Core;
    using ChillHub.Core.Changelog;

    using Xunit;

    /// <summary>
    /// Предохранитель списка обновлений.
    /// <para>
    /// Держит два обещания, данных пользователю. Первое: КАЖДАЯ выкатка попадает
    /// в список — версия объявляется в ChillHub.csproj, и если её здесь нет,
    /// сборка красная. Иначе окно «Что нового» после обновления открывалось бы
    /// с чужой, прошлой записью наверху.
    /// </para>
    /// <para>
    /// Второе: список написан для игрока, а не для разработчика. Слова про
    /// внутренности лаунчера, номера задач и названия файлов в нём запрещены
    /// машинно — совести тут недостаточно, потому что писать записи будут
    /// в конце работы над версией, когда голова ещё в коде.
    /// </para>
    /// </summary>
    public class ChangelogTests {
        /// <summary>
        /// Корни слов, которых игрок не поймёт или которые говорят о коде, а не
        /// об экране. Список намеренно короткий: он ловит типичный съезд в
        /// технический язык, а не пытается вычитать текст за автора.
        /// </summary>
        private static readonly string[] ForbiddenStems = {
            "манифест", "хеш", "хэш", "рефактор",
            "исключени", "конфиг", "коммит", "репозитор", "деплой", "мерж",
        };

        /// <summary>
        /// То же, но латиницей. Ищем целыми словами: «url» внутри «YourLauncher»
        /// не имеет к адресам никакого отношения, а подстрочный поиск ловил именно его.
        /// </summary>
        private static readonly string[] ForbiddenTerms = {
            "json", "http", "api", "url", "dispatcher", "aot", "ed25519", "sha", "blake3",
        };

        /// <summary>
        /// Обороты, по которым текст сразу читается как написанный не человеком,
        /// а генератором: вводные без содержания и похвала себе вместо факта.
        /// </summary>
        private static readonly string[] ForbiddenPhrases = {
            "кроме того", "более того", "стоит отметить", "важно отметить",
            "в целом", "значительно улучш", "существенно улучш", "оптимизирова",
            "повышена стабильность", "ряд улучшений", "различные исправления",
            "мелкие исправления и улучшения", "производительность улучшена",
        };

        /// <summary>
        /// Разговорные и приблизительные слова. Каждое из них когда-то стояло в этом
        /// файле вместо точного: «освежает» вместо «обновляет», «качаются» вместо
        /// «скачиваются», «вылезает» вместо «выходит за край».
        /// </summary>
        private static readonly string[] ForbiddenColloquialisms = {
            "освежа", "качаются", "качается", "вылеза", "до ума", "раздражител",
        };

        /// <summary>
        /// Имена, у которых русского написания нет: их латиница законна. Сюда же —
        /// надписи на клавишах: игрок читает «Tab» на своей клавиатуре, а не перевод.
        /// Всё остальное латиницей — либо непереведённый термин, либо код ошибки,
        /// который игроку показывать нечего.
        /// </summary>
        private static readonly string[] AllowedLatinNames = {
            "Chill Hub", "Steam", "Windows", "FreeTP", "Thunderstore", "Tab",
        };

        /// <summary>Номера задач и PR — след процесса разработки, игроку он ни о чём не говорит.</summary>
        private static readonly Regex IssueReference = new Regex(@"#\d+", RegexOptions.CultureInvariant);

        [Fact]
        public void ДляОбъявленнойВерсииЕстьЗапись() {
            var declared = DeclaredVersion();
            var versions = ChangelogData.Releases.Select(r => r.Version).ToList();

            Assert.True(
                versions.Contains(declared, StringComparer.Ordinal),
                $"версия {declared} объявлена в ChillHub.csproj, но записи о ней нет в ChangelogData. " +
                "Подняли версию — опишите, что в ней изменилось для игрока.");
        }

        [Fact]
        public void ОбъявленнаяВерсияСтоитПервой() {
            var declared = DeclaredVersion();

            Assert.Equal(declared, ChangelogData.Releases[0].Version);
        }

        [Fact]
        public void ВыпускиИдутОтНовогоКСтарому() {
            var releases = ChangelogData.Releases;
            for (var i = 1; i < releases.Count; i++) {
                var newer = releases[i - 1].Version;
                var older = releases[i].Version;
                Assert.True(
                    VersionOrder.Compare(newer, older) > 0,
                    $"выпуск {newer} стоит выше {older}, хотя не новее его");
            }
        }

        [Fact]
        public void ВерсииНеПовторяются() {
            var versions = ChangelogData.Releases.Select(r => r.Version).ToList();

            Assert.Equal(versions.Count, versions.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void СписокНачинаетсяСПервогоВыпуска() {
            Assert.Equal("1.0", ChangelogData.Releases[^1].Version);
        }

        [Fact]
        public void ТехническиеВыпускиИгрокуНеПоказываются() {
            Assert.DoesNotContain(ChangelogData.Visible, r => r.Technical);
            Assert.All(ChangelogData.Visible, r => Assert.NotEmpty(r.Changes));

            // Смысл записи о техническом выпуске — только в том, чтобы у выкатки была
            // строка. Пункты в ней означали бы, что кто-то всё же написал «ничего не
            // изменилось» словами и показал это игроку.
            foreach (var release in ChangelogData.Releases) {
                if (release.Technical) {
                    Assert.Empty(release.Changes);
                }
            }
        }

        [Fact]
        public void ВидимыеВыпускиИдутВТомЖеПорядке() {
            var expected = ChangelogData.Releases.Where(r => !r.Technical).Select(r => r.Version).ToList();

            Assert.Equal(expected, ChangelogData.Visible.Select(r => r.Version).ToList());
        }

        [Fact]
        public void УКаждогоВыпускаЕстьДатаИХотяБыОдинПункт() {
            foreach (var release in ChangelogData.Releases) {
                Assert.False(string.IsNullOrWhiteSpace(release.Date), $"у выпуска {release.Version} нет даты");
                Assert.True(
                    DateTime.TryParseExact(
                        release.Date,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out _),
                    $"дата выпуска {release.Version} записана не как ГГГГ-ММ-ДД: {release.Date}");
                Assert.True(
                    release.Technical || release.Changes.Count > 0,
                    $"выпуск {release.Version} не помечен техническим, но и рассказать о нём нечего");
                Assert.All(release.Changes, line => Assert.False(string.IsNullOrWhiteSpace(line)));
            }
        }

        [Fact]
        public void ПунктыНаписаныДляИгрока() {
            foreach (var (version, line) in AllLines()) {
                var lower = line.ToLowerInvariant();

                foreach (var stem in ForbiddenStems) {
                    Assert.False(
                        lower.Contains(stem, StringComparison.Ordinal),
                        $"выпуск {version}: «{stem}» — слово про устройство лаунчера, а не про то, " +
                        $"что увидел игрок. Перепишите пункт: {line}");
                }

                foreach (var term in ForbiddenTerms) {
                    Assert.False(
                        Regex.IsMatch(lower, $@"{Regex.Escape(term)}", RegexOptions.CultureInvariant),
                        $"выпуск {version}: «{term}» — слово про устройство лаунчера, а не про то, " +
                        $"что увидел игрок. Перепишите пункт: {line}");
                }

                foreach (var word in ForbiddenColloquialisms) {
                    Assert.False(
                        lower.Contains(word, StringComparison.Ordinal),
                        $"выпуск {version}: «{word}» — разговорное слово вместо точного. Перепишите пункт: {line}");
                }

                foreach (var phrase in ForbiddenPhrases) {
                    Assert.False(
                        lower.Contains(phrase, StringComparison.Ordinal),
                        $"выпуск {version}: «{phrase}» ничего не сообщает. Напишите, что именно изменилось: {line}");
                }

                Assert.False(
                    IssueReference.IsMatch(line),
                    $"выпуск {version}: номер задачи игроку ни о чём не говорит: {line}");
            }
        }

        /// <summary>
        /// Список — по-русски. Английские слова в нём появляются ровно одним способом:
        /// кто-то перенёс в текст то, что увидел в коде или в логе.
        /// </summary>
        [Fact]
        public void ПунктыНаписаныПоРусски() {
            foreach (var (version, line) in AllLines()) {
                var stripped = line;
                foreach (var name in AllowedLatinNames) {
                    stripped = stripped.Replace(name, string.Empty, StringComparison.Ordinal);
                }

                Assert.False(
                    Regex.IsMatch(stripped, "[A-Za-z]", RegexOptions.CultureInvariant),
                    $"выпуск {version}: список ведётся по-русски, латиницей остаются только имена " +
                    $"({string.Join(", ", AllowedLatinNames)}). Перепишите пункт: {line}");
            }
        }

        [Fact]
        public void ПунктыКороткиеИЗаконченные() {
            foreach (var (version, line) in AllLines()) {
                Assert.True(
                    line.Length <= 160,
                    $"выпуск {version}: пункт длиннее 160 символов — это уже абзац, разбейте его: {line}");
                Assert.True(
                    line.EndsWith('.') || line.EndsWith('!'),
                    $"выпуск {version}: пункт — законченная фраза и кончается точкой: {line}");
                Assert.Equal(line.Trim(), line);
            }
        }

        /// <summary>Все пункты всех выпусков вместе с версией, к которой они относятся.</summary>
        private static IEnumerable<(string Version, string Line)> AllLines() {
            foreach (var release in ChangelogData.Releases) {
                foreach (var line in release.Changes) {
                    yield return (release.Version, line);
                }
            }
        }

        /// <summary>
        /// Версия, которую объявляет исходник. Читаем сам csproj, а не версию сборки:
        /// тестовый прогон может идти с подставленным -p:Version, а обещание дано именно
        /// про то, что записано в репозитории.
        /// </summary>
        private static string DeclaredVersion() {
            var csproj = Path.Combine(FindRepoRoot(), "launcher", "ChillHub", "ChillHub.csproj");
            var text = File.ReadAllText(csproj);
            var match = Regex.Match(text, @"<Version>([^<]+)</Version>", RegexOptions.CultureInvariant);
            Assert.True(match.Success, $"в {csproj} не нашли <Version> — проверьте, чем теперь объявляется версия");
            return match.Groups[1].Value.Trim();
        }

        /// <summary>
        /// Поднимается от каталога сборки тестов до корня репозитория по метке
        /// (CLAUDE.md + каталог launcher/), а не по фиксированной глубине.
        /// </summary>
        private static string FindRepoRoot() {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null) {
                if (File.Exists(Path.Combine(current.FullName, "CLAUDE.md")) &&
                    Directory.Exists(Path.Combine(current.FullName, "launcher"))) {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"не нашли корень репозитория, поднимаясь от {AppContext.BaseDirectory}");
        }
    }
}
