// <copyright file="StartupGuardTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Threading.Tasks;

    using ChillHub.Core.Shell;

    using Xunit;

    /// <summary>
    /// Исход аварии на старте лаунчера.
    /// <para>
    /// Шаги запуска идут в async void, и до появления окна режим завершения — «только
    /// явно». Пока страховки не было, исключение (значок в трее не создаётся при
    /// перезапуске explorer.exe) оставляло живой процесс без окна и без значка: снять
    /// его можно было только диспетчером задач, а замок единственного экземпляра он
    /// держал всё это время — повторный запуск лаунчера молча не стартовал.
    /// </para>
    /// </summary>
    public class StartupGuardTests : IDisposable {
        /// <inheritdoc/>
        public void Dispose() => BootLog.ResetPathForTests();

        /// <summary>Удачный запуск проходит насквозь: гасить и жаловаться не на что.</summary>
        [Fact]
        public async Task УдачныйЗапускНичегоНеГасит() {
            var shutdowns = 0;
            var reported = 0;

            await StartupGuard.RunAsync(
                () => Task.CompletedTask,
                () => false,
                _ => reported++,
                () => shutdowns++);

            Assert.Equal(0, reported);
            Assert.Equal(0, shutdowns);
        }

        /// <summary>
        /// Окна ещё нет — приложение обязано завершиться, а не остаться процессом без
        /// окна, значка и с занятым замком единственного экземпляра.
        /// </summary>
        [Fact]
        public async Task АварияДоПоявленияОкнаЗавершаетПриложение() {
            var boom = new InvalidOperationException("значок в трее не создан");
            Exception? seen = null;
            var shutdowns = 0;

            await StartupGuard.RunAsync(
                () => throw boom,
                () => false,
                ex => seen = ex,
                () => shutdowns++);

            Assert.Same(boom, seen);
            Assert.Equal(1, shutdowns);
        }

        /// <summary>Падение после первого await — тот же исход: исключение из async-шага не теряется.</summary>
        [Fact]
        public async Task АварияПослеПервогоAwaitТожеЗавершаетПриложение() {
            var shutdowns = 0;

            await StartupGuard.RunAsync(
                async () => {
                    await Task.Yield();
                    throw new InvalidOperationException("упало после await");
                },
                () => false,
                _ => { },
                () => shutdowns++);

            Assert.Equal(1, shutdowns);
        }

        /// <summary>
        /// Окно уже на экране — авария относится к тому, что после него (открытие игры по
        /// ярлыку). Лаунчер работает дальше: гасить рабочее окно из-за этого нельзя.
        /// </summary>
        [Fact]
        public async Task ПриЖивомОкнеЛаунчерПродолжаетРаботу() {
            var shutdowns = 0;
            var reported = 0;

            await StartupGuard.RunAsync(
                () => throw new InvalidOperationException("ярлык не открылся"),
                () => true,
                _ => reported++,
                () => shutdowns++);

            Assert.Equal(1, reported);
            Assert.Equal(0, shutdowns);
        }

        /// <summary>
        /// Сообщить об аварии не вышло (окно с ошибкой тоже надо чем-то показать) —
        /// тем более нельзя остаться висеть без окна.
        /// </summary>
        [Fact]
        public async Task СбойСообщенияНеОтменяетЗавершение() {
            var log = Path.Combine(Path.GetTempPath(), "chillhub-tests", Guid.NewGuid().ToString("N") + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            BootLog.PathProvider = () => log;
            var shutdowns = 0;

            await StartupGuard.RunAsync(
                () => throw new InvalidOperationException("нечем показать окно"),
                () => false,
                _ => throw new InvalidOperationException("и сообщение тоже"),
                () => shutdowns++);

            Assert.Equal(1, shutdowns);
        }
    }
}
