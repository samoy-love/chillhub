// <copyright file="SelfUpdateDownloadOutcomeTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.SelfUpdate;

    using Xunit;

    /// <summary>
    /// Признак «пакет скачан» в результате шага загрузки.
    /// <para>
    /// Окно обновления заходит в загрузку только когда пакета ещё нет, и уходит в
    /// применение единственным условием — этим признаком. Поэтому он обязан быть
    /// истинным ровно для одного исхода: если хоть один отказ загрузки начнёт
    /// выдавать себя за скачанный пакет, апдейтер запустится на пустом каталоге.
    /// </para>
    /// <para>
    /// Тест закрепляет ОПРЕДЕЛЕНИЕ признака и ничего больше. Про новый член
    /// перечисления он не спасает: неизвестный исход даст «не Ready» и в проверяемом
    /// коде, и в самой проверке. Настоящий сценарий — что окно ставит признак только
    /// на успешной ветке — живёт в UpdateWindow.xaml.cs и без WPF не проверяется.
    /// </para>
    /// </summary>
    public class SelfUpdateDownloadOutcomeTests {
        /// <summary>Готовый пакет — единственный исход, ведущий к применению.</summary>
        [Fact]
        public void СкачаннымСчитаетсяТолькоГотовыйПакет() {
            foreach (SelfUpdateDownloadResult result in Enum.GetValues(typeof(SelfUpdateDownloadResult))) {
                var download = new SelfUpdateDownload { Result = result };
                Assert.Equal(result == SelfUpdateDownloadResult.Ready, download.Downloaded);
            }
        }
    }
}
