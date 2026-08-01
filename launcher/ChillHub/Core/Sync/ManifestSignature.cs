// <copyright file="ManifestSignature.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Результат проверки подписи манифеста.
    /// </summary>
    public enum ManifestSignatureStatus {
        /// <summary>Подпись есть и она верна.</summary>
        Valid,

        /// <summary>Подписи нет вовсе (пустое поле или старая заглушка dev-mock-signature).</summary>
        Missing,

        /// <summary>Подпись есть, но проверить её нечем: в клиент не зашит публичный ключ.</summary>
        NoPublicKey,

        /// <summary>Подпись есть, но она не сходится с содержимым манифеста.</summary>
        Invalid,
    }

    /// <summary>
    /// Проверка подписи манифеста Ed25519.
    /// <para>
    /// Клиент скачивает и ЗАПУСКАЕТ исполняемые файлы, поэтому доверять одному
    /// лишь TLS нельзя: тот, кто получил доступ к раздаче контента, подсунет
    /// произвольный exe. Подпись привязывает содержимое манифеста к приватному
    /// ключу, который лежит только на сервере сборки.
    /// </para>
    /// <para>
    /// <b>Режим совместимости.</b> На раздаче ещё лежат манифесты со старой
    /// заглушкой, а у пользователей стоят версии лаунчера без проверки. Поэтому
    /// по умолчанию действует мягкий режим: НЕТ подписи — предупреждение в лог и
    /// работаем дальше; ЕСТЬ, но неверная — отказ. Когда все манифесты будут
    /// перевыпущены подписанными, включается строгий режим — см. <see cref="Strict"/>.
    /// </para>
    /// </summary>
    public static class ManifestSignature {
        /// <summary>
        /// Префикс реальной подписи. Всё, что без него (включая историческую
        /// заглушку "dev-mock-signature"), считается отсутствием подписи.
        /// </summary>
        public const string Prefix = "ed25519:";

        /// <summary>
        /// Версия схемы канонизации. Входит в подписываемые байты, поэтому её
        /// смена намеренно обесценивает старые подписи.
        /// </summary>
        public const string CanonicalVersion = "chillhub-manifest-v1";

        /// <summary>
        /// Публичный ключ проверки подписи (base64, 32 байта).
        /// <para>
        /// ЗАПОЛНИТЬ ПЕРЕД РЕЛИЗОМ: сгенерировать пару командой
        /// <c>go run ./internal/adminapi/builds/keygen</c> в каталоге <c>server</c>,
        /// приватную часть положить в <c>MANIFEST_SIGNING_KEY</c> на сервере,
        /// публичную — сюда. Приватный ключ в репозиторий не коммитить.
        /// </para>
        /// <para>
        /// Пока ключ пустой, подписанные манифесты проверить нечем: в мягком
        /// режиме это предупреждение, в строгом — отказ.
        /// </para>
        /// </summary>
        public const string PublicKeyBase64 = "jMkDIZ6gbdU5KEQNMRgOcDZf5JUNxEHrgFCPu11tvok=";

        /// <summary>
        /// Имя переменной окружения, включающей строгий режим (значение "1", "true" или "yes").
        /// </summary>
        public const string StrictEnvVar = "CHILLHUB_MANIFEST_STRICT";

        /// <summary>
        /// Единый текст для пользователя, когда манифест не прошёл проверку подписи.
        /// Технические подробности (URL, статус, стек) остаются в логе: здесь только
        /// суть проблемы и что делать. Держим в одном месте, чтобы формулировка
        /// не расползалась по страницам.
        /// </summary>
        public const string UserMessage =
            "Файлы не прошли проверку подлинности: содержимое раздачи не совпадает с подписью сервера. "
            + "Устанавливать их небезопасно — файлы могли быть подменены. Свяжитесь с поддержкой.";

        /// <summary>
        /// Значение строгого режима по умолчанию. Переключить в <c>true</c>
        /// (и выпустить новую версию лаунчера) после того, как ВСЕ манифесты на
        /// раздаче будут перевыпущены подписанными, иначе обновления сломаются.
        /// </summary>
        public const bool StrictByDefault = false;

        /// <summary>
        /// Gets a value indicating whether строгий режим включён: любой манифест
        /// без проверяемой подписи отвергается.
        /// </summary>
        public static bool Strict {
            get {
                var env = Environment.GetEnvironmentVariable(StrictEnvVar);
                if (!string.IsNullOrWhiteSpace(env)) {
                    var v = env.Trim();
                    if (v is "1" or "true" or "TRUE" or "yes" or "YES") {
                        return true;
                    }

                    if (v is "0" or "false" or "FALSE" or "no" or "NO") {
                        return false;
                    }
                }

                return StrictByDefault;
            }
        }

        /// <summary>
        /// Строит каноническое представление манифеста — ровно те байты, которые
        /// подписывает сервер (см. <c>server/internal/adminapi/builds/sign.go</c>).
        /// <para>
        /// Схема (строки через LF, в конце тоже LF):
        /// заголовок версии схемы, version, gameId, buildId, количество файлов,
        /// отсортированные по пути записи файлов (путь, размер, blake3, sha256,
        /// флаг исполняемости через TAB), количество пустых каталогов и они сами.
        /// Поля <c>createdAt</c> и <c>signature</c> НЕ подписываются.
        /// </para>
        /// </summary>
        /// <param name="manifest">Манифест.</param>
        /// <returns>Канонические байты (UTF-8).</returns>
        public static byte[] Canonicalize(Manifest manifest) {
            ArgumentNullException.ThrowIfNull(manifest);

            var sb = new StringBuilder();
            sb.Append(CanonicalVersion).Append('\n');
            sb.Append("version:").Append(manifest.Version ?? string.Empty).Append('\n');
            sb.Append("gameId:").Append(manifest.GameId ?? string.Empty).Append('\n');
            sb.Append("buildId:").Append(manifest.BuildId ?? string.Empty).Append('\n');

            var files = new List<ManifestFile>(manifest.Files ?? new List<ManifestFile>());
            var rows = new List<(string Path, string Blake3, string Line)>(files.Count);
            foreach (var f in files) {
                if (f is null) {
                    continue;
                }

                var path = CanonPath(f.Path);
                var blake3 = (f.Blake3 ?? string.Empty).Trim().ToLowerInvariant();
                var sha256 = (f.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
                var line = string.Concat(
                    "file:",
                    path,
                    "\t",
                    f.Size.ToString(CultureInfo.InvariantCulture),
                    "\t",
                    blake3,
                    "\t",
                    sha256,
                    "\t",
                    f.Executable ? "1" : "0");
                rows.Add((path, blake3, line));
            }

            // Ordinal-сортировка обязана совпадать с сортировкой байтовых строк в Go.
            rows.Sort((a, b) => {
                int c = string.CompareOrdinal(a.Path, b.Path);
                return c != 0 ? c : string.CompareOrdinal(a.Blake3, b.Blake3);
            });
            sb.Append("files:").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var r in rows) {
                sb.Append(r.Line).Append('\n');
            }

            var dirs = new List<string>();
            foreach (var d in manifest.EmptyDirs ?? new List<string>()) {
                dirs.Add(CanonPath(d));
            }

            dirs.Sort(string.CompareOrdinal);
            sb.Append("dirs:").Append(dirs.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var d in dirs) {
                sb.Append("dir:").Append(d).Append('\n');
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Проверяет подпись манифеста указанным публичным ключом.
        /// </summary>
        /// <param name="manifest">Манифест.</param>
        /// <param name="publicKeyBase64">Публичный ключ в base64; пустой — проверять нечем.</param>
        /// <returns>Статус проверки.</returns>
        public static ManifestSignatureStatus Check(Manifest manifest, string? publicKeyBase64) {
            ArgumentNullException.ThrowIfNull(manifest);

            var sig = (manifest.Signature ?? string.Empty).Trim();
            if (!sig.StartsWith(Prefix, StringComparison.Ordinal)) {
                // Пусто или старая заглушка dev-mock-signature — подписи нет.
                return ManifestSignatureStatus.Missing;
            }

            if (string.IsNullOrWhiteSpace(publicKeyBase64)) {
                return ManifestSignatureStatus.NoPublicKey;
            }

            byte[] key;
            byte[] sigBytes;
            try {
                key = Convert.FromBase64String(publicKeyBase64.Trim());
                sigBytes = Convert.FromBase64String(sig.Substring(Prefix.Length).Trim());
            }
            catch (FormatException) {
                return ManifestSignatureStatus.Invalid;
            }

            if (key.Length != Ed25519Verifier.PublicKeySize || sigBytes.Length != Ed25519Verifier.SignatureSize) {
                return ManifestSignatureStatus.Invalid;
            }

            return Ed25519Verifier.Verify(sigBytes, Canonicalize(manifest), key)
                ? ManifestSignatureStatus.Valid
                : ManifestSignatureStatus.Invalid;
        }

        /// <summary>
        /// Проверяет манифест зашитым в клиент ключом и применяет политику
        /// совместимости: неверная подпись — исключение всегда; отсутствующая или
        /// непроверяемая — предупреждение в лог, а в строгом режиме тоже исключение.
        /// </summary>
        /// <param name="manifest">Манифест.</param>
        /// <param name="source">Что проверяем (URL или описание) — попадает в лог и в текст ошибки.</param>
        /// <exception cref="ManifestSignatureException">Подпись неверна либо строгий режим не удовлетворён.</exception>
        public static void Enforce(Manifest manifest, string source) {
            // Структурная проверка идёт первой и НЕ зависит от режима совместимости.
            // Подпись отвечает на вопрос «манифест наш?», а не «манифест осмысленный?»:
            // путь с ".." или дубликат пути опасны независимо от того, кто их подписал.
            ManifestValidator.Validate(manifest, source);

            var status = Check(manifest, PublicKeyBase64);
            switch (status) {
                case ManifestSignatureStatus.Valid:
                    ChillHub.Core.Logging.Logger.Info($"Манифест подписан и проверен: {source}");
                    return;

                case ManifestSignatureStatus.Invalid:
                    // Отказ безусловный: подпись стоит, но содержимое ей не соответствует.
                    // Это либо подмена, либо порча по дороге — качать и запускать нельзя.
                    throw new ManifestSignatureException(
                        $"Подпись манифеста неверна ({source}). Загрузка отменена: файлы могли быть подменены.");

                case ManifestSignatureStatus.NoPublicKey:
                    if (Strict) {
                        throw new ManifestSignatureException(
                            $"Манифест подписан, но в лаунчер не зашит публичный ключ ({source}), а включён строгий режим.");
                    }

                    ChillHub.Core.Logging.Logger.Warn($"Манифест подписан, но проверить нечем: не задан ManifestSignature.PublicKeyBase64 ({source})");
                    return;

                default:
                    if (Strict) {
                        throw new ManifestSignatureException(
                            $"Манифест не подписан ({source}), а включён строгий режим проверки подписи.");
                    }

                    ChillHub.Core.Logging.Logger.Warn($"Манифест НЕ подписан — работаем в режиме совместимости ({source})");
                    return;
            }
        }

        /// <summary>
        /// Приводит путь манифеста к каноническому виду.
        /// <para>
        /// Одна реализация на весь проект (<see cref="ChillHub.Update.ManifestPath.Canonicalize"/>):
        /// разойдись канонизация подписи и канонизация записи на диск — и подпись
        /// снова начнёт покрывать не тот путь, который создаётся.
        /// </para>
        /// </summary>
        private static string CanonPath(string? p) => ChillHub.Update.ManifestPath.Canonicalize(p);
    }

    /// <summary>
    /// Манифест не прошёл проверку подписи. Отдельный тип, чтобы вызывающий код
    /// мог отличить проблему безопасности от обычной сетевой ошибки.
    /// </summary>
    public class ManifestSignatureException : Exception {
        /// <summary>Initializes a new instance of the <see cref="ManifestSignatureException"/> class.</summary>
        public ManifestSignatureException() {
        }

        /// <summary>Initializes a new instance of the <see cref="ManifestSignatureException"/> class.</summary>
        /// <param name="message">Сообщение об ошибке.</param>
        public ManifestSignatureException(string message)
            : base(message) {
        }

        /// <summary>Initializes a new instance of the <see cref="ManifestSignatureException"/> class.</summary>
        /// <param name="message">Сообщение об ошибке.</param>
        /// <param name="innerException">Внутреннее исключение.</param>
        public ManifestSignatureException(string message, Exception innerException)
            : base(message, innerException) {
        }
    }
}
