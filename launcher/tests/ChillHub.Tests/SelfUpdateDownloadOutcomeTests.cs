// <copyright file="SelfUpdateDownloadOutcomeTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.SelfUpdate;

    using Xunit;

    /// <summary>
    /// A4. Признак «пакет скачан» в результате шага загрузки.
    /// <para>
    /// Окно обновления заходит в загрузку только когда пакета ещё нет, и уходит в
    /// применение единственным условием — этим признаком. Поэтому он обязан быть
    /// истинным ровно для одного исхода: если хоть один отказ загрузки начнёт
    /// выдавать себя за скачанный пакет, апдейтер запустится на пустом каталоге и
    /// снесёт установку. Новый исход в перечислении по умолчанию должен считаться
    /// отказом — тест перебирает перечисление целиком, чтобы это не забыли.
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

        /// <summary>
        /// «Уже актуально» — не отказ сети, но и не пакет: окну нечего применять,
        /// иначе оно перезапустит лаунчер ради нулевого обновления.
        /// </summary>
        [Fact]
        public void УжеАктуальнаяВерсияНеСчитаетсяСкачаннымПакетом() {
            var download = new SelfUpdateDownload { Result = SelfUpdateDownloadResult.AlreadyUpToDate };

            Assert.False(download.Downloaded);
            Assert.Null(download.TempRoot);
            Assert.Null(download.WorkDir);
        }
    }
}
