// <copyright file="SelfUpdateVersions.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;
    using System.IO;
    using System.Reflection;
    using System.Text.RegularExpressions;

    using ChillHub.Core.Sync;
    using ChillHub.Update;

    /// <summary>
    /// Работа с номерами версий и путями манифеста: проверка версии, пришедшей с сервера,
    /// чтение установленной версии, strip-prefix пакета и запись маркера версии.
    /// </summary>
    internal static class SelfUpdateVersions {
        /// <summary>
        /// A6. Допустимая форма версии: 1.2.3, 1.2.3.4, 1.2.3-beta.1.
        /// <para>
        /// Строка приходит из latest.json, то есть С СЕТИ, и дальше подставляется в
        /// Path.Combine (%TEMP%\ChillHub\SelfUpdate\&lt;версия&gt;), в URL манифеста и в
        /// аргументы внешнего процесса. Значение вроде "..\..\Startup" уводит каталог
        /// обновления куда угодно, а любой сюрприз в кавычках/бэкслешах меняет разбор
        /// командной строки апдейтера. Проверяем ДО первого использования: версия —
        /// это данные, а не команда.
        /// </para>
        /// </summary>
        private static readonly Regex VersionPattern = new Regex(
            @"^[0-9]{1,6}(\.[0-9]{1,6}){1,3}(-[0-9A-Za-z][0-9A-Za-z.]{0,31})?$",
            RegexOptions.CultureInvariant);

        /// <summary>A6. Проверяет, что строка версии безопасна для пути, URL и аргументов.</summary>
        /// <param name="version">Версия из latest.json.</param>
        /// <returns>true, если версия допустима.</returns>
        internal static bool IsValidVersion(string? version) {
            var v = (version ?? string.Empty).Trim();
            return v.Length > 0 && v.Length <= 64 && VersionPattern.IsMatch(v);
        }

        /// <summary>
        /// Установленная версия. Предпочитаем маркер, который пишет апдейтер, и только
        /// при его отсутствии (или ошибке чтения) откатываемся на версию сборки.
        /// </summary>
        /// <param name="installDir">Папка установки лаунчера.</param>
        /// <returns>Номер версии либо пустая строка, если её узнать не удалось.</returns>
        internal static string ReadLocalVersion(string installDir) {
            // Prefer a version marker written by updater; fallback to assembly version
            try {
                var markerPath = Path.Combine(installDir, "launcher.version");
                if (File.Exists(markerPath)) {
                    return (File.ReadAllText(markerPath) ?? string.Empty).Trim();
                }

                return AssemblyVersion();
            }
            catch {
                return AssemblyVersion();
            }
        }

        /// <summary>
        /// Определяет общий корневой каталог всех путей манифеста (strip-prefix).
        /// Пустая строка — файлы лежат в корне пакета (текущий случай).
        /// </summary>
        /// <param name="mf">Манифест целевой версии.</param>
        /// <returns>Имя общей корневой папки либо пустая строка.</returns>
        internal static string ComputeStripPrefix(Manifest mf) {
            string? candidate = null;
            foreach (var f in mf.Files) {
                var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                if (rel.Length == 0) {
                    continue;
                }

                var idx = rel.IndexOf('/', StringComparison.Ordinal);
                if (idx <= 0) {
                    // Есть файл в корне пакета — значит общей корневой папки нет.
                    return string.Empty;
                }

                var seg = rel.Substring(0, idx);
                if (candidate == null) {
                    candidate = seg;
                }
                else if (!candidate.Equals(seg, StringComparison.OrdinalIgnoreCase)) {
                    return string.Empty;
                }
            }

            return candidate ?? string.Empty;
        }

        /// <summary>Переводит путь из манифеста в путь относительно папки установки.</summary>
        /// <param name="stripPrefix">Общая корневая папка пакета (может быть пустой).</param>
        /// <param name="rel">Путь из манифеста.</param>
        /// <returns>Путь относительно папки установки.</returns>
        internal static string StripLocal(string stripPrefix, string rel) {
            var norm = (rel ?? string.Empty).Replace('\\', '/').Trim('/');
            if (stripPrefix.Length == 0) {
                return norm;
            }

            return norm.StartsWith(stripPrefix + "/", StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(stripPrefix.Length + 1)
                : norm;
        }

        /// <summary>
        /// A12. Сравнивает один файл манифеста с ФАКТИЧЕСКИМ файлом в папке установки.
        /// Возвращает true, если файл на месте и совпадает (по хешам, а при их отсутствии — по размеру).
        /// Любая ошибка чтения трактуется как «не совпадает»: лучше лишний раз скачать файл,
        /// чем оставить установку в неконсистентном состоянии.
        /// </summary>
        /// <param name="baseDir">Папка установки лаунчера.</param>
        /// <param name="stripPrefix">Общая корневая папка пакета.</param>
        /// <param name="f">Запись манифеста.</param>
        /// <param name="reason">Человекочитаемая причина расхождения (для лога).</param>
        /// <param name="ct">Токен отмены: подсчёт хеша большого файла — это минуты.</param>
        /// <returns>true, если локальный файл соответствует манифесту.</returns>
        internal static bool LocalFileMatches(
            string baseDir,
            string stripPrefix,
            ManifestFile f,
            out string reason,
            System.Threading.CancellationToken ct = default) {
            reason = string.Empty;
            try {
                var rel = (f.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                if (rel.Length == 0) {
                    return true;
                }

                var localRel = StripLocal(stripPrefix, rel);
                var localPath = Path.Combine(baseDir, localRel.Replace('/', Path.DirectorySeparatorChar));

                // Р5. Раньше здесь лежала своя копия цикла хеширования — она успела разойтись
                // с копией планировщика игр (не проверяла отмену). Разные вердикты на одинаковых
                // входах приводили к тому, что лаунчер бесконечно предлагал одно и то же обновление.
                // Теперь и сверка самообновления, и сверка файлов игры идут через FileHasher.
                return FileHasher.Matches(localPath, f.Size, f.Sha256, f.Blake3, out reason, ct);
            }
            catch (OperationCanceledException) {
                // Отмену не глушим: иначе отменённая проверка «подтвердила» бы битый файл.
                throw;
            }
            catch (Exception ex) {
                // Через Describe, а не по Message: у пропавшей СБОРКИ он пуст, и в
                // журнале стояло «reason=io_error » — двести семьдесят четыре строки,
                // не называющие ни причины, ни места, где смотреть.
                reason = $"io_error {Sync.ExceptionText.Describe(ex)}";
                return false;
            }
        }

        /// <summary>
        /// A7. Пишет маркер версии АТОМАРНО.
        /// <para>
        /// File.WriteAllText — это truncate + write: между ними файл существует и он
        /// пустой. Обрыв ровно в этот момент оставляет пустой launcher.version, а
        /// пустой маркер лаунчер читает как «версия неизвестна» — и обновление после
        /// этого не предлагается уже НИКОГДА. Поэтому содержимое сначала целиком
        /// ложится во временный файл рядом и лишь потом подменяет маркер.
        /// </para>
        /// </summary>
        /// <param name="installDir">Папка установки лаунчера.</param>
        /// <param name="version">Версия для записи.</param>
        /// <param name="error">Текст ошибки, если запись не удалась.</param>
        /// <returns>true, если маркер записан.</returns>
        internal static bool TryWriteVersionMarker(string installDir, string version, out string error) {
            error = string.Empty;
            try {
                var marker = Path.Combine(installDir, "launcher.version");
                AtomicFile.WriteAllText(marker, (version ?? string.Empty).Trim(), SelfUpdateRules.Utf8NoBom);
                return true;
            }
            catch (Exception ex) {
                error = ex.Message;
                try {
                    Logging.Logger.Error(ex, "UpdateWindow.WriteVersionMarker");
                }
                catch {
                }

                return false;
            }
        }

        private static string AssemblyVersion() {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm?.GetName()?.Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
        }
    }
}
