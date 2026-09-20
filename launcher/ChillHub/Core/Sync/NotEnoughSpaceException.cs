// <copyright file="NotEnoughSpaceException.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.IO;

    /// <summary>
    /// На диске не хватает места под операцию.
    /// <para>
    /// ОТДЕЛЬНЫЙ ТИП, А НЕ ГОЛЫЙ <see cref="IOException"/>, потому что этот отказ
    /// единственный из всех дисковых, у которого есть понятный игроку ответ:
    /// освободить столько-то и повторить. Пока он приезжал наверх обычным
    /// <see cref="IOException"/>, установка модпака называла его «Не удалось установить
    /// моды. Попробуйте ещё раз» — то есть предлагала повторить ровно то, что
    /// гарантированно повторится, — а в статистику уходила как общий сбой синхронизации
    /// вперемешку с правами доступа и обрывами сети.
    /// </para>
    /// <para>
    /// Числа лежат полями, а не только в тексте: сообщение игроку собирается там, где
    /// известно, как в этом месте принято называть размеры, а разбирать ради них
    /// строку исключения — способ однажды тихо перестать их находить.
    /// </para>
    /// </summary>
    internal sealed class NotEnoughSpaceException : IOException {
        /// <summary>Initializes a new instance of the <see cref="NotEnoughSpaceException"/> class.</summary>
        /// <param name="drive">Корень тома, на котором не хватило места.</param>
        /// <param name="requiredBytes">Сколько байт нужно свободными.</param>
        /// <param name="availableBytes">Сколько байт свободно на самом деле.</param>
        /// <param name="inner">
        /// Отказ файловой системы, из которого этот вывод сделан; null — место
        /// посчитали заранее, и отказа ещё не было. Нужен журналу: без него в логе
        /// остаётся вердикт без единой строки о том, на чём именно он получен.
        /// </param>
        internal NotEnoughSpaceException(string drive, long requiredBytes, long availableBytes, Exception? inner = null)
            : base(
                $"Недостаточно свободного места на диске {drive}. " +
                $"Требуется {requiredBytes} байт, доступно {availableBytes} байт.",
                inner) {
            this.Drive = drive;
            this.RequiredBytes = requiredBytes;
            this.AvailableBytes = availableBytes;
        }

        /// <summary>Gets корень тома, на котором не хватило места.</summary>
        internal string Drive { get; }

        /// <summary>Gets сколько байт нужно свободными.</summary>
        internal long RequiredBytes { get; }

        /// <summary>Gets сколько байт свободно на самом деле.</summary>
        internal long AvailableBytes { get; }

        /// <summary>Gets сколько не хватает: именно это число игрок и освобождает.</summary>
        internal long MissingBytes => this.RequiredBytes > this.AvailableBytes
            ? this.RequiredBytes - this.AvailableBytes
            : 0;
    }
}
