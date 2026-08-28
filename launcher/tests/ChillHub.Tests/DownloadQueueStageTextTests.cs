// <copyright file="DownloadQueueStageTextTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using ChillHub.Core.Game;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Подпись карточки в очереди загрузок.
    /// <para>
    /// «Скачивание обновления…» на полутора гигабайтах модов — правда, но не вся:
    /// игрок ждёт обновления ИГРЫ и не понимает, почему её столько. Пометка «Моды ·»
    /// и есть ответ на этот вопрос.
    /// </para>
    /// </summary>
    public class DownloadQueueStageTextTests {
        /// <summary>Стадии игры переводятся на русский без приписки.</summary>
        [Theory]
        [InlineData("Checking", "Проверка…")]
        [InlineData("Downloading", "Скачивание обновления…")]
        [InlineData("Verifying", "Проверка файлов…")]
        [InlineData("Activating", "Применение обновления…")]
        [InlineData("Completed", "Готово")]
        public void СтадииИгрыПереводятся(string stage, string expected) {
            Assert.Equal(expected, DownloadQueue.StageText(new SyncProgress { Stage = stage }));
        }

        /// <summary>Незнакомая стадия показывается как есть, а не пустой строкой.</summary>
        [Fact]
        public void НезнакомаяСтадияПоказываетсяКакЕсть() {
            Assert.Equal("Whatever", DownloadQueue.StageText(new SyncProgress { Stage = "Whatever" }));
        }

        /// <summary>Отчёт модпака подписан его именем.</summary>
        [Fact]
        public void ОтчётМодпакаПодписан() {
            var text = DownloadQueue.StageText(new SyncProgress { Stage = "Downloading", Scope = "Моды" });

            Assert.Equal("Моды · Скачивание обновления…", text);
        }
    }
}
