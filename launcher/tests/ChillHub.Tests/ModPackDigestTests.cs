// <copyright file="ModPackDigestTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System.Collections.Generic;

    using ChillHub.Core.Mods;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Отпечаток содержимого модпака — договор между лаунчером и сервером.
    /// <para>
    /// Сервер считает его при публикации (server/internal/adminapi/builds,
    /// <c>treeDigest</c>) и отдаёт в <c>/api/games</c>; лаунчер считает свой по
    /// установленному манифесту и сравнивает напрямую. Разъедутся реализации —
    /// лаунчер либо перестанет замечать пересборки (исправление останется на
    /// сервере), либо начнёт звать обновляться на каждой проверке. Обе поломки
    /// молчаливые, поэтому ожидаемое значение прибито с обеих сторон: здесь и в
    /// <c>treedigest_test.go</c>.
    /// </para>
    /// </summary>
    public class ModPackDigestTests {
        /// <summary>Тот же вектор, что и в тесте сервера.</summary>
        private const string Expected = "fffdf2012157dea2d60a57ffb6797bb8";

        /// <summary>Отпечаток совпадает с посчитанным сервером на том же дереве.</summary>
        [Fact]
        public void MatchesTheServerOnTheSameTree() {
            Assert.Equal(Expected, ModPackDigest.Of(Fixture()));
        }

        /// <summary>Порядок файлов в манифесте на отпечаток не влияет.</summary>
        [Fact]
        public void IgnoresFileOrder() {
            var shuffled = new Manifest {
                Files = new List<ManifestFile> {
                    Fixture().Files[2], Fixture().Files[0], Fixture().Files[1],
                },
            };
            Assert.Equal(Expected, ModPackDigest.Of(shuffled));
        }

        /// <summary>Изменившийся файл меняет отпечаток — иначе пересборка не доедет.</summary>
        [Fact]
        public void FollowsContent() {
            var changed = Fixture();
            changed.Files[0].Blake3 = "другой хеш";
            Assert.NotEqual(Expected, ModPackDigest.Of(changed));

            var added = Fixture();
            added.Files.Add(new ManifestFile { Path = "c.txt", Blake3 = "ddd" });
            Assert.NotEqual(Expected, ModPackDigest.Of(added));
        }

        /// <summary>Пустой манифест отпечатка не даёт: сравнивать было бы не с чем.</summary>
        [Fact]
        public void EmptyManifestHasNoDigest() {
            Assert.Equal(string.Empty, ModPackDigest.Of(null));
            Assert.Equal(string.Empty, ModPackDigest.Of(new Manifest()));
        }

        /// <summary>
        /// Тот же набор файлов, что в тесте сервера: порядок нарочно не отсортирован,
        /// и один путь не-ASCII — сортировка идёт по байтам UTF-8, и на кириллице
        /// расхождение между языками вылезло бы именно здесь.
        /// </summary>
        /// <returns>Манифест-образец.</returns>
        private static Manifest Fixture() => new() {
            Files = new List<ManifestFile> {
                new() { Path = "b.txt", Blake3 = "bbb" },
                new() { Path = "BepInEx/plugins/Автор-Мод/мод.dll", Blake3 = "aaa" },
                new() { Path = "a.txt", Blake3 = "ccc" },
            },
        };
    }
}
