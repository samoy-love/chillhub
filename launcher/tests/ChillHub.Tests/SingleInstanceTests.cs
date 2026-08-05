// <copyright file="SingleInstanceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Threading;

    using ChillHub.Core;

    using Xunit;

    /// <summary>
    /// Замок на второй экземпляр лаунчера.
    /// <para>
    /// Две копии синхронизируют одну папку игры независимо друг от друга: один экземпляр
    /// качает файл, второй в этот же момент считает его лишним и удаляет, а маркер
    /// незавершённого обновления снимает тот, кто закончил первым.
    /// </para>
    /// <para>
    /// Замок именованный и на весь сеанс пользователя, поэтому «другой экземпляр» тут
    /// изображает отдельный поток: владение мьютексом принадлежит потоку, а не процессу.
    /// </para>
    /// </summary>
    public class SingleInstanceTests {
        /// <summary>Имя должно совпадать с тем, что занимает продакшн-код.</summary>
        private const string MutexName = @"Local\ChillHub.SingleInstance";

        /// <summary>Свободный замок — запускаться можно.</summary>
        [Fact]
        public void СвободныйЗамокПозволяетЗапуск() {
            try {
                Assert.True(SingleInstance.TryAcquire(100));
            }
            finally {
                // Иначе замок остался бы за потоком xunit и следующий тест увидел бы
                // его свободным: владение мьютексом реентерабельно для своего потока.
                SingleInstance.ReleaseForTests();
            }
        }

        /// <summary>Занятый замок не пускает второй экземпляр.</summary>
        [Fact]
        public void ЗанятыйЗамокНеПускаетВторойЭкземпляр() {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            // Владение мьютексом — за потоком, поэтому «чужой экземпляр» держит его
            // из отдельного потока, а не из текущего.
            var holder = new Thread(() => {
                using var m = new Mutex(initiallyOwned: false, MutexName);
                m.WaitOne();
                held.Set();
                release.Wait();
                m.ReleaseMutex();
            });
            holder.IsBackground = true;
            holder.Start();

            try {
                Assert.True(held.Wait(5000), "поток-владелец должен успеть занять замок");
                Assert.False(SingleInstance.TryAcquire(100), "второй экземпляр запускаться не должен");
            }
            finally {
                SingleInstance.ReleaseForTests();
                release.Set();
                holder.Join(5000);
            }
        }

        /// <summary>Отпущенный замок снова пускает — иначе после выхода лаунчер не поднять.</summary>
        [Fact]
        public void ОтпущенныйЗамокСноваПускает() {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            var holder = new Thread(() => {
                using var m = new Mutex(initiallyOwned: false, MutexName);
                m.WaitOne();
                held.Set();
                release.Wait();
                m.ReleaseMutex();
            });
            holder.IsBackground = true;
            holder.Start();

            Assert.True(held.Wait(5000));
            release.Set();
            Assert.True(holder.Join(5000), "поток-владелец должен отпустить замок");

            try {
                Assert.True(SingleInstance.TryAcquire(1000));
            }
            finally {
                SingleInstance.ReleaseForTests();
            }
        }
    }
}
