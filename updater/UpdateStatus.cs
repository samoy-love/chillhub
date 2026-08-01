// <copyright file="UpdateStatus.cs" company="PlaceholderCompany">
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
/// Исход последнего запуска апдейтера, записанный рядом с маркером версии.
/// <para>
/// Апдейтер — отдельный процесс, который завершается уже ПОСЛЕ перезапуска
/// лаунчера. Его код возврата (2 — копирование не доехало, 3 — фатальная ошибка)
/// не читает никто: родителя к этому моменту нет. Поэтому единственный способ
/// рассказать пользователю, почему обновление не применилось, — оставить запись
/// на диске, которую лаунчер прочитает при следующем старте.
/// </para>
/// </summary>
public sealed class UpdateStatus {
    /// <summary>Имя файла состояния в каталоге установки (рядом с launcher.version).</summary>
    public const string FileName = "launcher.update-status";

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>Gets or sets исход: ok / copy-errors / integrity / fatal.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Gets or sets код возврата апдейтера.</summary>
    public int ExitCode { get; set; }

    /// <summary>Gets or sets версию, которую пытались установить.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets человекочитаемое пояснение.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets путь к журналу апдейтера.</summary>
    public string LogPath { get; set; } = string.Empty;

    /// <summary>Gets or sets время записи (UTC, ISO-8601).</summary>
    public string TimeUtc { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether обновление применено успешно.</summary>
    public bool IsSuccess => string.Equals(this.Outcome, "ok", StringComparison.OrdinalIgnoreCase);

    /// <summary>Полный путь к файлу состояния.</summary>
    /// <param name="installDir">Каталог установки.</param>
    /// <returns>Путь к файлу.</returns>
    public static string PathIn(string installDir) => Path.Combine(installDir, FileName);

    /// <summary>
    /// Пишет состояние атомарно. Ошибка записи не должна ронять апдейтер:
    /// это диагностика, а не часть обновления.
    /// </summary>
    /// <param name="installDir">Каталог установки.</param>
    /// <param name="status">Состояние.</param>
    /// <param name="log">Логгер.</param>
    public static void Write(string installDir, UpdateStatus status, Action<string>? log = null) {
        try {
            status.TimeUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append("outcome=").Append(Escape(status.Outcome)).Append('\n');
            sb.Append("exit=").Append(status.ExitCode.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("version=").Append(Escape(status.Version)).Append('\n');
            sb.Append("time=").Append(Escape(status.TimeUtc)).Append('\n');
            sb.Append("log=").Append(Escape(status.LogPath)).Append('\n');
            sb.Append("message=").Append(Escape(status.Message)).Append('\n');
            AtomicFile.WriteAllText(PathIn(installDir), sb.ToString(), Utf8NoBom);
        }
        catch (Exception ex) {
            log?.Invoke($"update status write error: {ex.Message}");
        }
    }

    /// <summary>Читает состояние; возвращает null, если файла нет или он испорчен.</summary>
    /// <param name="installDir">Каталог установки.</param>
    /// <returns>Состояние либо null.</returns>
    public static UpdateStatus? TryRead(string installDir) {
        try {
            var path = PathIn(installDir);
            if (!File.Exists(path)) {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8)) {
                var idx = line.IndexOf('=', StringComparison.Ordinal);
                if (idx <= 0) {
                    continue;
                }

                map[line.Substring(0, idx)] = Unescape(line.Substring(idx + 1));
            }

            if (map.Count == 0) {
                return null;
            }

            return new UpdateStatus {
                Outcome = Get(map, "outcome"),
                ExitCode = int.TryParse(Get(map, "exit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0,
                Version = Get(map, "version"),
                TimeUtc = Get(map, "time"),
                LogPath = Get(map, "log"),
                Message = Get(map, "message"),
            };
        }
        catch {
            return null;
        }
    }

    /// <summary>Удаляет файл состояния (после того, как о нём сообщили пользователю).</summary>
    /// <param name="installDir">Каталог установки.</param>
    public static void Clear(string installDir) {
        try {
            AtomicFile.TryDelete(PathIn(installDir));
        }
        catch {
            // Не удалилось — в худшем случае сообщение покажется ещё раз.
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) ? v : string.Empty;

    // Значения однострочные: переводы строк экранируем, иначе одна запись
    // с многострочным сообщением превратилась бы в несколько ключей.
    private static string Escape(string? s)
        => (s ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string s) {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++) {
            if (s[i] == '\\' && i + 1 < s.Length) {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', '\\' => '\\', var c => c });
                continue;
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }
}
