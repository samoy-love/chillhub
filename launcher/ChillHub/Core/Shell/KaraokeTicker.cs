// <copyright file="KaraokeTicker.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Linq;

    /// <summary>
    /// Состояние «караоке»-строки в шапке: какая строка печатается, до какого символа
    /// она дошла и сколько времени на неё потрачено.
    /// <para>
    /// Про элементы интерфейса не знает — ни про текстовые блоки, ни про анимации, ни про
    /// таймер: печать идёт со скоростью около тридцати шагов в секунду, и проверять её,
    /// поднимая окно, нельзя. Здесь только счёт символов и времени; всё видимое остаётся
    /// в окне.
    /// </para>
    /// <para>
    /// Отсчёт ведётся по переданному времени, а не по числу тиков: под нагрузкой тики
    /// приходят реже, и посимвольная привязка к ним растягивала бы строку.
    /// </para>
    /// </summary>
    internal sealed class KaraokeTicker {
        private readonly KaraokeConfig config;

        private string[] lines = Array.Empty<string>();
        private int lineIndex;
        private int charIndex;

        // time-base for current line typing
        private DateTime lineStartAtUtc;
        private DateTime? pauseStartedUtc;
        private DateTime lastProgressAtUtc;

        internal KaraokeTicker(KaraokeConfig config) => this.config = config;

        /// <summary>Настройки скоростей и пауз.</summary>
        internal KaraokeConfig Config => this.config;

        /// <summary>Все строки песни в том порядке, в котором они печатаются.</summary>
        internal string[] Lines => this.lines;

        /// <summary>Строка, которая печатается сейчас.</summary>
        internal string CurrentLine {
            get {
                if (this.lines.Length == 0) {
                    return string.Empty;
                }

                return this.lines[Math.Clamp(this.lineIndex, 0, this.lines.Length - 1)] ?? string.Empty;
            }
        }

        /// <summary>Строка, которая пойдёт следом; после последней — снова первая.</summary>
        internal string NextLine {
            get {
                if (this.lines.Length == 0) {
                    return string.Empty;
                }

                var idx = (this.lineIndex + 1) % this.lines.Length;
                return this.lines[idx] ?? string.Empty;
            }
        }

        /// <summary>Напечатанная часть текущей строки.</summary>
        internal string TypedText => this.CurrentLine.Substring(0, Math.Min(this.charIndex, this.CurrentLine.Length));

        /// <summary>Строка дописана до конца — пора переходить к следующей.</summary>
        internal bool LineComplete => this.charIndex >= this.CurrentLine.Length;

        /// <summary>Разбирает текст песни на строки для печати.</summary>
        /// <param name="raw">Текст песни.</param>
        internal void SetLyrics(string raw) {
            // Разбиваем на строки, удаляем чисто пустые, но оставляем одинарные пустые как паузу
            var split = (raw ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            this.lines = split
                .Select(l => (l ?? string.Empty).TrimEnd())
                .ToArray();
            if (this.lines.Length == 0) {
                this.lines = new[] { string.Empty };
            }
        }

        /// <summary>Возвращает печать к самому началу песни.</summary>
        /// <param name="nowUtc">Текущее время.</param>
        internal void ResetToStart(DateTime nowUtc) {
            this.lineIndex = 0;
            this.charIndex = 0;
            this.ResetTimeBase(nowUtc);
        }

        /// <summary>
        /// Сдвигает отметку прогресса назад на один символ, чтобы первый же тик после
        /// старта что-то напечатал: иначе строка начинается заметной паузой.
        /// </summary>
        /// <param name="nowUtc">Текущее время.</param>
        internal void BackdateForFirstChar(DateTime nowUtc)
            => this.lastProgressAtUtc = nowUtc.AddMilliseconds(-this.config.CharIntervalMs);

        /// <summary>Запоминает начало паузы; повторный вызов ничего не меняет.</summary>
        /// <param name="nowUtc">Текущее время.</param>
        internal void BeginPause(DateTime nowUtc) {
            if (this.pauseStartedUtc == null) {
                this.pauseStartedUtc = nowUtc;
            }
        }

        /// <summary>
        /// Учитывает длительность паузы: отметка прогресса сдвигается вперёд, иначе при
        /// возобновлении печать «догоняла» бы сразу всю строку.
        /// </summary>
        /// <param name="nowUtc">Текущее время.</param>
        internal void EndPause(DateTime nowUtc) {
            if (this.pauseStartedUtc == null) {
                return;
            }

            // Обнуляем отметку паузы до сдвига: сдвиг может не удаться, но пауза
            // при этом уже закончилась — иначе она учлась бы ещё раз.
            var pausedDur = nowUtc - this.pauseStartedUtc.Value;
            this.pauseStartedUtc = null;
            this.lastProgressAtUtc += pausedDur;
        }

        /// <summary>
        /// Сколько символов положено напечатать к этому моменту, с потолком на один тик.
        /// </summary>
        /// <param name="nowUtc">Текущее время.</param>
        /// <returns>Число символов; 0 — печатать пока нечего.</returns>
        internal int PlanAdvance(DateTime nowUtc) {
            var deltaMs = (nowUtc - this.lastProgressAtUtc).TotalMilliseconds;
            int add = (int)Math.Floor(deltaMs / Math.Max(1.0, this.config.CharIntervalMs));
            if (add <= 0) {
                return 0;
            }

            return add > this.config.MaxAdvanceCharsPerTick ? this.config.MaxAdvanceCharsPerTick : add;
        }

        /// <summary>Печатает очередные символы и возвращает то, что теперь видно.</summary>
        /// <param name="add">Сколько символов добавить.</param>
        /// <returns>Напечатанная часть строки.</returns>
        internal string Type(int add) {
            var line = this.CurrentLine;
            this.charIndex = Math.Min(line.Length, this.charIndex + add);
            return line.Substring(0, this.charIndex);
        }

        /// <summary>
        /// Сдвигает отметку прогресса на время, «потраченное» на напечатанные символы.
        /// Сдвиг именно на потраченное, а не на текущее время: иначе накопившееся
        /// отставание списывалось бы каждый тик и печать шла бы медленнее заданной.
        /// </summary>
        /// <param name="add">Сколько символов напечатали.</param>
        internal void CommitProgress(int add)
            => this.lastProgressAtUtc = this.lastProgressAtUtc.AddMilliseconds(add * this.config.CharIntervalMs);

        /// <summary>Аварийный сброс отметки прогресса, когда обычный сдвиг не удался.</summary>
        /// <param name="nowUtc">Текущее время.</param>
        internal void ResetProgressTo(DateTime nowUtc) => this.lastProgressAtUtc = nowUtc;

        /// <summary>Переходит к следующей строке; после последней — к первой.</summary>
        /// <param name="nowUtc">Текущее время.</param>
        internal void MoveToNextLine(DateTime nowUtc) {
            this.lineIndex = (this.lineIndex + 1) % this.lines.Length;
            this.charIndex = 0;
            this.ResetTimeBase(nowUtc);
        }

        /// <summary>
        /// Ширина контейнера строки: измеренная ширина плюс отступы, но не уже 260 и не шире 800.
        /// Без ограничений шапка либо схлопывается в точку, либо выдавливает кнопки за край окна.
        /// </summary>
        /// <param name="measured">Ширина самой длинной строки.</param>
        /// <param name="padding">Внутренние отступы контейнера, слева плюс справа.</param>
        /// <returns>Ширина контейнера.</returns>
        internal static double HostWidth(double measured, double padding) {
            double width = Math.Ceiling(measured) + padding + 12; // padding + safety
            return Math.Max(260, Math.Min(width, 800));
        }

        private void ResetTimeBase(DateTime nowUtc) {
            this.lineStartAtUtc = nowUtc;
            this.pauseStartedUtc = null;
            this.lastProgressAtUtc = this.lineStartAtUtc;
        }
    }
}
