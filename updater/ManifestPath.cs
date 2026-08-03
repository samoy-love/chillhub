// <copyright file="ManifestPath.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Update;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Проверка относительных путей, пришедших ИЗВНЕ (манифест сборки, списки
/// filelist/deletelist/emptydirs для апдейтера).
/// <para>
/// Такой путь подставляется в <c>Path.Combine(root, rel)</c>, а результат
/// открывается на запись или удаление. Значит путь — это данные, которым нельзя
/// доверять: <c>"../../../AppData/Roaming/Microsoft/Windows/Start Menu/Programs/Startup/x.exe"</c>
/// кладёт исполняемый файл в автозагрузку, а <c>"C:/Windows/System32/x.dll"</c>
/// уводит запись вообще за пределы корня. Проверка хеша от этого не спасает:
/// хеш берётся из того же манифеста.
/// </para>
/// <para>
/// Класс живёт в проекте апдейтера, потому что правила обязаны совпадать у обеих
/// сторон: лаунчер решает, что писать в папку игры, апдейтер — что писать в папку
/// установки. Разъедься они, и одна из сторон снова начнёт доверять чужому пути.
/// </para>
/// </summary>
public static class ManifestPath {
    /// <summary>Максимальная длина относительного пути. Защита от «путь на 30 000 символов».</summary>
    public const int MaxLength = 1024;

    /// <summary>Имена устройств Windows: обращение к ним по имени файла попадает не в файл.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Приводит путь к каноническому виду: слеши вперёд, без повторов,
    /// без ведущих и замыкающих слешей, без краевых пробелов.
    /// <para>
    /// ВАЖНО: канонизация — это НЕ санация. Она существует только чтобы описать,
    /// как должен выглядеть путь; путь, который отличается от своей канонической
    /// формы, отвергается (см. <see cref="IsSafe(string)"/>), а не исправляется.
    /// Иначе подписываются одни байты, а на диск идут другие.
    /// </para>
    /// </summary>
    /// <param name="path">Исходный путь.</param>
    /// <returns>Канонический путь.</returns>
    public static string Canonicalize(string? path) {
        var s = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal)) {
            s = s.Replace("//", "/", StringComparison.Ordinal);
        }

        return s.Trim('/');
    }

    /// <summary>
    /// Путь безопасен: он канонический и гарантированно остаётся внутри корня.
    /// </summary>
    /// <param name="path">Проверяемый путь.</param>
    /// <returns>true, если путь можно использовать.</returns>
    public static bool IsSafe(string? path) => Describe(path) == null;

    /// <summary>
    /// Объясняет, чем плох путь. Возвращает <c>null</c>, если путь безопасен.
    /// Текст попадает в лог и в сообщение об отказе — по нему видно, что именно
    /// не так с манифестом.
    /// </summary>
    /// <param name="path">Проверяемый путь.</param>
    /// <returns>Причина отказа либо <c>null</c>.</returns>
    public static string? Describe(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return "пустой путь";
        }

        if (path.Length > MaxLength) {
            return $"длина {path.Length} превышает предел {MaxLength}";
        }

        // Управляющие символы: TAB и LF — разделители полей в подписываемом
        // представлении, остальные в именах файлов не встречаются вовсе.
        foreach (var ch in path) {
            if (ch < 0x20 || ch == 0x7F) {
                return $"управляющий символ U+{(int)ch:X4}";
            }
        }

        // Двоеточие — это либо диск ("C:/..."), либо альтернативный поток NTFS
        // ("file.txt:evil.exe"), который пишется мимо самого файла.
        if (path.Contains(':', StringComparison.Ordinal)) {
            return "двоеточие (диск или альтернативный поток NTFS)";
        }

        if (path.Contains('\\', StringComparison.Ordinal)) {
            return "обратный слеш";
        }

        // Всё, что не совпало с канонической формой, отвергаем целиком: подписан
        // канонический вид, а на диск пошёл бы исходный.
        if (!string.Equals(path, Canonicalize(path), StringComparison.Ordinal)) {
            return "неканоническая форма (краевые пробелы/слеши либо повтор слешей)";
        }

        if (Path.IsPathRooted(path) || Path.IsPathFullyQualified(path)) {
            return "абсолютный путь";
        }

        foreach (var segment in path.Split('/')) {
            if (segment.Length == 0) {
                return "пустой сегмент";
            }

            if (segment == "." || segment == "..") {
                return $"сегмент '{segment}'";
            }

            // Windows молча срезает точки и пробелы в конце имени, поэтому
            // "foo. " и "foo" — один и тот же файл, но разные подписанные байты.
            if (segment[^1] == '.' || segment[^1] == ' ' || segment[0] == ' ') {
                return $"сегмент '{segment}' с краевым пробелом или точкой";
            }

            foreach (var ch in segment) {
                if (ch is '*' or '?' or '"' or '<' or '>' or '|') {
                    return $"недопустимый символ '{ch}'";
                }
            }

            var dot = segment.IndexOf('.', StringComparison.Ordinal);
            var stem = dot >= 0 ? segment.Substring(0, dot) : segment;
            if (ReservedNames.Contains(stem)) {
                return $"зарезервированное имя устройства '{stem}'";
            }
        }

        return null;
    }

    /// <summary>
    /// Соединяет корень с относительным путём, проверив путь и убедившись, что
    /// результат физически лежит внутри корня.
    /// <para>
    /// Вторая проверка не дублирует первую: <see cref="Describe(string?)"/> смотрит
    /// на СТРОКУ пути, а здесь сверяется уже склеенный с корнем результат — так
    /// ловится выход за корень, который дала бы сама склейка (например корень,
    /// заданный вызывающим кодом с хвостом вроде <c>"C:/games/.."</c>).
    /// </para>
    /// <para>
    /// ЧЕГО ЭТА ПРОВЕРКА НЕ ДЕЛАЕТ: <c>Path.GetFullPath</c> нормализует путь
    /// ЛЕКСИЧЕСКИ и не разворачивает точки повторного разбора NTFS. Символическая
    /// ссылка или junction внутри корня уведёт наружу путь, прошедший обе проверки.
    /// Это осознанная граница: чтобы создать junction внутри папки установки, нужен
    /// доступ на запись в неё же — то есть ровно то, что даёт и прямую запись мимо
    /// апдейтера. Хотите закрыть и это — разворачивайте реальную цель
    /// (<c>ResolveLinkTarget(returnFinalTarget: true)</c>) у каждого существующего
    /// предка И у самого корня, иначе установка, лежащая ЗА junction'ом,
    /// перестанет обновляться целиком.
    /// </para>
    /// </summary>
    /// <param name="root">Корень (папка игры или папка установки).</param>
    /// <param name="relative">Относительный путь из манифеста.</param>
    /// <returns>Полный путь.</returns>
    /// <exception cref="ManifestPathException">Путь небезопасен либо выходит за корень.</exception>
    public static string Combine(string root, string relative) {
        var reason = Describe(relative);
        if (reason != null) {
            throw new ManifestPathException(relative, reason);
        }

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
            throw new ManifestPathException(relative, $"результат '{full}' выходит за пределы '{rootFull}'");
        }

        return full;
    }
}

