// <copyright file="UpdateStatusTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Файл исхода самообновления.
    /// <para>
    /// Апдейтер завершается уже ПОСЛЕ перезапуска лаунчера, поэтому его код возврата
    /// не читает никто. Этот файл — единственный способ рассказать пользователю,
    /// почему обновление не применилось. Потерянное или неразобранное сообщение
    /// означает молчаливый откат к старой версии без всяких объяснений.
    /// </para>
    /// </summary>
    public class UpdateStatusTests {
        /// <summary>Записанное состояние читается обратно целиком.</summary>
        [Fact]
        public void СостояниеЧитаетсяОбратноЦеликом() {
            using var dir = new TempDir();
            var status = new UpdateStatus {
                Outcome = "copy-errors",
                ExitCode = 2,
                Version = "1.4.2",
                Message = "не удалось заменить ChillHub.dll",
                LogPath = @"C:\Temp\updater.log",
            };

            UpdateStatus.Write(dir.Root, status);
            var back = UpdateStatus.TryRead(dir.Root)!;

            Assert.Equal("copy-errors", back.Outcome);
            Assert.Equal(2, back.ExitCode);
            Assert.Equal("1.4.2", back.Version);
            Assert.Equal("не удалось заменить ChillHub.dll", back.Message);
            Assert.Equal(@"C:\Temp\updater.log", back.LogPath);
            Assert.False(string.IsNullOrWhiteSpace(back.TimeUtc));
        }

        /// <summary>Успехом считается только outcome=ok, и регистр значения роли не играет.</summary>
        [Theory]
        [InlineData("ok", true)]
        [InlineData("OK", true)]
        [InlineData("Ok", true)]
        [InlineData("copy-errors", false)]
        [InlineData("integrity", false)]
        [InlineData("fatal", false)]
        [InlineData("", false)]
        public void УспехомСчитаетсяТолькоOk(string outcome, bool success) {
            Assert.Equal(success, new UpdateStatus { Outcome = outcome }.IsSuccess);
        }

        /// <summary>
        /// Многострочное сообщение не разваливает файл на лишние ключи: значения
        /// однострочные, перевод строки экранируется и восстанавливается при чтении.
        /// </summary>
        [Fact]
        public void МногострочноеСообщениеПереживаетКруг() {
            using var dir = new TempDir();
            const string message = "первая строка\nвторая строка\nтретья";

            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "fatal", Message = message });
            var back = UpdateStatus.TryRead(dir.Root)!;

            Assert.Equal(message, back.Message);
            Assert.Equal("fatal", back.Outcome);
        }

        /// <summary>
        /// Обратные слеши в путях Windows переживают круг: без экранирования
        /// «C:\new\log.txt» вернулся бы с переводом строки вместо «\n».
        /// </summary>
        [Fact]
        public void ОбратныеСлешиВПутиПереживаютКруг() {
            using var dir = new TempDir();
            const string logPath = @"C:\Users\name\AppData\new\log.txt";

            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "ok", LogPath = logPath });

            Assert.Equal(logPath, UpdateStatus.TryRead(dir.Root)!.LogPath);
        }

        /// <summary>Возврат каретки в сообщении не оставляет «\r» в разобранном значении.</summary>
        [Fact]
        public void ВозвратКареткиНеПопадаетВЗначение() {
            using var dir = new TempDir();

            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "ok", Message = "строка\r\nвторая" });

            Assert.Equal("строка\nвторая", UpdateStatus.TryRead(dir.Root)!.Message);
        }

        /// <summary>Файла нет — состояния нет. Это норма: обновления просто не было.</summary>
        [Fact]
        public void ОтсутствующийФайлДаётNull() {
            using var dir = new TempDir();

            Assert.Null(UpdateStatus.TryRead(dir.Root));
        }

        /// <summary>
        /// Мусор вместо файла состояния не роняет запуск лаунчера: лучше промолчать
        /// об исходе обновления, чем не запуститься вовсе.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("совершенно посторонний текст")]
        [InlineData("=без ключа")]
        [InlineData("\n\n\n")]
        public void МусорВместоСостоянияНеРоняетЧтение(string garbage) {
            using var dir = new TempDir();
            File.WriteAllText(UpdateStatus.PathIn(dir.Root), garbage, new UTF8Encoding(false));

            Assert.Null(UpdateStatus.TryRead(dir.Root));
        }

        /// <summary>Нечисловой код возврата не роняет разбор — остальные поля важнее.</summary>
        [Fact]
        public void НечисловойКодВозвратаНеРоняетРазбор() {
            using var dir = new TempDir();
            File.WriteAllText(
                UpdateStatus.PathIn(dir.Root),
                "outcome=fatal\nexit=не число\nmessage=всё плохо\n",
                new UTF8Encoding(false));

            var back = UpdateStatus.TryRead(dir.Root)!;
            Assert.Equal("fatal", back.Outcome);
            Assert.Equal(0, back.ExitCode);
            Assert.Equal("всё плохо", back.Message);
        }

        /// <summary>Состояние живёт рядом с маркером версии, в каталоге установки.</summary>
        [Fact]
        public void СостояниеЛежитВКаталогеУстановки() {
            Assert.Equal(
                Path.Combine(@"C:\App", UpdateStatus.FileName),
                UpdateStatus.PathIn(@"C:\App"));
        }

        /// <summary>После показа пользователю состояние убирается, и повторно не всплывает.</summary>
        [Fact]
        public void ПоказанноеСостояниеУбирается() {
            using var dir = new TempDir();
            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "ok" });

            UpdateStatus.Clear(dir.Root);

            Assert.Null(UpdateStatus.TryRead(dir.Root));
        }

        /// <summary>Повторная уборка ничего не ломает: лаунчер может позвать её на всякий случай.</summary>
        [Fact]
        public void ПовторнаяУборкаБезопасна() {
            using var dir = new TempDir();

            UpdateStatus.Clear(dir.Root);
            UpdateStatus.Clear(dir.Root);

            Assert.Null(UpdateStatus.TryRead(dir.Root));
        }

        /// <summary>Новая запись поверх старой не смешивается с ней.</summary>
        [Fact]
        public void НоваяЗаписьЗамещаетСтарую() {
            using var dir = new TempDir();
            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "fatal", ExitCode = 3, Message = "старое" });

            UpdateStatus.Write(dir.Root, new UpdateStatus { Outcome = "ok", ExitCode = 0 });

            var back = UpdateStatus.TryRead(dir.Root)!;
            Assert.Equal("ok", back.Outcome);
            Assert.Equal(0, back.ExitCode);
            Assert.Equal(string.Empty, back.Message);
        }

        /// <summary>
        /// Недоступный каталог не роняет апдейтер: файл состояния — диагностика,
        /// а не часть обновления. Сбой уходит в лог и на этом всё.
        /// </summary>
        [Fact]
        public void НедоступныйКаталогНеРоняетЗапись() {
            var reported = new List<string>();

            UpdateStatus.Write("\0недопустимый путь", new UpdateStatus { Outcome = "ok" }, reported.Add);

            Assert.NotEmpty(reported);
        }
    }
}
