// <copyright file="RedactAndFormatTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Обезличивание диагностики и форматирование, которое видит пользователь.
    /// <para>
    /// Редакция — единственное, что стоит между логами и именем пользователя Windows,
    /// уезжающим на сервер. Она применяется и к бандлу, и к тексту исключения в
    /// автоотчёте; тихая поломка здесь означает утечку персональных данных, которую
    /// никто не заметит, потому что отчёт всё равно доходит.
    /// </para>
    /// </summary>
    public class RedactAndFormatTests {
        /// <summary>Путь к профилю заменяется плейсхолдером.</summary>
        [Fact]
        public void ПутьКПрофилюЗаменяется() {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(string.IsNullOrWhiteSpace(profile), "тест бессмыслен без профиля");

            var text = $"не удалось открыть {profile}\\ChillHub\\config.json";
            var red = Diagnostics.Redact(text);

            Assert.DoesNotContain(profile, red, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", red, StringComparison.Ordinal);
        }

        /// <summary>
        /// В JSON путь приходит с удвоенными слешами — форма, в которой конфиг попадает
        /// в бандл. Её тоже нужно закрывать, иначе имя утекает именно оттуда.
        /// </summary>
        [Fact]
        public void ПутьСЭкранированнымиСлешамиТожеЗаменяется() {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var escaped = profile.Replace("\\", "\\\\");

            var red = Diagnostics.Redact($"{{ \"GamesPath\": \"{escaped}\\\\Games\" }}");

            Assert.DoesNotContain(escaped, red, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", red, StringComparison.Ordinal);
        }

        /// <summary>Имя пользователя вычищается и отдельно от пути — оно встречается в текстах ошибок.</summary>
        [Fact]
        public void ИмяПользователяЗаменяетсяОтдельно() {
            var user = Environment.UserName;
            if (string.IsNullOrWhiteSpace(user) || user.Length < 3) {
                return; // правило намеренно не трогает слишком короткие имена — см. ниже
            }

            var red = Diagnostics.Redact($"процесс запущен от {user}");
            Assert.DoesNotContain(user, red, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USER%", red, StringComparison.Ordinal);
        }

        /// <summary>Регистр не спасает от редакции: пути приходят в разном написании.</summary>
        [Fact]
        public void РедакцияНеЗависитОтРегистра() {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var red = Diagnostics.Redact(profile.ToUpperInvariant() + "\\logs");
            Assert.Contains("%USERPROFILE%", red, StringComparison.Ordinal);
        }

        /// <summary>Пустой ввод не должен ронять сбор диагностики.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ПустойВводНеРоняет(string? text) {
            Assert.Equal(string.Empty, Diagnostics.Redact(text!));
        }

        /// <summary>Текст без персональных данных проходит без изменений.</summary>
        [Fact]
        public void ЧистыйТекстНеМеняется() {
            const string text = "Манифест отвергнут: пустой каталог #0";
            Assert.Equal(text, Diagnostics.Redact(text));
        }

        /// <summary>Размер показывается человеку, а не в байтах.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(512)]
        [InlineData(1024)]
        [InlineData(1024L * 1024)]
        [InlineData(1024L * 1024 * 1024)]
        [InlineData(long.MaxValue)]
        public void РазмерФорматируетсяБезИсключений(long bytes) {
            var s = HomeFormat.FormatSize(bytes);
            Assert.False(string.IsNullOrWhiteSpace(s));
        }

        /// <summary>Отрицательный размер — это ошибка расчёта, но падать из-за неё нельзя.</summary>
        [Fact]
        public void ОтрицательныйРазмерНеРоняет() {
            Assert.False(string.IsNullOrWhiteSpace(HomeFormat.FormatSize(-1)));
        }

        /// <summary>Оставшееся время не должно превращаться в «NaN» или «∞» на экране.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(59)]
        [InlineData(3600)]
        [InlineData(86400)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-5)]
        public void ОставшеесяВремяВсегдаЧитаемо(double seconds) {
            var s = HomeFormat.FormatEta(seconds);
            Assert.DoesNotContain("NaN", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Infinity", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("∞", s, StringComparison.Ordinal);
        }

        /// <summary>Русское склонение дней: 1 день, 2 дня, 5 дней.</summary>
        [Theory]
        [InlineData(1, "день")]
        [InlineData(2, "дня")]
        [InlineData(5, "дней")]
        [InlineData(11, "дней")]
        [InlineData(21, "день")]
        [InlineData(22, "дня")]
        [InlineData(25, "дней")]
        [InlineData(101, "день")]
        public void СклонениеДнейПоРусски(int n, string expected) {
            Assert.Equal(expected, HomeFormat.PluralizeDayRu(n));
        }

        /// <summary>
        /// Имя файла из внешних данных попадает на диск: запрещённые символы обязаны
        /// исчезнуть, иначе запись упадёт или уедет не туда.
        /// </summary>
        [Theory]
        [InlineData("обычное.txt")]
        [InlineData("с:двоеточием.txt")]
        [InlineData("со/слешем.txt")]
        [InlineData("с\\обратным.txt")]
        [InlineData("со*звёздочкой.txt")]
        [InlineData("с?вопросом.txt")]
        [InlineData("с\"кавычкой.txt")]
        [InlineData("с<угловыми>.txt")]
        [InlineData("с|трубой.txt")]
        public void ИмяФайлаОчищаетсяОтЗапрещённыхСимволов(string name) {
            var safe = HomeFormat.SanitizeFileName(name);
            Assert.DoesNotContain(safe, Path.GetInvalidFileNameChars().Length == 0 ? "\u0000" : string.Empty, StringComparison.Ordinal);
            foreach (var bad in Path.GetInvalidFileNameChars()) {
                Assert.DoesNotContain(bad.ToString(), safe, StringComparison.Ordinal);
            }
        }

        /// <summary>Пустое имя не должно давать пустой путь — файл надо куда-то положить.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("///")]
        public void ПустоеИмяФайлаДаётНепустойРезультат(string name) {
            Assert.False(string.IsNullOrWhiteSpace(HomeFormat.SanitizeFileName(name)));
        }

        /// <summary>
        /// Сетевой путь начинается с двух слешей, и это НЕ повтор разделителя:
        /// схлопывание сломало бы доступ к сетевой папке с играми.
        /// </summary>
        [Fact]
        public void СетевойПутьСохраняетДваВедущихСлеша() {
            var norm = HomeFormat.NormalizeWindowsPath(@"\\server\share\Games");
            Assert.StartsWith(@"\\", norm, StringComparison.Ordinal);
        }

        /// <summary>Повторяющиеся разделители внутри пути схлопываются.</summary>
        [Fact]
        public void ПовторыРазделителейСхлопываются() {
            var norm = HomeFormat.NormalizeWindowsPath(@"D:\\Games\\\ChillHub");
            Assert.DoesNotContain(@"\\\", norm, StringComparison.Ordinal);
        }

        /// <summary>Нормализация пустого пути не роняет UI настроек.</summary>
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void НормализацияПустогоПутиБезопасна(string? path) {
            Assert.NotNull(HomeFormat.NormalizeWindowsPath(path!));
            Assert.NotNull(HomeFormat.NormalizeDisplayPath(path!));
        }
    }
}
