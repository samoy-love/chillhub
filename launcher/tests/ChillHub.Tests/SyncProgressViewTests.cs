// <copyright file="SyncProgressViewTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Game;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Строка прогресса на странице игры.
    /// <para>
    /// Установка сборки идёт десятки минут, и всё это время единственное, что видит
    /// пользователь, — прогресс-бар и строка со скоростью. Замерший индикатор
    /// неотличим от зависшего лаунчера, поэтому проверяется, что каждая стадия
    /// переводит бар в правильный режим и что деление на ноль в расчёте скорости
    /// не превращает строку в «∞» или «NaN».
    /// </para>
    /// </summary>
    public class SyncProgressViewTests {
        /// <summary>Проверка файлов идёт неизвестно сколько — бар обязан быть «бегущим».</summary>
        [Fact]
        public void ПроверкаФайловДаётБегущийИндикатор() {
            var display = new SyncProgressView().Describe(Stage("Checking"), 1);

            Assert.Equal("Проверка файлов…", display.Status);
            Assert.True(display.Indeterminate);
        }

        /// <summary>Скачивание переводит бар в проценты: только тут видно, что процесс идёт.</summary>
        [Fact]
        public void СкачиваниеПоказываетПроценты() {
            var p = Stage("Downloading");
            p.TotalBytes = 1000;
            p.BytesDownloaded = 250;

            var display = new SyncProgressView().Describe(p, 1);

            Assert.Equal("Скачивание…", display.Status);
            Assert.False(display.Indeterminate);
            Assert.Equal(25.0, display.Value);
        }

        /// <summary>
        /// Скачано больше, чем обещал манифест — прогресс упирается в 100, а не рисует 137%.
        /// </summary>
        [Fact]
        public void ПрогрессНеВыходитЗаСтоПроцентов() {
            var p = Stage("Downloading");
            p.TotalBytes = 1000;
            p.BytesDownloaded = 1370;

            var display = new SyncProgressView().Describe(p, 1);

            Assert.Equal(100.0, display.Value);
        }

        /// <summary>
        /// Нулевой общий объём (сервер не назвал размер) не роняет расчёт делением на ноль
        /// и не трогает шкалу — иначе бар прыгнул бы в неизвестное положение.
        /// </summary>
        [Fact]
        public void НулевойОбъёмНеРоняетРасчёт() {
            var p = Stage("Downloading");
            p.TotalBytes = 0;
            p.BytesDownloaded = 100;

            var display = new SyncProgressView().Describe(p, 1);

            Assert.Equal("Скачивание…", display.Status);
            Assert.Null(display.Value);
            Assert.Null(display.SpeedEta);
        }

        /// <summary>Мгновенный отчёт (нулевое время) не даёт бесконечной скорости.</summary>
        [Fact]
        public void НулевоеВремяНеДаётБесконечнойСкорости() {
            var p = Stage("Downloading");
            p.TotalBytes = 1000;
            p.BytesDownloaded = 500;

            var display = new SyncProgressView().Describe(p, 0);

            Assert.DoesNotContain("∞", display.SpeedEta, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", display.SpeedEta, StringComparison.Ordinal);
        }

        /// <summary>Строка со счётчиком файлов показывает и файлы, и байты — по ней судят о застревании.</summary>
        [Fact]
        public void СтрокаФайловПоказываетИФайлыИБайты() {
            var p = Stage("Downloading");
            p.TotalBytes = 2048;
            p.BytesDownloaded = 1024;
            p.TotalFiles = 10;
            p.FilesDownloaded = 3;

            var display = new SyncProgressView().Describe(p, 1);

            Assert.StartsWith("3/10 • ", display.FilesSize, StringComparison.Ordinal);
        }

        /// <summary>
        /// Скорость сглаживается: второй отчёт с той же скоростью не должен менять
        /// показание рывком, иначе цифра прыгает на каждом обновлении.
        /// </summary>
        [Fact]
        public void СкоростьСглаживаетсяМеждуОтчётами() {
            var view = new SyncProgressView();
            var p = Stage("Downloading");
            p.TotalBytes = 100L * 1024 * 1024;

            p.BytesDownloaded = 10L * 1024 * 1024;
            var first = view.Describe(p, 1);

            p.BytesDownloaded = 90L * 1024 * 1024;
            var second = view.Describe(p, 2);

            // Мгновенная скорость выросла с 10 до 45 МБ/с — сглаженная обязана отстать.
            Assert.NotEqual(first.SpeedEta, second.SpeedEta);
            Assert.DoesNotContain("45,0", second.SpeedEta, StringComparison.Ordinal);
            Assert.DoesNotContain("45.0", second.SpeedEta, StringComparison.Ordinal);
        }

        /// <summary>Сброс возвращает сглаживание к нулю: новая операция не наследует скорость прошлой.</summary>
        [Fact]
        public void СбросЗабываетСкоростьПрошлойОперации() {
            var view = new SyncProgressView();
            var p = Stage("Downloading");
            p.TotalBytes = 100L * 1024 * 1024;
            p.BytesDownloaded = 90L * 1024 * 1024;
            var fast = view.Describe(p, 1);

            view.Reset();
            p.BytesDownloaded = 10L * 1024 * 1024;
            var afterReset = view.Describe(p, 1);

            Assert.NotEqual(fast.SpeedEta, afterReset.SpeedEta);
        }

        /// <summary>Проверка скачанного и применение — снова «бегущий» бар на полной шкале.</summary>
        [Theory]
        [InlineData("Verifying", "Проверка скачанного…")]
        [InlineData("Activating", "Применение…")]
        public void ЗавершающиеСтадииДержатПолнуюШкалу(string stage, string expected) {
            var display = new SyncProgressView().Describe(Stage(stage), 1);

            Assert.Equal(expected, display.Status);
            Assert.True(display.Indeterminate);
            Assert.Equal(100.0, display.Value);
            Assert.Equal(string.Empty, display.SpeedEta);
        }

        /// <summary>Завершение останавливает «бегущий» бар: иначе готовая операция выглядит незаконченной.</summary>
        [Fact]
        public void ЗавершениеОстанавливаетБегущийИндикатор() {
            var display = new SyncProgressView().Describe(Stage("Completed"), 1);

            Assert.Equal("Готово", display.Status);
            Assert.False(display.Indeterminate);
            Assert.Equal(100.0, display.Value);
        }

        /// <summary>
        /// Незнакомая стадия выводится как есть: служба синхронизации может добавить новую,
        /// и молчащая строка состояния хуже технического слова.
        /// </summary>
        [Fact]
        public void НезнакомаяСтадияПоказываетсяКакЕсть() {
            var display = new SyncProgressView().Describe(Stage("Repacking"), 1);

            Assert.Equal("Repacking", display.Status);
            Assert.Null(display.Indeterminate);
            Assert.Null(display.Value);
        }

        private static SyncProgress Stage(string stage) => new SyncProgress { Stage = stage };
    }
}
