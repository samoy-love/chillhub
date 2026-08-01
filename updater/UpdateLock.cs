// <copyright file="UpdateLock.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Update;

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

/// <summary>
/// Именованный замок на КАТАЛОГ УСТАНОВКИ.
/// <para>
/// Апдейтер меняет файлы работающей установки. Два апдейтера, запущенные
/// одновременно (пользователь нажал «Обновить» дважды, или второй экземпляр
/// лаунчера дошёл до применения), пишут в одни и те же файлы вперемешку:
/// один подменяет файл, второй в этот момент делает бэкап уже нового
/// содержимого, и откат любого из них оставляет смесь версий. Гонку
/// невозможно вычистить постфактум — её нужно не допустить.
/// </para>
/// <para>
/// Имя замка выводится из пути установки, поэтому две разные установки
/// (например, портативная и обычная) друг другу не мешают.
/// </para>
/// </summary>
public static class UpdateLock {
    /// <summary>
    /// Имя мьютекса для каталога установки. Одинаково считается лаунчером и апдейтером.
    /// </summary>
    /// <param name="installDir">Каталог установки.</param>
    /// <returns>Имя именованного мьютекса.</returns>
    public static string MutexName(string installDir) {
        string full;
        try {
            full = Path.GetFullPath(installDir ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch {
            full = installDir ?? string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        var id = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();

        // Local\ (а не Global\): установка живёт в профиле пользователя
        // (%LOCALAPPDATA%\ChillHub), сеанс другого пользователя нам не конкурент,
        // а Global\ требует прав, которых у обычного пользователя может не быть.
        return string.Create(CultureInfo.InvariantCulture, $@"Local\ChillHub.Updater.{id}");
    }

    /// <summary>
    /// Пытается захватить замок на каталог установки.
    /// </summary>
    /// <param name="installDir">Каталог установки.</param>
    /// <param name="waitMs">Сколько ждать освобождения (0 — не ждать).</param>
    /// <param name="mutex">Захваченный мьютекс; освободить через <see cref="Release"/>.</param>
    /// <returns>true, если замок наш.</returns>
    public static bool TryAcquire(string installDir, int waitMs, out Mutex? mutex) {
        mutex = null;
        try {
            var m = new Mutex(false, MutexName(installDir));
            bool owned;
            try {
                owned = m.WaitOne(waitMs, false);
            }
            catch (AbandonedMutexException) {
                // Предыдущий владелец умер, не освободив мьютекс. Замок теперь наш:
                // мёртвый процесс файлы уже не пишет.
                owned = true;
            }

            if (!owned) {
                m.Dispose();
                return false;
            }

            mutex = m;
            return true;
        }
        catch {
            // Мьютексы недоступны (экзотическая политика безопасности) — не повод
            // отказываться от обновления целиком, но и защиты в этом случае нет.
            mutex = null;
            return true;
        }
    }

    /// <summary>Проверяет, занят ли каталог установки другим апдейтером.</summary>
    /// <param name="installDir">Каталог установки.</param>
    /// <returns>true, если апдейтер уже работает.</returns>
    public static bool IsBusy(string installDir) {
        if (!TryAcquire(installDir, 0, out var m)) {
            return true;
        }

        Release(m);
        return false;
    }

    /// <summary>Освобождает замок.</summary>
    /// <param name="mutex">Мьютекс, полученный из <see cref="TryAcquire"/>.</param>
    public static void Release(Mutex? mutex) {
        if (mutex == null) {
            return;
        }

        try {
            mutex.ReleaseMutex();
        }
        catch {
            // Уже освобождён либо не наш — освобождать нечего.
        }

        try {
            mutex.Dispose();
        }
        catch {
            // Dispose не должен мешать выходу из процесса.
        }
    }
}
