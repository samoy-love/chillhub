// <copyright file="Ed25519Verifier.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Numerics;
    using System.Security.Cryptography;

    /// <summary>
    /// Минимальная проверка подписи Ed25519 (RFC 8032) на чистом .NET.
    /// <para>
    /// В .NET 8 Ed25519 в System.Security.Cryptography отсутствует, а тянуть
    /// BouncyCastle ради одной проверки при старте — лишняя зависимость в
    /// доверенном пути. Здесь реализована ТОЛЬКО verify: подписывать клиенту
    /// нечего, приватных ключей у него нет.
    /// </para>
    /// <para>
    /// Скорость не важна: одна проверка на манифест, единицы миллисекунд.
    /// Защита от тайминг-атак не нужна — все входные данные публичные.
    /// </para>
    /// </summary>
    internal static class Ed25519Verifier {
        /// <summary>Размер публичного ключа в байтах.</summary>
        public const int PublicKeySize = 32;

        /// <summary>Размер подписи в байтах.</summary>
        public const int SignatureSize = 64;

        // p = 2^255 - 19 — характеристика поля.
        private static readonly BigInteger P =
            BigInteger.Pow(2, 255) - 19;

        // L = 2^252 + 27742317777372353535851937790883648493 — порядок подгруппы.
        private static readonly BigInteger L =
            BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);

        // d = -121665/121666 mod p — параметр скрученной кривой Эдвардса.
        private static readonly BigInteger D =
            Mod(-121665 * Inv(121666));

        // 2^((p-1)/4) — константа для выбора корня при распаковке точки.
        private static readonly BigInteger SqrtM1 =
            BigInteger.ModPow(2, (P - 1) / 4, P);

        // Базовая точка B.
        private static readonly BigInteger ByConst = Mod(4 * Inv(5));
        private static readonly BigInteger BxConst = RecoverX(ByConst, 0) ?? BigInteger.Zero;

        /// <summary>
        /// Проверяет подпись Ed25519.
        /// </summary>
        /// <param name="signature">Подпись, 64 байта.</param>
        /// <param name="message">Подписанное сообщение.</param>
        /// <param name="publicKey">Публичный ключ, 32 байта.</param>
        /// <returns>true, если подпись верна.</returns>
        public static bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey) {
            if (signature.Length != SignatureSize || publicKey.Length != PublicKeySize) {
                return false;
            }

            try {
                // S должно быть в [0, L): иначе подпись «пластична» и её можно
                // модифицировать, не ломая проверку.
                var sBytes = signature.Slice(32, 32).ToArray();
                var s = new BigInteger(sBytes, isUnsigned: true, isBigEndian: false);
                if (s >= L) {
                    return false;
                }

                var a = DecodePoint(publicKey);
                var rPoint = DecodePoint(signature.Slice(0, 32));
                if (a is null || rPoint is null) {
                    return false;
                }

                // k = SHA-512(R || A || M) mod L
                var buf = new byte[32 + 32 + message.Length];
                signature.Slice(0, 32).CopyTo(buf.AsSpan(0, 32));
                publicKey.CopyTo(buf.AsSpan(32, 32));
                message.CopyTo(buf.AsSpan(64));
                var hash = SHA512.HashData(buf);
                var k = Mod(new BigInteger(hash, isUnsigned: true, isBigEndian: false), L);

                // Бескофакторное уравнение (как в crypto/ed25519 в Go): [S]B == R + [k]A.
                var left = ScalarMul(BasePoint(), s);
                var right = Add(rPoint.Value, ScalarMul(a.Value, k));
                return EncodePoint(left).AsSpan().SequenceEqual(EncodePoint(right));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) {
                return false;
            }
        }

        private static Point BasePoint() => new Point(BxConst, ByConst, BigInteger.One, Mod(BxConst * ByConst));

        private static BigInteger Mod(BigInteger x) => Mod(x, P);

        private static BigInteger Mod(BigInteger x, BigInteger m) {
            var r = x % m;
            return r.Sign < 0 ? r + m : r;
        }

        private static BigInteger Inv(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

        /// <summary>
        /// Восстанавливает координату x по y и знаковому биту либо возвращает null,
        /// если такой точки на кривой нет.
        /// </summary>
        private static BigInteger? RecoverX(BigInteger y, int sign) {
            if (y >= P) {
                return null;
            }

            var y2 = Mod(y * y);
            var num = Mod(y2 - 1);
            var den = Mod((D * y2) + 1);
            var x2 = Mod(num * Inv(den));
            if (x2.IsZero) {
                return sign == 0 ? BigInteger.Zero : (BigInteger?)null;
            }

            var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod((x * x) - x2).IsZero) {
                x = Mod(x * SqrtM1);
            }

            if (!Mod((x * x) - x2).IsZero) {
                return null;
            }

            if ((int)(x & 1) != sign) {
                x = Mod(-x);
            }

            return x;
        }

        private static Point? DecodePoint(ReadOnlySpan<byte> encoded) {
            var bytes = encoded.ToArray();
            int sign = (bytes[31] >> 7) & 1;
            bytes[31] &= 0x7F;
            var y = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
            var x = RecoverX(y, sign);
            if (x is null) {
                return null;
            }

            return new Point(x.Value, y, BigInteger.One, Mod(x.Value * y));
        }

        private static byte[] EncodePoint(Point p) {
            var zi = Inv(p.Z);
            var x = Mod(p.X * zi);
            var y = Mod(p.Y * zi);
            var outBytes = new byte[32];
            var yb = y.ToByteArray(isUnsigned: true, isBigEndian: false);
            Array.Copy(yb, outBytes, Math.Min(yb.Length, 32));
            outBytes[31] |= (byte)((int)(x & 1) << 7);
            return outBytes;
        }

        /// <summary>Сложение точек в расширенных координатах (RFC 8032, 5.1.4).</summary>
        private static Point Add(Point p, Point q) {
            var a = Mod((p.Y - p.X) * (q.Y - q.X));
            var b = Mod((p.Y + p.X) * (q.Y + q.X));
            var c = Mod(p.T * 2 * D * q.T);
            var dd = Mod(p.Z * 2 * q.Z);
            var e = Mod(b - a);
            var f = Mod(dd - c);
            var g = Mod(dd + c);
            var h = Mod(b + a);
            return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
        }

        private static Point ScalarMul(Point p, BigInteger k) {
            // Нейтральный элемент (0, 1).
            var result = new Point(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);
            if (k.Sign <= 0) {
                return result;
            }

            var addend = p;
            while (k.Sign > 0) {
                if (!(k & 1).IsZero) {
                    result = Add(result, addend);
                }

                addend = Add(addend, addend);
                k >>= 1;
            }

            return result;
        }

        /// <summary>Точка кривой в расширенных координатах: x = X/Z, y = Y/Z, xy = T/Z.</summary>
        private readonly struct Point {
            public Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t) {
                this.X = x;
                this.Y = y;
                this.Z = z;
                this.T = t;
            }

            public BigInteger X { get; }

            public BigInteger Y { get; }

            public BigInteger Z { get; }

            public BigInteger T { get; }
        }
    }
}
