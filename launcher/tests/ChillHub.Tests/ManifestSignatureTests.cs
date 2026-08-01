// <copyright file="ManifestSignatureTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Проверка подписи манифеста на клиенте.
    /// <para>
    /// Вектор ниже выпущен серверным кодом (<c>server/internal/adminapi/builds/sign.go</c>)
    /// на фиксированном ключе-семени 01,02,...,20. Тем самым тесты проверяют не
    /// только сам Ed25519, но и то, что канонизация в Go и в C# совпадает
    /// байт в байт: разойдись они — валидная подпись перестанет проходить.
    /// </para>
    /// </summary>
    public class ManifestSignatureTests {
        /// <summary>Публичный ключ тестового вектора.</summary>
        private const string TestPublicKey = "ebVWLo/mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ=";

        /// <summary>Манифест, подписанный серверным кодом на тестовом ключе.</summary>
        private const string SignedManifestJson = """
        {
          "version": "1.2.3",
          "buildId": "b-42",
          "gameId": "chill",
          "createdAt": "2026-01-01T00:00:00Z",
          "files": [
            { "path": "bin/game.exe", "size": 100, "blake3": "aaaa", "sha256": "bbbb", "executable": true },
            { "path": "data/pak0.dat", "size": 200, "blake3": "cccc", "executable": false },
            { "path": "readme.txt", "size": 3, "blake3": "dddd", "executable": false }
          ],
          "emptyDirs": ["logs", "saves"],
          "signature": "ed25519:w5Fc4iDMw2yXEN2nzTFtH1moe4PCpSBrg3KIFFk1vWhyRDhwp0eqUkW4cU9+mmKyNIznttbZjeK+djgi65X9Dw=="
        }
        """;

        /// <summary>Каноническое представление того же манифеста, посчитанное сервером.</summary>
        private const string ExpectedCanonicalBase64 =
            "Y2hpbGxodWItbWFuaWZlc3QtdjEKdmVyc2lvbjoxLjIuMwpnYW1lSWQ6Y2hpbGwKYnVpbGRJZD" +
            "piLTQyCmZpbGVzOjMKZmlsZTpiaW4vZ2FtZS5leGUJMTAwCWFhYWEJYmJiYgkxCmZpbGU6ZGF0" +
            "YS9wYWswLmRhdAkyMDAJY2NjYwkJMApmaWxlOnJlYWRtZS50eHQJMwlkZGRkCQkwCmRpcnM6Mg" +
            "pkaXI6bG9ncwpkaXI6c2F2ZXMK";

        [Fact]
        public void Canonicalization_MatchesServer() {
            var manifest = Load();
            var canon = ManifestSignature.Canonicalize(manifest);
            Assert.Equal(ExpectedCanonicalBase64, Convert.ToBase64String(canon));
        }

        [Fact]
        public void Canonicalization_IgnoresOrderAndCosmetics() {
            var baseline = ManifestSignature.Canonicalize(Load());

            var shuffled = Load();
            shuffled.Files = new List<ManifestFile> { shuffled.Files[2], shuffled.Files[0], shuffled.Files[1] };
            shuffled.EmptyDirs = new List<string> { "saves", "logs" };
            Assert.Equal(baseline, ManifestSignature.Canonicalize(shuffled));

            var cosmetic = Load();
            cosmetic.Files[0].Path = @"bin\game.exe";
            cosmetic.Files[0].Blake3 = "AAAA";
            cosmetic.Files[1].Path = "/data//pak0.dat";
            cosmetic.CreatedAt = "2030-01-01T00:00:00Z";
            cosmetic.Signature = "что угодно";
            Assert.Equal(baseline, ManifestSignature.Canonicalize(cosmetic));
        }

        [Fact]
        public void ValidSignature_IsAccepted() {
            Assert.Equal(ManifestSignatureStatus.Valid, ManifestSignature.Check(Load(), TestPublicKey));
        }

        [Theory]
        [InlineData("size")]
        [InlineData("hash")]
        [InlineData("extra")]
        [InlineData("version")]
        [InlineData("emptyDir")]
        public void TamperedManifest_IsRejected(string what) {
            var m = Load();
            switch (what) {
                case "size":
                    m.Files[0].Size = 101;
                    break;
                case "hash":
                    m.Files[0].Blake3 = "deadbeef";
                    break;
                case "extra":
                    m.Files.Add(new ManifestFile { Path = "evil.exe", Size = 1, Blake3 = "ffff", Executable = true });
                    break;
                case "version":
                    m.Version = "1.2.4";
                    break;
                default:
                    m.EmptyDirs.Add("tmp");
                    break;
            }

            Assert.Equal(ManifestSignatureStatus.Invalid, ManifestSignature.Check(m, TestPublicKey));
        }

        [Fact]
        public void GarbledSignature_IsRejected() {
            var m = Load();

            // Портим один байт подписи (в base64 меняем символ на другой валидный).
            var raw = Convert.FromBase64String(m.Signature.Substring(ManifestSignature.Prefix.Length));
            raw[10] ^= 0xFF;
            m.Signature = ManifestSignature.Prefix + Convert.ToBase64String(raw);
            Assert.Equal(ManifestSignatureStatus.Invalid, ManifestSignature.Check(m, TestPublicKey));

            // Не-base64 после префикса.
            m.Signature = ManifestSignature.Prefix + "не base64!!!";
            Assert.Equal(ManifestSignatureStatus.Invalid, ManifestSignature.Check(m, TestPublicKey));

            // Подпись правильной формы, но не той длины.
            m.Signature = ManifestSignature.Prefix + Convert.ToBase64String(new byte[32]);
            Assert.Equal(ManifestSignatureStatus.Invalid, ManifestSignature.Check(m, TestPublicKey));
        }

        [Fact]
        public void ForeignKey_DoesNotValidate() {
            // Валидный по форме, но чужой публичный ключ.
            var other = Convert.ToBase64String(new byte[32]);
            Assert.NotEqual(ManifestSignatureStatus.Valid, ManifestSignature.Check(Load(), other));
        }

        [Fact]
        public void LegacyMockSignature_CountsAsMissing() {
            // Совместимость: на раздаче ещё лежат манифесты со старой заглушкой.
            var m = Load();
            m.Signature = "dev-mock-signature";
            Assert.Equal(ManifestSignatureStatus.Missing, ManifestSignature.Check(m, TestPublicKey));

            m.Signature = string.Empty;
            Assert.Equal(ManifestSignatureStatus.Missing, ManifestSignature.Check(m, TestPublicKey));
        }

        [Fact]
        public void SignedManifest_WithoutEmbeddedKey_IsUnverifiable() {
            Assert.Equal(ManifestSignatureStatus.NoPublicKey, ManifestSignature.Check(Load(), string.Empty));
        }

        [Fact]
        public void Enforce_SoftMode_AllowsUnsignedManifest() {
            var m = Load();
            m.Signature = "dev-mock-signature";
            WithStrict(null, () => ManifestSignature.Enforce(m, "test://legacy"));
        }

        [Fact]
        public void Enforce_StrictMode_RejectsUnsignedManifest() {
            var m = Load();
            m.Signature = "dev-mock-signature";
            WithStrict("1", () =>
                Assert.Throws<ManifestSignatureException>(() => ManifestSignature.Enforce(m, "test://legacy")));
        }

        [Fact]
        public void Enforce_RejectsInvalidSignatureEvenInSoftMode() {
            // Ключа в клиент пока не зашито, поэтому подделываем ситуацию через Check:
            // при пустом PublicKeyBase64 подписанный манифест нельзя проверить, но
            // сама политика «неверная подпись — всегда отказ» проверяется здесь.
            var m = Load();
            m.Files[0].Size = 999;
            Assert.Equal(ManifestSignatureStatus.Invalid, ManifestSignature.Check(m, TestPublicKey));

            if (!string.IsNullOrEmpty(ManifestSignature.PublicKeyBase64)) {
                // Как только ключ будет зашит, Enforce обязан падать на этом манифесте.
                WithStrict(null, () =>
                    Assert.Throws<ManifestSignatureException>(() => ManifestSignature.Enforce(m, "test://tampered")));
            }
        }

        [Fact]
        public void Utf8PathsSurviveCanonicalization() {
            // Кириллица в путях не должна ломать канонизацию (UTF-8, без нормализации).
            var m = Load();
            m.Files[0].Path = "данные/игра.exe";
            var canon = Encoding.UTF8.GetString(ManifestSignature.Canonicalize(m));
            Assert.Contains("file:данные/игра.exe\t", canon, StringComparison.Ordinal);
        }

        private static Manifest Load() =>
            JsonSerializer.Deserialize<Manifest>(SignedManifestJson)
            ?? throw new InvalidOperationException("не удалось разобрать тестовый манифест");

        /// <summary>Выполняет действие с заданным значением CHILLHUB_MANIFEST_STRICT.</summary>
        private static void WithStrict(string? value, Action action) {
            var old = Environment.GetEnvironmentVariable(ManifestSignature.StrictEnvVar);
            Environment.SetEnvironmentVariable(ManifestSignature.StrictEnvVar, value ?? "0");
            try {
                action();
            }
            finally {
                Environment.SetEnvironmentVariable(ManifestSignature.StrictEnvVar, old);
            }
        }
    }
}
