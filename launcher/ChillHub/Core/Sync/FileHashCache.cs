// <copyright file="FileHashCache.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Кеш посчитанных хешей локальных файлов игры.
    /// Ключ записи — относительный путь файла; запись считается валидной,
    /// если совпали размер и время последней модификации (UTC).
    /// Хранится отдельным файлом на каждую игру в %APPDATA%\ChillHub\hashcache\{gameId}.json:
    /// так удаление игры не задевает кеши остальных, файл остаётся небольшим
    /// (быстрее читается/пишется), а повреждение одного файла не ломает все игры сразу.
    /// </summary>
    public sealed class FileHashCache {
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly object gate = new object();
        private readonly Dictionary<string, CacheEntry> entries;
        private readonly string? filePath;
        private bool dirty;

        private FileHashCache(string? filePath, Dictionary<string, CacheEntry> entries) {
            this.filePath = filePath;
            this.entries = entries;
        }

        /// <summary>
        /// Загружает кеш игры. Битый или отсутствующий файл — это не ошибка:
        /// возвращается пустой кеш, хеши будут пересчитаны заново.
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        /// <returns>Кеш хешей (возможно пустой).</returns>
        public static FileHashCache Load(string gameId) {
            var path = GetCachePath(gameId);
            if (path == null) {
                return new FileHashCache(null, new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase));
            }

            try {
                if (File.Exists(path)) {
                    var json = File.ReadAllText(path);
                    var model = JsonSerializer.Deserialize<CacheFile>(json, JsonOptions);
                    if (model != null && model.Version == CurrentVersion && model.Entries != null) {
                        return new FileHashCache(path, new Dictionary<string, CacheEntry>(model.Entries, StringComparer.OrdinalIgnoreCase));
                    }

                    ChillHub.Core.Logging.Logger.Warn($"FileHashCache: несовместимый кеш gid={gameId}, будет пересоздан");
                }
            }
            catch (Exception ex) {
                // Битый кеш не должен ронять проверку игры — просто начинаем с чистого листа
                ChillHub.Core.Logging.Logger.Error(ex, $"FileHashCache.Load({gameId})");
            }

            return new FileHashCache(path, new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Удаляет кеш игры (например, после удаления локальных файлов).
        /// </summary>
        /// <param name="gameId">Идентификатор игры.</param>
        public static void Remove(string gameId) {
            try {
                var path = GetCachePath(gameId);
                if (path != null && File.Exists(path)) {
                    File.Delete(path);
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"FileHashCache.Remove({gameId})");
            }
        }

        /// <summary>
        /// Пытается взять готовые хеши для файла с указанными размером и временем модификации.
        /// </summary>
        /// <param name="relativePath">Относительный путь файла.</param>
        /// <param name="size">Текущий размер файла.</param>
        /// <param name="modifiedUtcTicks">Текущее время модификации (UTC, тики).</param>
        /// <param name="sha256">Хеш SHA-256 из кеша.</param>
        /// <param name="blake3">Хеш Blake3 из кеша.</param>
        /// <returns>true, если кеш содержит актуальную запись.</returns>
        public bool TryGet(string relativePath, long size, long modifiedUtcTicks, out string sha256, out string blake3) {
            sha256 = string.Empty;
            blake3 = string.Empty;
            lock (this.gate) {
                if (!this.entries.TryGetValue(relativePath, out var e) || e == null) {
                    return false;
                }

                if (e.Size != size || e.ModifiedUtcTicks != modifiedUtcTicks) {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(e.Sha256) || string.IsNullOrWhiteSpace(e.Blake3)) {
                    return false;
                }

                sha256 = e.Sha256!;
                blake3 = e.Blake3!;
                return true;
            }
        }

        /// <summary>
        /// Сохраняет посчитанные хеши файла в кеш (в памяти; на диск — при <see cref="PruneAndSave"/>).
        /// </summary>
        /// <param name="relativePath">Относительный путь файла.</param>
        /// <param name="size">Размер файла.</param>
        /// <param name="modifiedUtcTicks">Время модификации (UTC, тики).</param>
        /// <param name="sha256">Хеш SHA-256.</param>
        /// <param name="blake3">Хеш Blake3.</param>
        public void Set(string relativePath, long size, long modifiedUtcTicks, string sha256, string blake3) {
            lock (this.gate) {
                this.entries[relativePath] = new CacheEntry {
                    Size = size,
                    ModifiedUtcTicks = modifiedUtcTicks,
                    Sha256 = sha256,
                    Blake3 = blake3,
                };
                this.dirty = true;
            }
        }

        /// <summary>
        /// Выбрасывает записи о файлах, которых больше нет, и сохраняет кеш на диск при наличии изменений.
        /// </summary>
        /// <param name="alivePaths">Относительные пути существующих сейчас файлов.</param>
        public void PruneAndSave(ICollection<string> alivePaths) {
            try {
                lock (this.gate) {
                    if (alivePaths != null) {
                        var alive = new HashSet<string>(alivePaths, StringComparer.OrdinalIgnoreCase);
                        var stale = new List<string>();
                        foreach (var key in this.entries.Keys) {
                            if (!alive.Contains(key)) {
                                stale.Add(key);
                            }
                        }

                        foreach (var key in stale) {
                            this.entries.Remove(key);
                            this.dirty = true;
                        }
                    }

                    if (!this.dirty || this.filePath == null) {
                        return;
                    }

                    var model = new CacheFile { Version = CurrentVersion, Entries = this.entries };
                    var json = JsonSerializer.Serialize(model, JsonOptions);
                    // ChillHub.Update.AtomicFile — тот же приём временный-файл-и-подмена, которым
                    // уже пользуется самообновление для launcher.version, включая создание каталога.
                    ChillHub.Update.AtomicFile.WriteAllText(this.filePath, json, Core.SelfUpdate.SelfUpdateRules.Utf8NoBom);
                    this.dirty = false;
                }
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, "FileHashCache.PruneAndSave");
            }
        }

        private static string? GetCachePath(string gameId) {
            try {
                if (string.IsNullOrWhiteSpace(gameId)) {
                    return null;
                }

                var safe = SanitizeId(gameId);
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ChillHub",
                    "hashcache");
                return Path.Combine(dir, safe + ".json");
            }
            catch (Exception ex) {
                ChillHub.Core.Logging.Logger.Error(ex, $"FileHashCache.GetCachePath({gameId})");
                return null;
            }
        }

        private static string SanitizeId(string id) {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = id.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (Array.IndexOf(invalid, chars[i]) >= 0) {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private sealed class CacheFile {
            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("entries")]
            public Dictionary<string, CacheEntry>? Entries { get; set; }
        }

        private sealed class CacheEntry {
            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("mtime")]
            public long ModifiedUtcTicks { get; set; }

            [JsonPropertyName("sha256")]
            public string? Sha256 { get; set; }

            [JsonPropertyName("blake3")]
            public string? Blake3 { get; set; }
        }
    }
}
