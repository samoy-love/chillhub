// <copyright file="ErrorReporterQuotaTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Постоянные квоты на отправку отчётов: автоматических (3 за 3 минуты) и ручных
    /// (5 за 5 минут). Счётчики живут в файлах, а не в памяти, — иначе перезапуск
    /// лаунчера обнулял бы их, и цикл «падение при старте → отчёт → падение» слал бы
    /// на сервер отчёт за отчётом без всякого предела.
    /// <para>
    /// Тесты подменяют содержимое настоящих файлов квот в %APPDATA% и возвращают его
    /// на место: путь задан внутри продакшн-кода и наружу не выведен.
    /// </para>
    /// </summary>
    public class ErrorReporterQuotaTests {
        /// <summary>Пока лимит не выбран, отправка разрешена и время ожидания не назначается.</summary>
        [Fact]
        public void ПервыеОтправкиРазрешены() {
            using var q = new QuotaScope();
            q.WriteGlobal(count: 0, windowStart: DateTime.UtcNow);

            for (var i = 0; i < 3; i++) {
                Assert.True(ErrorReporter.TryConsumeGlobal(out var retryAfter), $"попытка {i + 1} отклонена");
                Assert.Equal(TimeSpan.Zero, retryAfter);
            }
        }

        /// <summary>
        /// Выбранная квота автоотчётов закрывает отправку и называет срок, через который
        /// можно повторить: пользователю показывают именно его.
        /// </summary>
        [Fact]
        public void ВыбраннаяКвотаАвтоотчётовЗакрываетОтправку() {
            using var q = new QuotaScope();
            q.WriteGlobal(count: 3, windowStart: DateTime.UtcNow);

            Assert.False(ErrorReporter.TryConsumeGlobal(out var retryAfter));
            Assert.True(retryAfter > TimeSpan.Zero, "срок повтора не назван");
            Assert.True(retryAfter <= TimeSpan.FromMinutes(3), $"срок повтора {retryAfter} больше окна квоты");
        }

        /// <summary>Истёкшее окно начинается заново — квота не может запереть отправку навсегда.</summary>
        [Fact]
        public void ИстёкшееОкноОткрываетОтправкуЗаново() {
            using var q = new QuotaScope();
            q.WriteGlobal(count: 99, windowStart: DateTime.UtcNow.AddMinutes(-10));

            Assert.True(ErrorReporter.TryConsumeGlobal(out _));
        }

        /// <summary>
        /// Каждая разрешённая отправка записывается на диск: без этого счётчик не растёт
        /// и квота не срабатывает никогда.
        /// </summary>
        [Fact]
        public void СчётчикРастётНаДиске() {
            using var q = new QuotaScope();
            q.WriteGlobal(count: 0, windowStart: DateTime.UtcNow);

            ErrorReporter.TryConsumeGlobal(out _);
            ErrorReporter.TryConsumeGlobal(out _);

            Assert.Equal(2, q.ReadGlobalCount());
        }

        /// <summary>
        /// Битый файл квоты не должен запирать отправку: счётчик — вспомогательные данные,
        /// восстановить их нечем, поэтому окно начинается заново.
        /// </summary>
        [Theory]
        [InlineData("не json вовсе")]
        [InlineData("{")]
        [InlineData("")]
        public void БитыйФайлКвотыНеЗапираетОтправку(string garbage) {
            using var q = new QuotaScope();
            q.WriteRawGlobal(garbage);

            Assert.True(ErrorReporter.TryConsumeGlobal(out _));
        }

        /// <summary>Ручная квота шире автоматической: у живого человека пять попыток за пять минут.</summary>
        [Fact]
        public void РучнаяКвотаДаётПятьПопыток() {
            using var q = new QuotaScope();
            q.WriteManual(count: 0, windowStart: DateTime.UtcNow);

            for (var i = 0; i < 5; i++) {
                Assert.True(ErrorReporter.TryConsumeManual(out _), $"ручная попытка {i + 1} отклонена");
            }

            Assert.False(ErrorReporter.TryConsumeManual(out var retryAfter));
            Assert.True(retryAfter > TimeSpan.Zero);
            Assert.True(retryAfter <= TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Квоты независимы: шквал автоотчётов не должен отбирать у человека возможность
        /// написать в обратную связь — именно тогда она и нужна.
        /// </summary>
        [Fact]
        public void АвтоматическаяИРучнаяКвотыНезависимы() {
            using var q = new QuotaScope();
            q.WriteGlobal(count: 3, windowStart: DateTime.UtcNow);
            q.WriteManual(count: 0, windowStart: DateTime.UtcNow);

            Assert.False(ErrorReporter.TryConsumeGlobal(out _));
            Assert.True(ErrorReporter.TryConsumeManual(out _));
        }

        /// <summary>
        /// Квоту считают и фоновые задачи, и UI одновременно. Гонка не должна ни ронять
        /// вызывающего, ни выпускать больше разрешений, чем позволяет окно.
        /// </summary>
        [Fact]
        public async Task КвотаВыдерживаетОдновременныйДоступ() {
            using var q = new QuotaScope();
            q.WriteGlobal(count: 0, windowStart: DateTime.UtcNow);

            var granted = 0;
            var tasks = new Task[8];
            for (var i = 0; i < tasks.Length; i++) {
                tasks[i] = Task.Run(() => {
                    for (var j = 0; j < 10; j++) {
                        if (ErrorReporter.TryConsumeGlobal(out _)) {
                            Interlocked.Increment(ref granted);
                        }
                    }
                });
            }

            await Task.WhenAll(tasks);
            Assert.Equal(3, granted);
        }

        /// <summary>
        /// Повторный вызов не должен ломаться, если каталога квот ещё нет: так выглядит
        /// первый запуск лаунчера, и именно там падение особенно вероятно.
        /// </summary>
        [Fact]
        public void ОтсутствующийФайлКвотыСоздаётсяСам() {
            using var q = new QuotaScope();
            q.DeleteBoth();

            Assert.True(ErrorReporter.TryConsumeGlobal(out _));
            Assert.Equal(1, q.ReadGlobalCount());
        }

        /// <summary>
        /// Сохраняет настоящие файлы квот и возвращает их на место: тест не должен
        /// ни списать квоту разработчика, ни оставить после себя чужой счётчик.
        /// </summary>
        private sealed class QuotaScope : IDisposable {
            private readonly string? savedGlobal;
            private readonly string? savedManual;

            internal QuotaScope() {
                this.savedGlobal = Read(GlobalPath);
                this.savedManual = Read(ManualPath);
            }

            private static string Dir => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");

            private static string GlobalPath => Path.Combine(Dir, "report_rl.json");

            private static string ManualPath => Path.Combine(Dir, "report_manual_rl.json");

            internal void WriteGlobal(int count, DateTime windowStart) => WriteState(GlobalPath, count, windowStart);

            internal void WriteManual(int count, DateTime windowStart) => WriteState(ManualPath, count, windowStart);

            internal void WriteRawGlobal(string content) {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(GlobalPath, content, Encoding.UTF8);
            }

            internal int ReadGlobalCount() {
                var json = Read(GlobalPath);
                if (json == null) {
                    return -1;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("Count").GetInt32();
            }

            internal void DeleteBoth() {
                foreach (var p in new[] { GlobalPath, ManualPath }) {
                    if (File.Exists(p)) {
                        File.Delete(p);
                    }
                }
            }

            public void Dispose() {
                Restore(GlobalPath, this.savedGlobal);
                Restore(ManualPath, this.savedManual);
            }

            private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

            private static void WriteState(string path, int count, DateTime windowStart) {
                Directory.CreateDirectory(Dir);
                var json = System.Text.Json.JsonSerializer.Serialize(new {
                    Count = count,
                    WindowStartUtc = windowStart,
                });
                File.WriteAllText(path, json, Encoding.UTF8);
            }

            private static void Restore(string path, string? content) {
                try {
                    if (content == null) {
                        if (File.Exists(path)) {
                            File.Delete(path);
                        }

                        return;
                    }

                    File.WriteAllText(path, content, Encoding.UTF8);
                }
                catch {
                    // Вернуть файл не удалось — прогон из-за этого валить не нужно.
                }
            }
        }
    }
}
