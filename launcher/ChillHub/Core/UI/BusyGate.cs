// <copyright file="BusyGate.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System;

    /// <summary>
    /// Показ индикатора работы, которая может кончиться мгновенно.
    /// <para>
    /// Полоса «Проверяем файлы игры…» появлялась и исчезала по факту смены текста. На
    /// машине, где проверка занимает пятьдесят миллисекунд, это не статус, а вспышка:
    /// человек видит, что внизу что-то дёрнулось, и не успевает прочитать, что именно.
    /// Прочитать он и не должен — работа уже кончилась.
    /// </para>
    /// <para>
    /// Правило двустороннее, и обе стороны нужны. <b>Порог появления</b>: работа короче
    /// него не показывается вовсе — показывать нечего. <b>Минимум на экране</b>: если
    /// индикатор всё-таки появился, он живёт не меньше этого времени, иначе получится
    /// та же вспышка, только позже.
    /// </para>
    /// <para>
    /// Времени здесь нет и таймеров тоже: и часы, и отложенный вызов приходят снаружи.
    /// Иначе поведение проверялось бы настоящими паузами в тестах — то есть не
    /// проверялось бы вовсе.
    /// </para>
    /// </summary>
    internal sealed class BusyGate {
        /// <summary>Работа короче — индикатор не появляется. 250 мс: ниже порога человек не замечает задержки.</summary>
        internal const int AppearAfterMs = 250;

        /// <summary>Появился — живёт не меньше. 400 мс хватает, чтобы прочитать короткую строку.</summary>
        internal const int MinVisibleMs = 400;

        private readonly Action<bool> apply;
        private readonly Func<DateTime> now;
        private readonly Action<TimeSpan, Action> schedule;

        private bool wanted;
        private bool visible;
        private DateTime shownAt;
        private int generation;

        /// <summary>Initializes a new instance of the <see cref="BusyGate"/> class.</summary>
        /// <param name="apply">Показать или спрятать индикатор.</param>
        /// <param name="now">Часы; по умолчанию системные.</param>
        /// <param name="schedule">Отложенный вызов; по умолчанию — таймер диспетчера.</param>
        internal BusyGate(Action<bool> apply, Func<DateTime>? now = null, Action<TimeSpan, Action>? schedule = null) {
            this.apply = apply;
            this.now = now ?? (() => DateTime.UtcNow);
            this.schedule = schedule ?? DispatcherSchedule;
        }

        /// <summary>Видно ли индикатор прямо сейчас.</summary>
        internal bool Visible => this.visible;

        /// <summary>
        /// Ставит состояние немедленно, без порогов. Для случаев, когда работа заведомо
        /// длинная и мигать нечему: очередь загрузок появляется сразу и висит минутами,
        /// задерживать её на четверть секунды незачем.
        /// </summary>
        /// <param name="busy">Показать индикатор.</param>
        internal void Force(bool busy) {
            this.wanted = busy;
            this.generation++;
            if (this.visible == busy) {
                return;
            }

            this.visible = busy;
            this.shownAt = this.now();
            this.apply(busy);
        }

        /// <summary>Сообщает, идёт работа или нет.</summary>
        /// <param name="busy">Работа идёт.</param>
        internal void Set(bool busy) {
            if (busy == this.wanted) {
                return;
            }

            this.wanted = busy;

            // Каждая смена намерения обесценивает отложенные вызовы предыдущей: без этого
            // «начали — кончили — начали» оставляло висеть три таймера, и последний из них
            // прятал индикатор посреди новой работы.
            var mine = ++this.generation;

            if (busy) {
                if (this.visible) {
                    return;
                }

                this.schedule(TimeSpan.FromMilliseconds(AppearAfterMs), () => {
                    if (mine != this.generation || !this.wanted || this.visible) {
                        return;
                    }

                    this.visible = true;
                    this.shownAt = this.now();
                    this.apply(true);
                });
                return;
            }

            if (!this.visible) {
                return;
            }

            var left = TimeSpan.FromMilliseconds(MinVisibleMs) - (this.now() - this.shownAt);
            if (left <= TimeSpan.Zero) {
                this.visible = false;
                this.apply(false);
                return;
            }

            this.schedule(left, () => {
                if (mine != this.generation || this.wanted || !this.visible) {
                    return;
                }

                this.visible = false;
                this.apply(false);
            });
        }

        private static void DispatcherSchedule(TimeSpan delay, Action action) {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => {
                timer.Stop();
                action();
            };
            timer.Start();
        }
    }
}
