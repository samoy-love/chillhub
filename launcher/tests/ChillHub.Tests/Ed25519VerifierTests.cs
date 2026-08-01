// <copyright file="Ed25519VerifierTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Проверка самописного Ed25519 на официальных векторах RFC 8032 §7.1.
    ///
    /// Реализация написана вручную (в .NET 8 штатного Ed25519 нет), и она стоит
    /// в доверенном пути: именно ей решать, запускать ли скачанный exe. Одного
    /// сквозного вектора, выпущенного своим же серверным кодом, для этого мало —
    /// он подтверждает лишь согласованность двух своих реализаций, а не
    /// соответствие стандарту. Поэтому здесь независимые эталонные векторы.
    /// </summary>
    public class Ed25519VerifierTests {
        // RFC 8032, §7.1, TEST 1: пустое сообщение.
        private const string Pub1 = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
        private const string Msg1 = "";
        private const string Sig1 = "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";

        // RFC 8032, §7.1, TEST 2: один байт.
        private const string Pub2 = "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c";
        private const string Msg2 = "72";
        private const string Sig2 = "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00";

        // RFC 8032, §7.1, TEST 3: два байта.
        private const string Pub3 = "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025";
        private const string Msg3 = "af82";
        private const string Sig3 = "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a";

        [Theory]
        [InlineData(Sig1, Msg1, Pub1)]
        [InlineData(Sig2, Msg2, Pub2)]
        [InlineData(Sig3, Msg3, Pub3)]
        public void RfcVectors_Verify(string sigHex, string msgHex, string pubHex) {
            Assert.True(Ed25519Verifier.Verify(Hex(sigHex), Hex(msgHex), Hex(pubHex)));
        }

        [Fact]
        public void TamperedMessage_Rejected() {
            var msg = Hex(Msg3);
            msg[0] ^= 0x01;
            Assert.False(Ed25519Verifier.Verify(Hex(Sig3), msg, Hex(Pub3)));
        }

        [Fact]
        public void TamperedSignature_Rejected() {
            var sig = Hex(Sig3);
            sig[0] ^= 0x01;
            Assert.False(Ed25519Verifier.Verify(sig, Hex(Msg3), Hex(Pub3)));
        }

        [Fact]
        public void WrongPublicKey_Rejected() {
            // Подпись из вектора 3 не должна проходить под чужим ключом.
            Assert.False(Ed25519Verifier.Verify(Hex(Sig3), Hex(Msg3), Hex(Pub2)));
        }

        [Fact]
        public void MalformedLengths_Rejected() {
            Assert.False(Ed25519Verifier.Verify(new byte[63], Hex(Msg3), Hex(Pub3)));
            Assert.False(Ed25519Verifier.Verify(Hex(Sig3), Hex(Msg3), new byte[31]));
            Assert.False(Ed25519Verifier.Verify(Array.Empty<byte>(), Hex(Msg3), Hex(Pub3)));
        }

        /// <summary>
        /// Малleability: S обязан лежать ниже порядка группы L. Реализация, которая
        /// не проверяет диапазон, примет S и S+L как одинаково валидные — то есть
        /// у одной и той же подписи появится второе представление.
        /// </summary>
        [Fact]
        public void NonCanonicalS_Rejected() {
            // L = 2^252 + 27742317777372353535851937790883648493, little-endian.
            var l = new byte[] {
                0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58,
                0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10,
            };

            var sig = Hex(Sig3);
            Assert.True(Ed25519Verifier.Verify(sig, Hex(Msg3), Hex(Pub3)));

            // S := S + L (little-endian сложение по 32 байтам).
            int carry = 0;
            for (int i = 0; i < 32; i++) {
                int sum = sig[32 + i] + l[i] + carry;
                sig[32 + i] = (byte)(sum & 0xff);
                carry = sum >> 8;
            }

            Assert.False(Ed25519Verifier.Verify(sig, Hex(Msg3), Hex(Pub3)));
        }

        private static byte[] Hex(string s) {
            if (s.Length == 0) {
                return Array.Empty<byte>();
            }

            var result = new byte[s.Length / 2];
            for (int i = 0; i < result.Length; i++) {
                result[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            }

            return result;
        }
    }
}
