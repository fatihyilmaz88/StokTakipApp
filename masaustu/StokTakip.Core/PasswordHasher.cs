using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StokTakip.Core;

/// <summary>
/// Python/werkzeug ile uyumlu şifre özeti. Web sürümünde açılan kullanıcılar (scrypt) masaüstünde,
/// masaüstünde açılanlar (pbkdf2) web sürümünde giriş yapabilir.
/// Biçim: "pbkdf2:sha256:600000$tuz$hex"  veya  "scrypt:32768:8:1$tuz$hex"
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const string SaltChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string Hash(string password)
    {
        var salt = new string(Enumerable.Range(0, 16).Select(_ => SaltChars[RandomNumberGenerator.GetInt32(SaltChars.Length)]).ToArray());
        var h = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt),
            Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2:sha256:{Iterations}${salt}${Convert.ToHexString(h).ToLowerInvariant()}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 3) return false;
            var method = parts[0].Split(':');
            var salt = Encoding.UTF8.GetBytes(parts[1]);
            var expected = Convert.FromHexString(parts[2]);
            var pw = Encoding.UTF8.GetBytes(password);
            byte[] actual;
            if (method[0] == "pbkdf2")
            {
                var alg = method.Length > 1 ? method[1] : "sha256";
                int it = method.Length > 2 ? int.Parse(method[2]) : Iterations;
                var name = alg switch
                {
                    "sha256" => HashAlgorithmName.SHA256,
                    "sha512" => HashAlgorithmName.SHA512,
                    "sha1" => HashAlgorithmName.SHA1,
                    _ => throw new NotSupportedException(alg),
                };
                actual = Rfc2898DeriveBytes.Pbkdf2(pw, salt, it, name, expected.Length);
            }
            else if (method[0] == "scrypt")
            {
                int n = int.Parse(method[1]), r = int.Parse(method[2]), p = int.Parse(method[3]);
                actual = Scrypt(pw, salt, n, r, p, expected.Length);
            }
            else return false;
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- scrypt (RFC 7914)
    public static byte[] Scrypt(byte[] password, byte[] salt, int N, int r, int p, int dkLen)
    {
        if (N < 2 || (N & (N - 1)) != 0) throw new ArgumentException("N 2'nin kuvveti olmalı");
        int blockLen = 128 * r;
        var B = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockLen);
        var X = new uint[32 * r];
        var Y = new uint[32 * r];
        var V = new uint[32 * r * N];
        for (int i = 0; i < p; i++)
        {
            int off = i * blockLen;
            for (int k = 0; k < 32 * r; k++) X[k] = BinaryPrimitives.ReadUInt32LittleEndian(B.AsSpan(off + k * 4));
            for (int j = 0; j < N; j++)
            {
                Array.Copy(X, 0, V, j * 32 * r, 32 * r);
                BlockMix(X, Y, r);
            }
            for (int j = 0; j < N; j++)
            {
                int idx = (int)(X[(2 * r - 1) * 16] & (uint)(N - 1));
                int vo = idx * 32 * r;
                for (int k = 0; k < 32 * r; k++) X[k] ^= V[vo + k];
                BlockMix(X, Y, r);
            }
            for (int k = 0; k < 32 * r; k++) BinaryPrimitives.WriteUInt32LittleEndian(B.AsSpan(off + k * 4), X[k]);
        }
        return Rfc2898DeriveBytes.Pbkdf2(password, B, 1, HashAlgorithmName.SHA256, dkLen);
    }

    private static void BlockMix(uint[] B, uint[] Y, int r)
    {
        var x = new uint[16];
        Array.Copy(B, (2 * r - 1) * 16, x, 0, 16);
        for (int i = 0; i < 2 * r; i++)
        {
            for (int k = 0; k < 16; k++) x[k] ^= B[i * 16 + k];
            Salsa208(x);
            // Çift bloklar ilk yarıya, tek bloklar ikinci yarıya
            int dst = (i % 2 == 0 ? i / 2 : r + i / 2) * 16;
            Array.Copy(x, 0, Y, dst, 16);
        }
        Array.Copy(Y, B, 32 * r);
    }

    private static uint R(uint a, int b) => (a << b) | (a >> (32 - b));

    private static void Salsa208(uint[] B)
    {
        uint x0 = B[0], x1 = B[1], x2 = B[2], x3 = B[3], x4 = B[4], x5 = B[5], x6 = B[6], x7 = B[7],
             x8 = B[8], x9 = B[9], x10 = B[10], x11 = B[11], x12 = B[12], x13 = B[13], x14 = B[14], x15 = B[15];
        for (int i = 0; i < 8; i += 2)
        {
            x4 ^= R(x0 + x12, 7); x8 ^= R(x4 + x0, 9); x12 ^= R(x8 + x4, 13); x0 ^= R(x12 + x8, 18);
            x9 ^= R(x5 + x1, 7); x13 ^= R(x9 + x5, 9); x1 ^= R(x13 + x9, 13); x5 ^= R(x1 + x13, 18);
            x14 ^= R(x10 + x6, 7); x2 ^= R(x14 + x10, 9); x6 ^= R(x2 + x14, 13); x10 ^= R(x6 + x2, 18);
            x3 ^= R(x15 + x11, 7); x7 ^= R(x3 + x15, 9); x11 ^= R(x7 + x3, 13); x15 ^= R(x11 + x7, 18);
            x1 ^= R(x0 + x3, 7); x2 ^= R(x1 + x0, 9); x3 ^= R(x2 + x1, 13); x0 ^= R(x3 + x2, 18);
            x6 ^= R(x5 + x4, 7); x7 ^= R(x6 + x5, 9); x4 ^= R(x7 + x6, 13); x5 ^= R(x4 + x7, 18);
            x11 ^= R(x10 + x9, 7); x8 ^= R(x11 + x10, 9); x9 ^= R(x8 + x11, 13); x10 ^= R(x9 + x8, 18);
            x12 ^= R(x15 + x14, 7); x13 ^= R(x12 + x15, 9); x14 ^= R(x13 + x12, 13); x15 ^= R(x14 + x13, 18);
        }
        B[0] += x0; B[1] += x1; B[2] += x2; B[3] += x3; B[4] += x4; B[5] += x5; B[6] += x6; B[7] += x7;
        B[8] += x8; B[9] += x9; B[10] += x10; B[11] += x11; B[12] += x12; B[13] += x13; B[14] += x14; B[15] += x15;
    }
}
