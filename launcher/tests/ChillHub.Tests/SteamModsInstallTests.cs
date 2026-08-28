// <copyright file="SteamModsInstallTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core;
    using ChillHub.Core.Home;
    using ChillHub.Core.Mods;

    using Xunit;

    /// <summary>
    /// Установка модпака в копию игры из Steam: решение о пункте меню, текст вопроса
    /// и разбор итога.
    /// <para>
    /// Всё это — про чужую папку. Модпак пишется прямо в установку Steam, поэтому цена
    /// ошибки здесь не «неудобно»: пункт, показанный не той игре, ведёт в тупик, вопрос
    /// без имени найденной папки просит согласиться неизвестно на что, а «ошибка» вместо
    /// причины поиска не даёт человеку сделать следующий шаг. Ни одну из этих строк не
    /// проверить через живое окно — потому они и вынесены из страницы.
    /// </para>
    /// </summary>
    public class SteamModsInstallTests {
        /// <summary>Игра с опубликованным модпаком и известным AppID.</summary>
        private static ModsInfo Ready() => new() {
            HasLatest = true,
            Version = "ASTeam-LethalReloaded-2.2.12",
            DisplayName = "Lethal Reloaded",
            DisplayVersion = "2.2.12",
            SteamAppId = "1966720",
            SteamFolder = "How to Fish/How to Fish",
        };

        // Тестов пункта контекстного меню здесь больше нет: пункт убран.
        // Установка модов в копию Steam теперь живёт строкой «Steam · с модами» в
        // меню кнопки «Играть», и её состояния проверяет ModsLaunchTests.

        // ---- Вопрос перед установкой ----

        /// <summary>
        /// В вопросе обязана быть НАЙДЕННАЯ папка: библиотек Steam бывает несколько, и
        /// без имени папки человек соглашается неизвестно на какую копию. Показывается
        /// она в том же виде, что и остальные пути в интерфейсе — с прямыми слешами.
        /// </summary>
        [Fact]
        public void ВопросНазываетНайденнуюПапку() {
            var text = SteamModsInstall.BuildConfirmText("How to Fish", Ready(), @"D:\SteamLibrary\steamapps\common\How to Fish");

            Assert.Contains("D:/SteamLibrary/steamapps/common/How to Fish", text, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Предупреждение про обновление игры в Steam — обязательная часть вопроса:
        /// Steam возвращает свои файлы поверх модов, и после его обновления игра
        /// перестаёт запускаться до повторной установки модпака.
        /// </summary>
        [Fact]
        public void ВопросПредупреждаетПроОбновлениеВSteam() {
            var text = SteamModsInstall.BuildConfirmText("How to Fish", Ready(), @"D:\Games\How to Fish");

            Assert.Contains("Обновление игры в Steam", text, System.StringComparison.Ordinal);
        }

        /// <summary>Название модпака в вопросе — чтобы было видно, что именно ставят.</summary>
        [Fact]
        public void ВопросНазываетМодпак() {
            var text = SteamModsInstall.BuildConfirmText("How to Fish", Ready(), @"D:\Games\How to Fish");

            Assert.Contains("Lethal Reloaded 2.2.12", text, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Пустые название игры и папка приходят только из гонки, но вопрос всё равно
        /// обязан остаться связным текстом, а не «Поставить моды в копию  из Steam?».
        /// </summary>
        [Fact]
        public void ВопросБезНазванияИПапкиОстаётсяСвязным() {
            var text = SteamModsInstall.BuildConfirmText(null, null, null);

            Assert.Contains("Поставить моды в копию игры из Steam?", text, System.StringComparison.Ordinal);
            Assert.Contains("Обновление игры в Steam", text, System.StringComparison.Ordinal);
        }

        // ---- Почему копию не нашли ----

        /// <summary>
        /// У каждой ступени поиска свой следующий шаг, поэтому и текст у каждой свой.
        /// «Ошибка» на все случаи не лечится ничем.
        /// <para>
        /// Один Fact на все ступени, а не Theory: <see cref="SteamLookup"/> объявлен
        /// internal, и открытый метод теста не имеет права взять его параметром.
        /// </para>
        /// </summary>
        [Fact]
        public void КаждаяПричинаНеудачиОбъясненаСвоимТекстом() {
            var failures = new[] {
                SteamLookup.SteamNotInstalled,
                SteamLookup.NoLibraries,
                SteamLookup.GameNotInstalled,
                SteamLookup.FolderMissing,
                SteamLookup.NoAppId,
            };

            foreach (var outcome in failures) {
                var text = SteamModsInstall.DescribeLookupFailure(outcome, "How to Fish");

                Assert.NotEmpty(text);
                Assert.DoesNotContain("Ошибка", text, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Тексты разных причин не совпадают — иначе объяснение ничего не даёт.</summary>
        [Fact]
        public void ПричиныНеудачиРазличаются() {
            var notInstalled = SteamModsInstall.DescribeLookupFailure(SteamLookup.SteamNotInstalled, "How to Fish");
            var noGame = SteamModsInstall.DescribeLookupFailure(SteamLookup.GameNotInstalled, "How to Fish");
            var noFolder = SteamModsInstall.DescribeLookupFailure(SteamLookup.FolderMissing, "How to Fish");

            Assert.NotEqual(notInstalled, noGame);
            Assert.NotEqual(noGame, noFolder);
        }

        /// <summary>Игра, которой нет в Steam, называется по имени: список игр длинный.</summary>
        [Fact]
        public void ПричинаНазываетИгру() {
            var text = SteamModsInstall.DescribeLookupFailure(SteamLookup.GameNotInstalled, "How to Fish");

            Assert.Contains("How to Fish", text, System.StringComparison.Ordinal);
        }

        /// <summary>Удачный поиск объяснять нечем — и незачем.</summary>
        [Fact]
        public void УдачныйПоискНеОбъясняется() {
            Assert.Empty(SteamModsInstall.DescribeLookupFailure(SteamLookup.Found, "How to Fish"));
        }

        // ---- Итог установки ----

        /// <summary>
        /// Успешная установка называет версию и объём: «готово» после полутора
        /// гигабайт трафика выглядит как отказ.
        /// </summary>
        [Fact]
        public void УстановкаНазываетВерсиюИОбъём() {
            var result = new ModsSyncResult(
                ModsSyncOutcome.Installed, "ASTeam-LethalReloaded-2.2.12", 5L * 1024 * 1024, 0, string.Empty);

            var text = SteamModsInstall.DescribeResult(result, "How to Fish");

            Assert.Contains("ASTeam-LethalReloaded-2.2.12", text, System.StringComparison.Ordinal);
            Assert.Contains("МБ", text, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Когда скачивать было нечего, про объём не пишем: «скачано 0 Б» читается как сбой.
        /// </summary>
        [Fact]
        public void УстановкаБезТрафикаНеПишетПроНольБайт() {
            var result = new ModsSyncResult(ModsSyncOutcome.Installed, "v1", 0, 3, string.Empty);

            Assert.DoesNotContain("скачано", SteamModsInstall.DescribeResult(result, "How to Fish"), System.StringComparison.Ordinal);
        }

        /// <summary>«Всё уже стоит» — тоже успех, и говорить о нём надо именно так.</summary>
        [Fact]
        public void АктуальныеМодыОписаныКакУспех() {
            var result = new ModsSyncResult(ModsSyncOutcome.UpToDate, "v1", 0, 0, string.Empty);

            var text = SteamModsInstall.DescribeResult(result, "How to Fish");

            Assert.Contains("актуальны", text, System.StringComparison.Ordinal);
        }

        /// <summary>Сбой показывает объяснение от службы, а не общее «не получилось».</summary>
        [Fact]
        public void СбойПоказываетСообщениеСлужбы() {
            var result = new ModsSyncResult(
                ModsSyncOutcome.Failed, "v1", 0, 0, "Сервер прислал некорректный манифест модпака.");

            Assert.Equal(
                "Сервер прислал некорректный манифест модпака.",
                SteamModsInstall.DescribeResult(result, "How to Fish"));
        }

        /// <summary>Сбой без объяснения всё равно не оставляет пользователя с пустой строкой.</summary>
        [Fact]
        public void СбойБезСообщенияПолучаетЗапаснойТекст() {
            var result = new ModsSyncResult(ModsSyncOutcome.Failed, "v1", 0, 0, string.Empty);

            Assert.NotEmpty(SteamModsInstall.DescribeResult(result, "How to Fish"));
        }

        /// <summary>Модпака нет — итог честно говорит, что ставить было нечего.</summary>
        [Fact]
        public void ОтсутствиеМодпакаОписаноОтдельно() {
            var result = new ModsSyncResult(ModsSyncOutcome.NoModpack, string.Empty, 0, 0, string.Empty);

            Assert.Contains("нечего", SteamModsInstall.DescribeResult(result, "How to Fish"), System.StringComparison.Ordinal);
        }
    }
}
