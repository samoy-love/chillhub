// <copyright file="SingleInstanceTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
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
    /// <para>
    /// Имя замка у каждого теста своё. Боевое брать нельзя: его держит настоящий
    /// лаунчер, если он запущен, — тогда набор всегда красный, — а пока тест держал
    /// бы его сам, установленная копия отказалась бы стартовать.
    /// </para>
    /// </summary>
    public class SingleInstanceTests {
        /// <summary>Свободный замок — запускаться можно.</summary>
        [Fact]
        public void СвободныйЗамокПозволяетЗапуск() {
            var mutexName = UniqueMutexName();

            try {
                Assert.True(SingleInstance.TryAcquire(100, mutexName));
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
            var mutexName = UniqueMutexName();
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            // Владение мьютексом — за потоком, поэтому «чужой экземпляр» держит его
            // из отдельного потока, а не из текущего.
            var holder = new Thread(() => {
                using var m = new Mutex(initiallyOwned: false, mutexName);
                m.WaitOne();
                held.Set();
                release.Wait();
                m.ReleaseMutex();
            });
            holder.IsBackground = true;
            holder.Start();

            try {
                Assert.True(held.Wait(5000), "поток-владелец должен успеть занять замок");
                Assert.False(SingleInstance.TryAcquire(100, mutexName), "второй экземпляр запускаться не должен");
            }
            finally {
                SingleInstance.ReleaseForTests();
                release.Set();
                holder.Join(5000);
            }
        }

        /// <summary>
        /// Брошенный замок не запирает лаунчер навсегда.
        /// <para>
        /// Лаунчер, убитый из диспетчера, замок не отпускает. Если бы это считалось
        /// «уже запущен», лаунчер перестал бы стартовать до перезагрузки.
        /// </para>
        /// </summary>
        [Fact]
        public void БрошенныйЗамокНеЗапираетЛаунчерНавсегда() {
            var mutexName = UniqueMutexName();

            // Поток занимает замок и умирает, не отпустив, — ровно как убитый процесс
            var holder = new Thread(() => {
                var m = new Mutex(initiallyOwned: false, mutexName);
                m.WaitOne();
            });
            holder.IsBackground = true;
            holder.Start();
            Assert.True(holder.Join(5000), "поток-владелец должен завершиться");

            try {
                Assert.True(SingleInstance.TryAcquire(100, mutexName), "брошенный замок должен доставаться нам");
            }
            finally {
                SingleInstance.ReleaseForTests();
            }
        }

        /// <summary>
        /// Замок, освободившийся во время ожидания, пускает.
        /// <para>
        /// Ради этого ожидание и заведено: апдейтер перезапускает лаунчер, не всегда
        /// дождавшись выхода прежнего, и мгновенный отказ означал бы «после обновления
        /// лаунчер не запускается».
        /// </para>
        /// </summary>
        [Fact]
        public void ОсвободившийсяВоВремяОжиданияЗамокПускает() {
            var mutexName = UniqueMutexName();
            using var held = new ManualResetEventSlim(false);

            var holder = new Thread(() => {
                using var m = new Mutex(initiallyOwned: false, mutexName);
                m.WaitOne();
                held.Set();
                Thread.Sleep(200);
                m.ReleaseMutex();
            });
            holder.IsBackground = true;
            holder.Start();

            Assert.True(held.Wait(5000), "поток-владелец должен успеть занять замок");

            try {
                // На входе замок занят: ждём и дожидаемся
                Assert.True(SingleInstance.TryAcquire(5000, mutexName));
            }
            finally {
                SingleInstance.ReleaseForTests();
                holder.Join(5000);
            }
        }

        /// <summary>Отпущенный замок снова пускает — иначе после выхода лаунчер не поднять.</summary>
        [Fact]
        public void ОтпущенныйЗамокСноваПускает() {
            var mutexName = UniqueMutexName();
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            var holder = new Thread(() => {
                using var m = new Mutex(initiallyOwned: false, mutexName);
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
                Assert.True(SingleInstance.TryAcquire(1000, mutexName));
            }
            finally {
                SingleInstance.ReleaseForTests();
            }
        }

        /// <summary>
        /// Имя замка, которого нет больше ни у кого: ни у боевого лаунчера, ни у
        /// соседнего теста. Замок именованный и виден всему сеансу пользователя.
        /// </summary>
        /// <returns>Уникальное имя.</returns>
        private static string UniqueMutexName()
            => @"Local\ChillHub.SingleInstance.Tests." + Guid.NewGuid().ToString("N");
    }
}
