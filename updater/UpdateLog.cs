// <copyright file="UpdateLog.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Update;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// Журнал апдейтера, который существует ДО разбора аргументов.
/// <para>
/// Раньше путь к журналу был обязательным аргументом и брался вне <c>try</c>:
/// любая проблема с ним (аргумент не передан, каталог не создаётся, диск занят)
/// убивала процесс молча — без строчки в логе и без перезапуска лаунчера.
/// Пользователь получал закрытый лаунчер и ноль информации. Поэтому лог
/// сначала копится в памяти, а при первой возможности сбрасывается в файл;
/// если указанный файл недоступен, берётся запасной путь в %TEMP%.
/// </para>
/// </summary>
internal sealed class UpdateLog {
    private readonly List<string> buffer = new();
    private string? path;

    /// <summary>Gets путь к файлу журнала (пустой, пока файл не открыт).</summary>
    public string Path => this.path ?? string.Empty;

    /// <summary>
    /// Привязывает журнал к файлу и сбрасывает накопленные строки.
    /// При неудаче пробует запасной путь в %TEMP%; если и он недоступен —
    /// журнал остаётся в памяти, но процесс продолжает работу.
    /// </summary>
    /// <param name="preferred">Желаемый путь (аргумент --log).</param>
    public void Open(string? preferred) {
        foreach (var candidate in Candidates(preferred)) {
            try {
                var dir = System.IO.Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir)) {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(candidate, string.Empty, Encoding.UTF8);
                this.path = candidate;
                this.Flush();
                return;
            }
            catch {
                // Следующий кандидат.
            }
        }
    }

    /// <summary>Пишет строку в журнал.</summary>
    /// <param name="message">Сообщение.</param>
    public void Write(string message) {
        var line = string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:O}] {message}\r\n");
        if (this.path == null) {
            this.buffer.Add(line);
            return;
        }

        try {
            File.AppendAllText(this.path, line, new UTF8Encoding(false));
        }
        catch {
            // Журнал не должен ломать обновление.
        }
    }

    private void Flush() {
        if (this.path == null || this.buffer.Count == 0) {
            return;
        }

        try {
            File.AppendAllText(this.path, string.Concat(this.buffer), new UTF8Encoding(false));
        }
        catch {
            // Не записалось — строки просто теряются, это не повод падать.
        }

        this.buffer.Clear();
    }

    private static IEnumerable<string> Candidates(string? preferred) {
        if (!string.IsNullOrWhiteSpace(preferred)) {
            yield return preferred!;
        }

        yield return System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ChillHub",
            "SelfUpdate",
            string.Create(CultureInfo.InvariantCulture, $"updater-{Environment.ProcessId}.log"));
    }
}