/// <summary>
/// Относительный путь из манифеста не прошёл проверку. Отдельный тип, чтобы
/// вызывающий код отличал попытку выйти за пределы корня от обычной ошибки ввода-вывода.
/// </summary>
public class ManifestPathException : Exception {
    /// <summary>Initializes a new instance of the <see cref="ManifestPathException"/> class.</summary>
    public ManifestPathException() {
    }

    /// <summary>Initializes a new instance of the <see cref="ManifestPathException"/> class.</summary>
    /// <param name="message">Сообщение об ошибке.</param>
    public ManifestPathException(string message)
        : base(message) {
    }

    /// <summary>Initializes a new instance of the <see cref="ManifestPathException"/> class.</summary>
    /// <param name="message">Сообщение об ошибке.</param>
    /// <param name="innerException">Внутреннее исключение.</param>
    public ManifestPathException(string message, Exception innerException)
        : base(message, innerException) {
    }

    /// <summary>Initializes a new instance of the <see cref="ManifestPathException"/> class.</summary>
    /// <param name="path">Небезопасный путь.</param>
    /// <param name="reason">Причина отказа.</param>
    internal ManifestPathException(string path, string reason)
        : base($"Небезопасный путь в манифесте: '{path}' ({reason})") {
        this.Path = path;
        this.Reason = reason;
    }

    /// <summary>Gets путь, вызвавший отказ.</summary>
    public string Path { get; } = string.Empty;

    /// <summary>Gets причину отказа.</summary>
    public string Reason { get; } = string.Empty;
}
