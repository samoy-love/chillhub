// <copyright file="ManifestValidator.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;

    using ChillHub.Update;

    /// <summary>
    /// Структурная проверка манифеста — до загрузки, до любой записи на диск.
    /// <para>
    /// Манифест определяет, какие файлы и по каким путям окажутся на диске, а
    /// значит какие исполняемые файлы запустит пользователь. Путь с <c>..</c>
    /// кладёт файл в автозагрузку; две записи на один путь делают результат
    /// зависимым от порядка в JSON; запись без хешей ставится вообще без проверки
    /// целостности. Поэтому манифест сначала проверяется на форму и только потом
    /// используется — независимо от того, откуда он получен.
    /// </para>
    /// <para>
    /// Отказ всегда касается манифеста ЦЕЛИКОМ. Пропустить «плохую» запись и
    /// обработать остальные нельзя: манифест — это единое утверждение о составе
    /// сборки, и если часть его подделана, доверять остальному нет оснований.
    /// </para>
    /// </summary>
    public static class ManifestValidator {
        /// <summary>
        /// Единый текст для пользователя, когда манифест не прошёл проверку.
        /// Технические подробности (URL, конкретный путь, номер записи) остаются
        /// в логе: здесь только суть и что делать.
        /// </summary>
        public const string UserMessage =
            "Список файлов, полученный от сервера, не прошёл проверку и был отклонён. "
            + "Устанавливать его небезопасно. Свяжитесь с поддержкой.";

        /// <summary>
        /// Проверяет манифест и бросает исключение при первой же проблеме.
        /// <para>
        /// Проверка НЕ зависит от режима совместимости: неподписанный манифест
        /// работать имеет право, манифест с опасным путём — нет, никогда.
        /// </para>
        /// </summary>
        /// <param name="manifest">Манифест.</param>
        /// <param name="source">URL или описание источника — попадает в текст ошибки и в лог.</param>
        /// <exception cref="ManifestValidationException">Манифест не прошёл проверку.</exception>
        public static void Validate(Manifest manifest, string source) {
            ArgumentNullException.ThrowIfNull(manifest);

            var files = manifest.Files ?? new List<ManifestFile>();

            // Регистронезависимо — ровно как ключуется словарь в планировщике и как
            // ведёт себя файловая система Windows: "A.dll" и "a.dll" — один файл.
            var seenFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++) {
                var f = files[i];
                if (f is null) {
                    throw Fail(source, $"запись #{i} пустая");
                }

                var reason = ManifestPath.Describe(f.Path);
                if (reason != null) {
                    throw Fail(source, $"файл #{i}: путь '{f.Path}' отвергнут ({reason})");
                }

                // Подпись инвариантна к перестановке записей (список сортируется),
                // а планировщик оставляет ПОСЛЕДНЮЮ. Значит две записи на один путь
                // позволяют выбрать, какой файл получит пользователь, не трогая подпись.
                if (seenFiles.TryGetValue(f.Path, out var prev)) {
                    throw Fail(source, $"путь '{f.Path}' встречается дважды (записи #{prev} и #{i})");
                }

                // Запись без единого хеша скачивается и устанавливается вообще без
                // проверки целостности: в загрузчике блок верификации целиком
                // обёрнут в «если хоть один хеш задан». Пустые хеши — это не
                // «нет данных», а выключенная проверка ровно для того файла,
                // который выберет тот, кто раздаёт манифест.
                if (string.IsNullOrWhiteSpace(f.Blake3) && string.IsNullOrWhiteSpace(f.Sha256)) {
                    throw Fail(source, $"файл #{i} ('{f.Path}'): нет ни одного хеша, проверить целостность нечем");
                }

                seenFiles[f.Path] = i;
            }

            var dirs = manifest.EmptyDirs ?? new List<string>();
            var seenDirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < dirs.Count; i++) {
                // Завершающий слеш у каталога — обычная и однозначная запись: "a/b/" и
                // "a/b" описывают ОДИН каталог, и планировщик всё равно приводит их к
                // одному виду (NormalizeRelPath), то есть проверяем и используем одно
                // и то же. Для файлов такая вольность недопустима — там строка решает,
                // какой именно файл окажется на диске, — но для каталога выбора нет.
                //
                // Без этого послабления клиент отвергал уже опубликованные манифесты:
                // у drive-beyond-horizons пустой каталог записан со слешем на конце,
                // и игра переставала устанавливаться вовсе.
                var dir = (dirs[i] ?? string.Empty).TrimEnd('/', '\\');

                var reason = ManifestPath.Describe(dir);
                if (reason != null) {
                    throw Fail(source, $"пустой каталог #{i}: путь '{dirs[i]}' отвергнут ({reason})");
                }

                if (seenDirs.TryGetValue(dir, out var prevDir)) {
                    throw Fail(source, $"пустой каталог '{dirs[i]}' встречается дважды (записи #{prevDir} и #{i})");
                }

                seenDirs[dir] = i;
            }
        }

        private static ManifestValidationException Fail(string source, string detail) {
            var message = $"Манифест отвергнут ({source}): {detail}.";
            try {
                ChillHub.Core.Logging.Logger.Error(new ManifestValidationException(message), "ManifestValidator");
            }
            catch {
                // Логирование не должно мешать отказу.
            }

            return new ManifestValidationException(message);
        }
    }

    /// <summary>
    /// Манифест не прошёл структурную проверку: опасный путь, дубликат записи
    /// или отсутствие хешей, по которым нечего проверять.
    /// </summary>
    public class ManifestValidationException : Exception {
        /// <summary>Initializes a new instance of the <see cref="ManifestValidationException"/> class.</summary>
        public ManifestValidationException() {
        }

        /// <summary>Initializes a new instance of the <see cref="ManifestValidationException"/> class.</summary>
        /// <param name="message">Сообщение об ошибке.</param>
        public ManifestValidationException(string message)
            : base(message) {
        }

        /// <summary>Initializes a new instance of the <see cref="ManifestValidationException"/> class.</summary>
        /// <param name="message">Сообщение об ошибке.</param>
        /// <param name="innerException">Внутреннее исключение.</param>
        public ManifestValidationException(string message, Exception innerException)
            : base(message, innerException) {
        }
    }
}
