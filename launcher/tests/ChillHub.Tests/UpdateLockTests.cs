// <copyright file="UpdateLockTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Update;

    using Xunit;

    /// <summary>
    /// Замок на каталог установки.
    /// <para>
    /// Два апдейтера, работающие одновременно с одной установкой, пишут в одни и те же
    /// файлы вперемешку: один подменяет файл, второй в этот момент делает бэкап уже
    /// нового содержимого, и откат любого из них оставляет смесь версий. Восстановить
    /// такую установку нечем — её придётся переустанавливать вручную.
    /// </para>
    /// <para>
    /// Мьютекс принадлежит ПОТОКУ, поэтому повторный захват из того же потока проходит
    /// всегда. Проверки конкуренции здесь намеренно уходят в отдельные потоки.
    /// </para>
    /// </summary>
    public class UpdateLockTests {
        /// <summary>Имя замка стабильно: иначе два запуска не увидят друг друга.</summary>
        [Fact]
        public void ИмяЗамкаСтабильноДляОдногоКаталога() {
            const string dir = @"C:\Users\test\AppData\Local\ChillHub";
            Assert.Equal(UpdateLock.MutexName(dir), UpdateLock.MutexName(dir));
        }

        /// <summary>Разные установки не мешают друг другу: у портативной копии свой замок.</summary>
        [Fact]
        public void РазныеКаталогиДаютРазныеЗамки() {
            Assert.NotEqual(
                UpdateLock.MutexName(@"C:\Games\ChillHub"),
                UpdateLock.MutexName(@"D:\Portable\ChillHub"));
        }

        /// <summary>
        /// Написание пути значения не имеет: лаунчер и апдейтер получают каталог из разных
        /// источников (аргумент командной строки и AppContext.BaseDirectory) и различаются
        /// регистром и хвостовым слешем. Разъехавшееся имя означало бы отсутствие замка.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Games\ChillHub", @"C:\Games\ChillHub\")]
        [InlineData(@"C:\Games\ChillHub", @"c:\games\chillhub")]
        [InlineData(@"C:\Games\ChillHub", @"C:\Games\ChillHub\\")]
        [InlineData(@"C:\Games\ChillHub", @"C:\Games\Other\..\ChillHub")]
        public void НаписаниеПутиНеМеняетЗамок(string a, string b) {
            Assert.Equal(UpdateLock.MutexName(a), UpdateLock.MutexName(b));
        }

        /// <summary>
        /// Замок локальный для сеанса, а не глобальный: установка лежит в профиле
        /// пользователя, а Global\ требует прав, которых у обычного пользователя нет.
        /// </summary>
        [Fact]
        public void ЗамокЛокаленДляСеанса() {
            var name = UpdateLock.MutexName(@"C:\Games\ChillHub");
            Assert.StartsWith(@"Local\", name, StringComparison.Ordinal);
            Assert.DoesNotContain(@"Global\", name, StringComparison.Ordinal);
        }

        /// <summary>
        /// В имя не должен попадать сам путь: имя мьютекса видно всем процессам сеанса,
        /// а путь содержит имя пользователя Windows.
        /// </summary>
        [Fact]
        public void ПутьНеПопадаетВИмяЗамка() {
            var name = UpdateLock.MutexName(@"C:\Users\Вася\AppData\Local\ChillHub");
            Assert.DoesNotContain("Вася", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AppData", name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Пустой и битый путь не роняют апдейтер — он должен хотя бы попробовать.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("::недопустимый::")]
        public void БитыйПутьНеРоняет(string dir) {
            Assert.False(string.IsNullOrEmpty(UpdateLock.MutexName(dir)));
        }

        /// <summary>Свободный каталог захватывается и потом отпускается.</summary>
        [Fact]
        public void СвободныйКаталогЗахватывается() {
            using var dir = new TempDir();
            Assert.True(UpdateLock.TryAcquire(dir.Root, 0, out var m));
            try {
                Assert.NotNull(m);
            }
            finally {
                UpdateLock.Release(m);
            }
        }

        /// <summary>
        /// Пока замок держат, второй апдейтер его не получает. Проверяется из другого
        /// потока: мьютекс реентерабелен для владельца, и из того же потока захват прошёл бы.
        /// </summary>
        [Fact]
        public async Task ЗанятыйКаталогВторымАпдейтеромНеЗахватывается() {
            using var dir = new TempDir();
            Assert.True(UpdateLock.TryAcquire(dir.Root, 0, out var held));
            try {
                var busy = await Task.Run(() => UpdateLock.IsBusy(dir.Root));
                Assert.True(busy, "второй апдейтер не увидел, что каталог занят");

                var acquired = await Task.Run(() => UpdateLock.TryAcquire(dir.Root, 0, out var second)
                    ? Release(second)
                    : false);
                Assert.False(acquired, "второй апдейтер захватил уже занятый каталог");
            }
            finally {
                UpdateLock.Release(held);
            }
        }

        /// <summary>После освобождения каталог снова доступен — иначе обновление больше не запустится.</summary>
        [Fact]
        public async Task ПослеОсвобожденияКаталогСноваСвободен() {
            using var dir = new TempDir();
            Assert.True(UpdateLock.TryAcquire(dir.Root, 0, out var m));
            UpdateLock.Release(m);

            Assert.False(await Task.Run(() => UpdateLock.IsBusy(dir.Root)));
        }

        /// <summary>Свободный каталог не считается занятым.</summary>
        [Fact]
        public void СвободныйКаталогНеСчитаетсяЗанятым() {
            using var dir = new TempDir();
            Assert.False(UpdateLock.IsBusy(dir.Root));
        }

        /// <summary>Разные установки обновляются параллельно и не блокируют друг друга.</summary>
        [Fact]
        public async Task РазныеУстановкиНеБлокируютДругДруга() {
            using var a = new TempDir();
            using var b = new TempDir();

            Assert.True(UpdateLock.TryAcquire(a.Root, 0, out var held));
            try {
                Assert.False(await Task.Run(() => UpdateLock.IsBusy(b.Root)));
            }
            finally {
                UpdateLock.Release(held);
            }
        }

        /// <summary>Повторное и пустое освобождение не должно бросать: Release зовут из finally.</summary>
        [Fact]
        public void ПовторноеОсвобождениеБезопасно() {
            using var dir = new TempDir();
            UpdateLock.Release(null);
            Assert.True(UpdateLock.TryAcquire(dir.Root, 0, out var m));
            UpdateLock.Release(m);
            UpdateLock.Release(m);
        }

        /// <summary>Ожидание освобождения не длится дольше запрошенного.</summary>
        [Fact]
        public async Task ОжиданиеНеДлитсяДольшеЗапрошенного() {
            using var dir = new TempDir();
            Assert.True(UpdateLock.TryAcquire(dir.Root, 0, out var held));
            try {
                var elapsed = await Task.Run(() => {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    UpdateLock.TryAcquire(dir.Root, 200, out var second);
                    UpdateLock.Release(second);
                    return sw.ElapsedMilliseconds;
                });

                // Порог с запасом: важно, что ожидание конечно, а не точная длительность.
                Assert.True(elapsed < 5000, $"ожидание заняло {elapsed} мс при лимите 200 мс");
            }
            finally {
                UpdateLock.Release(held);
            }
        }

        /// <summary>Каталог установки может ещё не существовать — замок это не должно смущать.</summary>
        [Fact]
        public void НесуществующийКаталогЗахватывается() {
            using var dir = new TempDir();
            var missing = Path.Combine(dir.Root, "ещё-не-создан");
            Assert.True(UpdateLock.TryAcquire(missing, 0, out var m));
            UpdateLock.Release(m);
        }

        // Освобождает захваченный мьютекс и сообщает, что захват состоялся.
        private static bool Release(Mutex? m) {
            UpdateLock.Release(m);
            return true;
        }
    }
}
