// <copyright file="GameStateResolverTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Game;

    using Xunit;

    /// <summary>
    /// Состояние игры на странице игры и подписи, которые из него следуют.
    /// <para>
    /// Кнопка действия — единственный способ что-то сделать с игрой, и её смысл целиком
    /// задаётся этим расчётом: «Установить» вместо «Обновить» отправит пользователя качать
    /// сборку заново, а «Установлена» на полусобранной папке оставит игру нерабочей и
    /// без единого намёка на причину. Ни один из этих случаев без окна раньше не ловился.
    /// </para>
    /// </summary>
    public class GameStateResolverTests {
        /// <summary>
        /// Маркер незавершённого обновления перевешивает всё остальное: на диске лежит
        /// смесь двух версий, и предлагать «Играть» по такой папке нельзя.
        /// </summary>
        [Theory]
        [InlineData(true, "1.0.0", "1.0.0", false)]
        [InlineData(false, "", "", false)]
        [InlineData(true, "", "2.0.0", true)]
        public void НезавершённоеОбновлениеВажнееОстальныхПризнаков(bool hasFiles, string local, string latest, bool needsUpdate) {
            Assert.Equal(GameState.Unfinished, GameStateResolver.Compute(true, hasFiles, local, latest, needsUpdate));
        }

        /// <summary>Ни файлов, ни маркера версии — игра не установлена.</summary>
        [Fact]
        public void БезФайловИМаркераИграНеУстановлена() {
            Assert.Equal(GameState.NotInstalled, GameStateResolver.Compute(false, false, string.Empty, "1.0.0", false));
        }

        /// <summary>
        /// Файлы есть, а маркера нет — игра всё равно установлена. Маркер теряется при
        /// ручном копировании папки, и считать такую установку отсутствующей значит
        /// предложить скачать десятки гигабайт заново.
        /// </summary>
        [Fact]
        public void ФайлыБезМаркераСчитаютсяУстановкой() {
            Assert.Equal(GameState.Installed, GameStateResolver.Compute(false, true, string.Empty, string.Empty, false));
        }

        /// <summary>Маркер без файлов тоже считается установкой: папку могли почистить, но версия известна.</summary>
        [Fact]
        public void МаркерБезФайловСчитаетсяУстановкой() {
            Assert.Equal(GameState.Installed, GameStateResolver.Compute(false, false, "1.0.0", "1.0.0", false));
        }

        /// <summary>Совпали версии и главная страница не просила обновления — игра актуальна.</summary>
        [Fact]
        public void СовпадениеВерсийДаётУстановлена() {
            Assert.Equal(GameState.Installed, GameStateResolver.Compute(false, true, "1.2.3", "1.2.3", false));
        }

        /// <summary>Регистр в маркере версии не должен выдавать совпадающие версии за разные.</summary>
        [Fact]
        public void РегистрВерсииНеСчитаетсяРасхождением() {
            Assert.Equal(GameState.Installed, GameStateResolver.Compute(false, true, "1.2.3-RC", "1.2.3-rc", false));
        }

        /// <summary>Версии разошлись — доступно обновление, даже если главная страница молчит.</summary>
        [Fact]
        public void РасхождениеВерсийДаётОбновление() {
            Assert.Equal(GameState.UpdateAvailable, GameStateResolver.Compute(false, true, "1.0.0", "1.1.0", false));
        }

        /// <summary>
        /// Главная страница сравнивает файлы с манифестом целиком — её вердикту доверяем,
        /// даже когда маркеры версий совпадают: испорченный файл маркер не меняет.
        /// </summary>
        [Fact]
        public void ВердиктГлавнойСтраницыДаётОбновлениеПриРавныхВерсиях() {
            Assert.Equal(GameState.UpdateAvailable, GameStateResolver.Compute(false, true, "1.0.0", "1.0.0", true));
        }

        /// <summary>
        /// Неизвестная последняя версия не превращается в «доступно обновление»:
        /// список игр мог не загрузиться, а обновлять до пустоты нечего.
        /// </summary>
        [Fact]
        public void НеизвестнаяПоследняяВерсияНеТребуетОбновления() {
            Assert.Equal(GameState.Installed, GameStateResolver.Compute(false, true, "1.0.0", "   ", false));
        }

        /// <summary>Подписи состояний зафиксированы: их читает пользователь на кнопке и в статусе.</summary>
        [Fact]
        public void ПодписиСоответствуютСостоянию() {
            AssertLabels(GameState.NotInstalled, "Не установлена", "Установить");
            AssertLabels(GameState.Installed, "Установлена", "Проверить файлы");
            AssertLabels(GameState.UpdateAvailable, "Доступно обновление", "Обновить");
            AssertLabels(GameState.Unfinished, "Обновление не завершено", "Завершить обновление");
        }

        /// <summary>
        /// «Проверить файлы» стоит только на актуальной установке. Именно это состояние
        /// включает подтверждение удаления лишних файлов — перепутать его с остальными нельзя.
        /// </summary>
        [Fact]
        public void ПроверкаФайловПредлагаетсяТолькоДляУстановленной() {
            Assert.Equal("Проверить файлы", GameStateResolver.Labels(GameState.Installed).ActionText);
            Assert.NotEqual("Проверить файлы", GameStateResolver.Labels(GameState.UpdateAvailable).ActionText);
            Assert.NotEqual("Проверить файлы", GameStateResolver.Labels(GameState.Unfinished).ActionText);
            Assert.NotEqual("Проверить файлы", GameStateResolver.Labels(GameState.NotInstalled).ActionText);
        }

        private static void AssertLabels(GameState state, string expectedState, string expectedAction) {
            var labels = GameStateResolver.Labels(state);

            Assert.Equal(expectedState, labels.StateText);
            Assert.Equal(expectedAction, labels.ActionText);
        }
    }
}
