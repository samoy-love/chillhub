// <copyright file="FileHashCacheTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Кеш хешей решает, перечитывать ли файл с диска. Ошибка здесь не видна глазом:
    /// изменившийся файл просто «подтвердится» старым хешем, и игра молча останется битой.
    /// </summary>
    public class FileHashCacheTests {
        private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string B3 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Fact]
        public void TryGet_ПопаданиеПриСовпаденииРазмераИВремени() {
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/data.pak", 1024, 638000000000000000L, Sha, B3);
            cache.PruneAndSave(new List<string> { "Game/data.pak" });

            // Перечитываем кеш с диска — так же, как это делает следующий запуск лаунчера.
            var reloaded = scope.Load();
            var hit = reloaded.TryGet("Game/data.pak", 1024, 638000000000000000L, out var sha, out var b3);

            Assert.True(hit);
            Assert.Equal(Sha, sha);
            Assert.Equal(B3, b3);
        }

        [Fact]
        public void TryGet_ПромахПриИзменившемсяРазмере() {
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/data.pak", 1024, 638000000000000000L, Sha, B3);

            Assert.False(cache.TryGet("Game/data.pak", 2048, 638000000000000000L, out var sha, out var b3));
            Assert.Equal(string.Empty, sha);
            Assert.Equal(string.Empty, b3);
        }

        [Fact]
        public void TryGet_ПромахПриИзменившемсяВремениМодификации() {
            // Самый опасный случай: размер тот же, а содержимое другое.
            // Если кеш проигнорирует mtime, испорченный файл будет считаться исправным.
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/data.pak", 1024, 638000000000000000L, Sha, B3);

            Assert.False(cache.TryGet("Game/data.pak", 1024, 638000000000000001L, out _, out _));
        }

        [Fact]
        public void TryGet_ПромахДляНеизвестногоФайла() {
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/data.pak", 1024, 1, Sha, B3);

            Assert.False(cache.TryGet("Game/other.pak", 1024, 1, out _, out _));
        }

        [Fact]
        public void TryGet_КлючНечувствителенКРегистру() {
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/Data.pak", 1024, 1, Sha, B3);

            // Пути на Windows регистронезависимы; иначе один и тот же файл перечитывался бы каждый раз.
            Assert.True(cache.TryGet("game/data.PAK", 1024, 1, out _, out _));
        }

        [Fact]
        public void Load_БитыйJsonТрактуетсяКакПустойКешБезИсключения() {
            using var scope = new HashCacheScope();
            scope.WriteRawCache("{ это вообще не json ");

            var cache = scope.Load();

            Assert.False(cache.TryGet("Game/data.pak", 1024, 1, out _, out _));
        }

        [Fact]
        public void Load_ОбрезанныйJsonТрактуетсяКакПустойКешБезИсключения() {
            using var scope = new HashCacheScope();

            // Типичный результат обрыва записи: файл оборван на середине.
            scope.WriteRawCache("{\"version\":1,\"entries\":{\"Game/data.pak\":{\"size\":1024,\"mti");

            var cache = scope.Load();

            Assert.False(cache.TryGet("Game/data.pak", 1024, 1, out _, out _));
        }

        [Fact]
        public void Load_ЧужаяВерсияФорматаТрактуетсяКакПустойКеш() {
            using var scope = new HashCacheScope();
            scope.WriteRawCache("{\"version\":99,\"entries\":{\"Game/data.pak\":{\"size\":1024,\"mtime\":1,\"sha256\":\"" + Sha + "\",\"blake3\":\"" + B3 + "\"}}}");

            var cache = scope.Load();

            Assert.False(cache.TryGet("Game/data.pak", 1024, 1, out _, out _));
        }

        [Fact]
        public void Load_ПустойФайлКешаНеРонаетЗагрузку() {
            using var scope = new HashCacheScope();
            scope.WriteRawCache(string.Empty);

            var cache = scope.Load();

            Assert.False(cache.TryGet("Game/data.pak", 1024, 1, out _, out _));
        }

        [Fact]
        public void Load_ЗаписьБезХешейСчитаетсяНевалидной() {
            using var scope = new HashCacheScope();
            scope.WriteRawCache("{\"version\":1,\"entries\":{\"Game/data.pak\":{\"size\":1024,\"mtime\":1}}}");

            var cache = scope.Load();

            // Размер и время совпали, но хешей нет — брать из кеша нечего.
            Assert.False(cache.TryGet("Game/data.pak", 1024, 1, out _, out _));
        }

        [Fact]
        public void PruneAndSave_ПишетКорректныйJsonИНеОставляетВременныйФайл() {
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/data.pak", 1024, 1, Sha, B3);
            cache.PruneAndSave(new List<string> { "Game/data.pak" });

            Assert.True(File.Exists(scope.CacheFile));

            // Запись идёт через .tmp + File.Move: после успешного сохранения временного файла быть не должно.
            Assert.False(File.Exists(scope.CacheFile + ".tmp"));

            // Файл на диске обязан быть валидным JSON — иначе следующий запуск потеряет весь кеш.
            using var doc = JsonDocument.Parse(File.ReadAllText(scope.CacheFile));
            Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            Assert.True(doc.RootElement.GetProperty("entries").TryGetProperty("Game/data.pak", out _));
        }

        [Fact]
        public void PruneAndSave_ОставшийсяОтСбояTmpНеЛоматКеш() {
            using var scope = new HashCacheScope();

            // Имитируем обрыв на предыдущей записи: рядом лежит мусорный временный файл
            // (суффикс ChillHub.Update.AtomicFile.TempSuffix — тем же приёмом, которым уже
            // пользуется самообновление, PruneAndSave теперь пишет тоже).
            Directory.CreateDirectory(HashCacheScope.CacheDir);
            File.WriteAllText(scope.CacheFile + ChillHub.Update.AtomicFile.TempSuffix, "полузаписанный мусор");

            var cache = scope.Load();
            cache.Set("Game/data.pak", 1024, 1, Sha, B3);
            cache.PruneAndSave(new List<string> { "Game/data.pak" });

            var reloaded = scope.Load();
            Assert.True(reloaded.TryGet("Game/data.pak", 1024, 1, out _, out _));
            Assert.False(File.Exists(scope.CacheFile + ChillHub.Update.AtomicFile.TempSuffix));
        }

        [Fact]
        public void PruneAndSave_ВыбрасываетЗаписиОбИсчезнувшихФайлах() {
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/alive.pak", 1, 1, Sha, B3);
            cache.Set("Game/gone.pak", 2, 2, Sha, B3);

            // Файла gone.pak на диске больше нет — запись о нём должна уйти.
            cache.PruneAndSave(new List<string> { "Game/alive.pak" });

            Assert.False(cache.TryGet("Game/gone.pak", 2, 2, out _, out _));

            var reloaded = scope.Load();
            Assert.True(reloaded.TryGet("Game/alive.pak", 1, 1, out _, out _));
            Assert.False(reloaded.TryGet("Game/gone.pak", 2, 2, out _, out _));
        }

        [Fact]
        public void PruneAndSave_ПустойСписокЖивыхФайловОчищаетКешПолностью() {
            using var scope = new HashCacheScope();
            var cache = scope.Load();
            cache.Set("Game/a.pak", 1, 1, Sha, B3);
            cache.Set("Game/b.pak", 2, 2, Sha, B3);
            cache.PruneAndSave(new List<string>());

            var reloaded = scope.Load();
            Assert.False(reloaded.TryGet("Game/a.pak", 1, 1, out _, out _));
            Assert.False(reloaded.TryGet("Game/b.pak", 2, 2, out _, out _));
        }

        [Fact]
        public void Remove_УдаляетКешТолькоУказаннойИгры() {
            using var one = new HashCacheScope("one");
            using var two = new HashCacheScope("two");

            var c1 = one.Load();
            c1.Set("a.pak", 1, 1, Sha, B3);
            c1.PruneAndSave(new List<string> { "a.pak" });

            var c2 = two.Load();
            c2.Set("a.pak", 1, 1, Sha, B3);
            c2.PruneAndSave(new List<string> { "a.pak" });

            FileHashCache.Remove(one.GameId);

            Assert.False(File.Exists(one.CacheFile));
            Assert.True(File.Exists(two.CacheFile));
            Assert.True(two.Load().TryGet("a.pak", 1, 1, out _, out _));
        }

        [Fact]
        public void Remove_НесуществующийКешНеПриводитКОшибке() {
            using var scope = new HashCacheScope();

            // Файла кеша ещё нет — удаление обязано быть безобидным.
            FileHashCache.Remove(scope.GameId);
            FileHashCache.Remove(scope.GameId);
        }

        [Fact]
        public void Load_ПустойИдентификаторИгрыДаётКешВПамятиБезФайла() {
            var cache = FileHashCache.Load(string.Empty, @"C:\games\none");
            cache.Set("a.pak", 1, 1, Sha, B3);

            // Запись на диск невозможна (пути нет), но падать не должно, а память работает.
            cache.PruneAndSave(new List<string> { "a.pak" });
            Assert.True(cache.TryGet("a.pak", 1, 1, out _, out _));
        }

        [Fact]
        public void Load_ИдентификаторСНедопустимымиСимволамиСохраняетсяВБезопасныйФайл() {
            var gameId = "game/id:with*bad?chars-" + System.Guid.NewGuid().ToString("N");
            try {
                var cache = FileHashCache.Load(gameId, @"C:\games\bad");
                cache.Set("a.pak", 1, 1, Sha, B3);
                cache.PruneAndSave(new List<string> { "a.pak" });

                Assert.True(FileHashCache.Load(gameId, @"C:\games\bad").TryGet("a.pak", 1, 1, out _, out _));
            }
            finally {
                FileHashCache.Remove(gameId);
            }
        }
    }
}
