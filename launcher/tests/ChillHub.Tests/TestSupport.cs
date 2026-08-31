// <copyright file="TestSupport.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Security.Cryptography;
    using System.Text;

    using ChillHub.Core.Sync;

    /// <summary>
    /// Глобальная настройка тестового прогона.
    /// Выполняется один раз при загрузке сборки тестов, до первого теста.
    /// </summary>
    internal static class TestEnvironment {
        [ModuleInitializer]
        internal static void Init() {
            // Логи клиента пишутся в %APPDATA%\ChillHub\logs. Тесты специально скармливают коду
            // битые данные, и засорять ими реальный лог пользователя не нужно.
            Environment.SetEnvironmentVariable("CHILLHUB_CLIENT_LOG", "0");

            // Logger.Error(Exception) дополнительно дёргает ErrorReporter, а тот уходит в сеть.
            // Тесты в сеть не ходят, поэтому глушим отправку переменной окружения:
            // тумблера в настройках у автоотчётов больше нет, они всегда включены.
            Environment.SetEnvironmentVariable(ChillHub.Core.ErrorReporter.EnvVar, "0");

            // ТО ЖЕ САМОЕ ДЛЯ СТАТИСТИКИ — и это не предосторожность, а починка.
            //
            // Заглушены были логи и автоотчёты, а метрики — нет, хотя тесты дёргают
            // тот же MetricsService: PlaytimeStore.FinishForTests закрывает сессию и
            // шлёт game_session, синхронизация модов шлёт mods_sync_failed. Адрес
            // сервера при этом берётся из конфига, а без конфига — из
            // AppConfig.DefaultApiBaseUrl, то есть прод. Раннер CI конфига не имеет
            // и потому слал события прямо на боевой /metrics/report, причём с новым
            // installId на каждый прогон: свежий профиль — свежий GUID.
            //
            // В панели это выглядело как живые игроки: сотни «игровых сессий» с
            // длительностями ровно из тестовых данных, десятки «уникальных
            // установок» и ни одного запуска игры, которого ни один тест не делает.
            Environment.SetEnvironmentVariable(ChillHub.Core.Metrics.MetricsService.EnvVar, "0");
        }
    }

    /// <summary>
    /// Временный каталог, живущий не дольше одного теста.
    /// Даёт изоляцию: тесты не зависят ни друг от друга, ни от порядка выполнения.
    /// </summary>
    internal sealed class TempDir : IDisposable {
        public TempDir() {
            this.Root = Path.Combine(Path.GetTempPath(), "chillhub-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.Root);
        }

        /// <summary>Полный путь к корню временного каталога.</summary>
        public string Root { get; }

        /// <summary>Создаёт файл по относительному пути (вместе с промежуточными папками).</summary>
        public string WriteFile(string relativePath, string content) {
            var full = Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return full;
        }

        /// <summary>Создаёт файл с точным набором байт (когда важен размер до байта).</summary>
        public string WriteBytes(string relativePath, byte[] content) {
            var full = Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
            return full;
        }

        public string PathTo(string relativePath)
            => Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose() {
            try {
                if (Directory.Exists(this.Root)) {
                    Directory.Delete(this.Root, recursive: true);
                }
            }
            catch {
                // Не смогли убрать временный каталог — это не повод валить тест.
            }
        }
    }

    /// <summary>
    /// Помощник для тестов, задевающих <see cref="ChillHub.Core.Sync.FileHashCache"/>.
    /// Путь к файлу кеша задан внутри продакшн-кода (%APPDATA%\ChillHub\hashcache\{gameId}.json)
    /// и наружу не выведен, поэтому изоляция достигается уникальным gameId на каждый тест
    /// плюс удалением файла кеша в Dispose.
    /// </summary>
    internal sealed class HashCacheScope : IDisposable {
        public HashCacheScope(string? prefix = null) {
            this.GameId = (prefix ?? "test") + "-" + Guid.NewGuid().ToString("N");
        }

        /// <summary>Уникальный идентификатор игры — гарантирует, что тесты не пересекаются.</summary>
        public string GameId { get; }

        /// <summary>
        /// Папка, для которой берётся кеш: он свой у каждой копии игры. Существовать на
        /// диске ей не обязательно — в имя файла едет только отметка пути.
        /// </summary>
        public string Root { get; set; } = @"C:\chillhub-tests\root";

        /// <summary>Кеш этой игры в этой папке.</summary>
        /// <param name="root">Другая папка, если нужна именно она.</param>
        /// <returns>Кеш хешей.</returns>
        public FileHashCache Load(string? root = null) => FileHashCache.Load(this.GameId, root ?? this.Root);

        /// <summary>Каталог, в котором лаунчер держит кеши хешей.</summary>
        public static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChillHub",
            "hashcache");

        /// <summary>Файл кеша текущей игры в её папке.</summary>
        public string CacheFile => FileHashCache.PathFor(this.GameId, this.Root)!;

        /// <summary>Кладёт на место файла кеша произвольное (в т.ч. заведомо битое) содержимое.</summary>
        public void WriteRawCache(string content) {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(this.CacheFile, content, new UTF8Encoding(false));
        }

        public void Dispose() {
            // Кеши игры во ВСЕХ папках: тест мог просить кеш и для соседней копии.
            FileHashCache.Remove(this.GameId);
            foreach (var path in new[] { this.CacheFile, this.CacheFile + ".tmp" }) {
                try {
                    if (File.Exists(path)) {
                        File.Delete(path);
                    }
                }
                catch {
                    // Уборка кеша — best effort.
                }
            }
        }
    }

    /// <summary>Небольшие утилиты для тестов синхронизации.</summary>
    internal static class TestHash {
        /// <summary>SHA-256 файла в том же виде, в котором его пишет манифест (hex, нижний регистр).</summary>
        public static string Sha256OfFile(string path) {
            using var sha = SHA256.Create();
            using var f = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(f)).ToLowerInvariant();
        }

        /// <summary>Blake3 файла (hex, нижний регистр).</summary>
        public static string Blake3OfFile(string path) {
            var bytes = File.ReadAllBytes(path);
            return Blake3.Hasher.Hash(bytes).ToString().ToLowerInvariant();
        }
    }
}
