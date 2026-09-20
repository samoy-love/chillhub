// <copyright file="SyncPlanner.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using ChillHub.Core.Sync;

    /// <summary>Построение плана различий так, чтобы обход папки игры не выполнялся на UI-потоке.</summary>
    internal static class SyncPlanner {
        /// <summary>Замок на <see cref="InFlight"/>: словарь и счётчик ждущих меняются вместе.</summary>
        private static readonly object Gate = new object();

        /// <summary>
        /// Расчёты, идущие прямо сейчас, по одному на игру + папку + версию.
        /// <para>
        /// ОДИН И ТОТ ЖЕ ОТВЕТ СПРАШИВАЮТ ДВОЕ, И КАЖДЫЙ РАЗ ЭТО ПОЛНЫЙ ОБХОД ПАПКИ.
        /// Открытие страницы игры заводит и проверку статуса (<see cref="GameStatusVerifier"/>),
        /// и подсказку «Нужно: N» на главной. Настройки у обеих одинаковые, ответ — тоже,
        /// а платится он дважды: на сборке в несколько гигабайт это минуты чтения диска и
        /// пересчёта хешей, да ещё обе в конце пишут кеш хешей одной игры.
        /// </para>
        /// <para>
        /// Опоздавший не начинает свой обход, а ждёт уже идущий. Готовый
        /// <see cref="DiffPlan"/> после расчёта никто не меняет — <c>ExecuteAsync</c> только
        /// читает его, — так что делить объект на всех безопасно.
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, Flight> InFlight =
            new Dictionary<string, Flight>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Строит план различий, гарантированно не занимая UI-поток.
        /// <see cref="ISyncService.PlanAsync(Manifest, string, string, CancellationToken)"/> только выглядит
        /// асинхронным: внутри полный обход папки игры с пересчётом хешей, а результат возвращается
        /// через уже завершённый Task. При вызове с UI-потока окно замирает на всё время обхода
        /// (гигабайты SHA-256/BLAKE3), Windows рисует «Не отвечает», и даже «Отмена» не нажимается.
        /// Тот же приём применён в <see cref="IntegrityChecker"/>.
        /// </summary>
        /// <param name="sync">Служба синхронизации.</param>
        /// <param name="manifest">Манифест эталонной версии.</param>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <param name="contentBaseUrl">База URL для скачивания файлов.</param>
        /// <param name="token">Токен отмены.</param>
        /// <returns>План различий.</returns>
        internal static Task<DiffPlan> PlanOffUiThreadAsync(
            ISyncService sync, Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken token) =>

            // PlanOptions.ForGame читает с диска копию манифеста модпака, поэтому строится
            // ВНУТРИ Task.Run — на UI-потоке здесь не должно происходить ничего, включая
            // открытие файла на сетевом диске.
            Task.Run(() => sync.PlanAsync(manifest, localRoot, contentBaseUrl, PlanOptions.ForGame(localRoot), token), token);

        /// <summary>
        /// То же с готовыми настройками — для синхронизации, которая владеет не всем
        /// корнем (модпак): её настройки строит вызывающий, а не мы.
        /// </summary>
        /// <param name="sync">Служба синхронизации.</param>
        /// <param name="manifest">Манифест эталонной версии.</param>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <param name="contentBaseUrl">База URL для скачивания файлов.</param>
        /// <param name="options">Настройки построения плана.</param>
        /// <param name="token">Токен отмены.</param>
        /// <returns>План различий.</returns>
        internal static Task<DiffPlan> PlanOffUiThreadAsync(
            ISyncService sync,
            Manifest manifest,
            string localRoot,
            string contentBaseUrl,
            PlanOptions options,
            CancellationToken token) =>
            Task.Run(() => sync.PlanAsync(manifest, localRoot, contentBaseUrl, options, token), token);

        /// <summary>
        /// То же, но одинаковые запросы объединяются в один обход папки (см. <see cref="InFlight"/>).
        /// <para>
        /// ТОЛЬКО ДЛЯ ЧИТАЮЩИХ. Тот, кто по этому плану потом МЕНЯЕТ файлы
        /// (<see cref="Game.GameSyncRunner"/>), всегда считает свой: настройки плана читают
        /// с диска состав установленного модпака, а он между началом чужого расчёта и
        /// нашим мог измениться — лаунчер сам его туда и ставит. Разделив с ним расчёт,
        /// синхронизация игры однажды снесла бы моды как лишние файлы, и заметить это
        /// можно было бы только по жалобе.
        /// </para>
        /// </summary>
        /// <param name="sync">Служба синхронизации.</param>
        /// <param name="manifest">Манифест эталонной версии.</param>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <param name="contentBaseUrl">База URL для скачивания файлов.</param>
        /// <param name="token">Токен отмены вызывающего.</param>
        /// <returns>План различий.</returns>
        internal static async Task<DiffPlan> SharedPlanOffUiThreadAsync(
            ISyncService sync, Manifest manifest, string localRoot, string contentBaseUrl, CancellationToken token) {
            var key = KeyFor(manifest, localRoot, contentBaseUrl);
            Flight flight;
            lock (Gate) {
                if (!InFlight.TryGetValue(key, out flight!)) {
                    flight = new Flight(key);
                    InFlight[key] = flight;
                    flight.Start(ct => PlanOffUiThreadAsync(sync, manifest, localRoot, contentBaseUrl, ct));
                }

                flight.Waiters++;
            }

            try {
                // WaitAsync, а не голый await: отмена у каждого ждущего своя. Ушедший по
                // отмене не уносит расчёт, который ещё нужен остальным, — обход папки
                // прекращается, только когда уходит последний (см. Flight.Leave).
                return await flight.Work.WaitAsync(token).ConfigureAwait(false);
            }
            finally {
                flight.Leave();
            }
        }

        /// <summary>Ключ расчёта: одна игра, одна папка, одна версия.</summary>
        /// <param name="manifest">Манифест эталонной версии.</param>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <param name="contentBaseUrl">База URL для скачивания файлов.</param>
        /// <returns>Ключ.</returns>
        private static string KeyFor(Manifest manifest, string localRoot, string contentBaseUrl) {
            string root;
            try {
                root = System.IO.Path.GetFullPath(localRoot ?? string.Empty).TrimEnd('\\', '/');
            }
            catch (Exception) {
                // Путь, который не разворачивается, — не повод ронять подсказку о размере:
                // берём как есть, в худшем случае расчёты просто не объединятся.
                root = localRoot ?? string.Empty;
            }

            return string.Join("|", manifest?.GameId, manifest?.Version, root, contentBaseUrl);
        }

        /// <summary>Один идущий расчёт и все, кто его ждёт.</summary>
        private sealed class Flight {
            private readonly string key;
            private readonly CancellationTokenSource cts = new CancellationTokenSource();

            internal Flight(string key) => this.key = key;

            /// <summary>Gets or sets сколько вызывающих сейчас ждут этот расчёт.</summary>
            internal int Waiters { get; set; }

            /// <summary>Gets сам расчёт.</summary>
            internal Task<DiffPlan> Work { get; private set; } = null!;

            /// <summary>Запускает расчёт и снимает его с учёта по завершении.</summary>
            /// <param name="start">Чем начинать расчёт.</param>
            internal void Start(Func<CancellationToken, Task<DiffPlan>> start) {
                this.Work = start(this.cts.Token);

                // С учёта снимаем сразу по завершении: следующий спрашивающий должен
                // получить свежий обход папки, а не готовый ответ пятиминутной давности.
                // Тогда же освобождается источник отмены — иначе на каждый обход папки
                // оставался бы утёкший CancellationTokenSource со своими регистрациями.
                _ = this.Work.ContinueWith(
                    _ => {
                        this.Forget();
                        this.cts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            /// <summary>Один из ждущих ушёл: дождался, сдался по ошибке или отменился.</summary>
            internal void Leave() {
                lock (Gate) {
                    if (--this.Waiters > 0) {
                        return;
                    }

                    this.Forget();
                }

                try {
                    // Ждать результат больше некому — продолжать обход папки незачем.
                    // Законченный расчёт отмена не трогает.
                    this.cts.Cancel();
                }
                catch (ObjectDisposedException) {
                    // Расчёт успел закончиться, и источник уже освобождён: отменять нечего.
                }
            }

            /// <summary>Убирает расчёт из списка идущих, если он всё ещё там числится.</summary>
            private void Forget() {
                lock (Gate) {
                    if (InFlight.TryGetValue(this.key, out var current) && ReferenceEquals(current, this)) {
                        InFlight.Remove(this.key);
                    }
                }
            }
        }
    }
}
