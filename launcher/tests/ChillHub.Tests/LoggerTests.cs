// <copyright file="LoggerTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Text;

    using ChillHub.Core.Logging;

    using Xunit;

    /// <summary>
    /// Проверки логгера клиента.
    /// <para>
    /// Логгер держит файл открытым между записями: открытие файла стоит ~0,15 мс, и на
    /// путях, где строка пишется на каждый файл сборки, это превращалось в минуты. Плата
    /// за это — живой дескриптор на запись, а он ломает всё, что читает лог обычным
    /// <c>File.ReadAllBytes</c>. Ровно эти два свойства — что строка доезжает до диска и
    /// что лог при этом читается — здесь и закреплены.
    /// </para>
    /// <para>
    /// Тесты идут последовательно внутри класса, но подмена каталога у логгера
    /// процессная (см. <c>Logger.OverrideForTests</c>): на время теста в подставной
    /// каталог пишет весь процесс.
    /// </para>
    /// </summary>
    public class LoggerTests {
        /// <summary>Строка доезжает до файла, а не теряется в буфере.</summary>
        [Fact]
        public void СтрокаПопадаетВФайлСразу() {
            using var dir = new TempDir();
            using (Logger.OverrideForTests(dir.Root)) {
                Logger.Info("проверка записи");

                // Читаем, не закрывая логгер: строка обязана быть на диске уже сейчас,
                // иначе разбор падения не увидит последних записей перед крахом.
                Assert.Contains("проверка записи", ReadShared(Logger.LogFilePath));
            }
        }

        /// <summary>
        /// Лог читается, пока логгер держит файл открытым.
        /// <para>
        /// Это главный риск перехода на постоянный дескриптор: <c>File.ReadAllBytes</c>
        /// просит <see cref="FileShare.Read"/>, то есть запрещает запись всем остальным,
        /// и падает с «файл занят». В диагностике падение молча съедалось бы в catch,
        /// превращая логи в отчёте в «(read error)» — то есть отчёты приходили бы пустыми
        /// ровно тогда, когда они нужны.
        /// </para>
        /// </summary>
        [Fact]
        public void ДиагностикаЧитаетЛогПриОткрытомФайле() {
            using var dir = new TempDir();
            using (Logger.OverrideForTests(dir.Root)) {
                // Метка уникальна на прогон: в каталог во время подмены пишет весь
                // процесс, и привязываться к содержимому файла целиком нельзя.
                var marker = "маркер-для-диагностики-" + Guid.NewGuid().ToString("N");
                Logger.Info(marker);
                Assert.Contains(marker, ReadShared(Logger.LogFilePath));

                var bundle = ChillHub.Core.Diagnostics.Build();

                // Если бы диагностика читала лог без разделения записи, строки бы не было:
                // чтение упало бы с «файл занят» и молча превратилось в «(read error)».
                Assert.Contains(marker, bundle.LogsMarkdown);
            }
        }

        /// <summary>
        /// Переполненный файл уезжает в архив, а не растёт бесконечно.
        /// <para>
        /// Ротация переименовывает файл, а переименовать открытый нельзя — поэтому поток
        /// закрывается до неё и открывается заново уже на новый client.log. Без этого
        /// логгер после первой же ротации писал бы в архив.
        /// </para>
        /// </summary>
        [Fact]
        public void ПереполненныйЛогУезжаетВАрхив() {
            using var dir = new TempDir();
            using (Logger.OverrideForTests(dir.Root)) {
                var padding = new string('ы', 2000);
                for (var i = 0; i < 3000; i++) {
                    Logger.Info(padding);
                }

                var archive = Path.Combine(dir.Root, "client.1.log");
                Assert.True(File.Exists(archive), "переполненный лог должен уехать в client.1.log");

                // После ротации пишем именно в новый файл, а не в архив
                Logger.Info("строка после ротации");
                Assert.Contains("строка после ротации", ReadShared(Logger.LogFilePath));
                Assert.DoesNotContain("строка после ротации", ReadShared(archive));
            }
        }

        /// <summary>Чтение файла, который кто-то держит открытым на запись.</summary>
        private static string ReadShared(string path) {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, new UTF8Encoding(false));
            return reader.ReadToEnd();
        }
    }
}
