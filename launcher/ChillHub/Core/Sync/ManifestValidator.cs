// <copyright file="ManifestValidator.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;

    using ChillHub.Update;

    /// <summary>
    /// Структурная проверка манифеста — до подписи, до загрузки, до любой записи на диск.
    /// <para>
    /// Подпись отвечает на вопрос «манифест выпущен нашим сервером?», но НЕ на вопрос
    /// «манифест осмысленный?». Между этими вопросами лежит целый класс атак: путь
    /// с <c>..</c> пишет файл в автозагрузку, дубликат пути позволяет менять результат
    /// не трогая подпись. Поэтому манифест сначала проверяется на форму и только
    /// потом используется.
    /// </para>
    /// <para>
    /// Отказ всегда касается манифеста ЦЕЛИКОМ. Пропустить «плохую» запись и
    /// обработать остальные нельзя: манифест — это единое утверждение о составе
    /// сборки, и если часть его подделана, доверять остальному нет оснований.
    /// </para>
    /// </summary>
    public static class ManifestValidator {
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
            for (var i = 0; i < files.Count; i++) {
                var f = files[i];
                if (f is null) {
                    throw Fail(source, $"запись #{i} пустая");
                }

                var reason = ManifestPath.Describe(f.Path);
                if (reason != null) {
                    throw Fail(source, $"файл #{i}: путь '{f.Path}' отвергнут ({reason})");
                }
            }

            var dirs = manifest.EmptyDirs ?? new List<string>();
            for (var i = 0; i < dirs.Count; i++) {
                var reason = ManifestPath.Describe(dirs[i]);
                if (reason != null) {
                    throw Fail(source, $"пустой каталог #{i}: путь '{dirs[i]}' отвергнут ({reason})");
                }
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
    /// Манифест не прошёл структурную проверку. Отличается от
    /// <see cref="ManifestSignatureException"/>: там подпись, здесь содержимое.
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
