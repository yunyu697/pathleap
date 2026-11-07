using System.Numerics;
using System.Security.Cryptography;

namespace Pathleap.Shared.Update;

/// <summary>
/// Ed25519（RFC 8032）签名与验签的托管实现。
/// 不依赖第三方包：发布机与客户端共用同一份代码，签名口径只在一处定义。
/// 只做本产品需要的最小集合：seed → 公钥、签名、验签。
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    private static readonly BigInteger L =
        (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    private static readonly BigInteger D = Mod(-121665 * ModInverse(121666));

    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);

    private static readonly BigInteger BaseX =
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202");

    private static readonly BigInteger BaseY =
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960");

    public static byte[] GenerateSeed() => RandomNumberGenerator.GetBytes(32);

    /// <summary>由 32 字节 seed 派生公钥（32 字节）。</summary>
    public static byte[] GetPublicKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
        {
            throw new ArgumentException("seed 必须是 32 字节", nameof(seed));
        }

        var lpScalar = ClampScalar(Sha512(seed).AsSpan(0, 32));
        return Encode(ScalarMultiply(lpScalar, Point.FromAffine(BaseX, BaseY)));
    }

    /// <summary>对消息签名，返回 64 字节签名（R || S）。</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        if (seed.Length != 32)
        {
            throw new ArgumentException("seed 必须是 32 字节", nameof(seed));
        }

        var lpHash = Sha512(seed);
        var lpScalar = ClampScalar(lpHash.AsSpan(0, 32));
        var lpPublic = Encode(ScalarMultiply(lpScalar, Point.FromAffine(BaseX, BaseY)));

        var lpNonceHash = Sha512(lpHash.AsSpan(32, 32), message);
        var lpNonce = FromLittleEndian(lpNonceHash) % L;
        var lpR = Encode(ScalarMultiply(lpNonce, Point.FromAffine(BaseX, BaseY)));

        var lpChallengeHash = Sha512(lpR, lpPublic, message);
        var lpChallenge = FromLittleEndian(lpChallengeHash) % L;
        var lpS = (lpNonce + lpChallenge * lpScalar) % L;

        var lpSignature = new byte[64];
        Array.Copy(lpR, 0, lpSignature, 0, 32);
        ToLittleEndian(lpS, lpSignature.AsSpan(32, 32));
        return lpSignature;
    }

    /// <summary>验签；任何格式或数学上的异常都按验签失败处理，不抛给调用方。</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        try
        {
            var lpS = FromLittleEndian(signature.Slice(32, 32));
            if (lpS >= L)
            {
                return false;
            }

            var lpPublic = Decompress(publicKey);
            var lpR = Decompress(signature.Slice(0, 32));
            if (lpPublic is null || lpR is null)
            {
                return false;
            }

            var lpChallengeHash = Sha512(signature.Slice(0, 32), publicKey, message);
            var lpChallenge = FromLittleEndian(lpChallengeHash) % L;

            var lpSBase = ToAffine(ScalarMultiply(lpS, Point.FromAffine(BaseX, BaseY)));
            var lpChallengePublic = ScalarMultiply(lpChallenge, lpPublic.Value);
            var lpExpected = ToAffine(Add(lpR.Value, lpChallengePublic));

            return lpSBase.X == lpExpected.X && lpSBase.Y == lpExpected.Y;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>扩展坐标点（X, Y, Z, T），对应 RFC 8032 中 a = -1 的 Edwards 曲线。</summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T)
    {
        public static Point FromAffine(BigInteger x, BigInteger y) => new(x, y, 1, Mod(x * y));
    }

    private static BigInteger ClampScalar(ReadOnlySpan<byte> raw)
    {
        var lpBytes = raw.ToArray();
        lpBytes[0] &= 248;
        lpBytes[31] &= 127;
        lpBytes[31] |= 64;
        return FromLittleEndian(lpBytes);
    }

    private static BigInteger Mod(BigInteger value)
    {
        var lpResult = value % P;
        return lpResult.Sign < 0 ? lpResult + P : lpResult;
    }

    private static BigInteger ModInverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static Point Add(Point left, Point right)
    {
        var lpA = Mod((left.Y - left.X) * (right.Y - right.X));
        var lpB = Mod((left.Y + left.X) * (right.Y + right.X));
        var lpC = Mod(2 * left.T * right.T * D);
        var lpD = Mod(2 * left.Z * right.Z);
        var lpE = Mod(lpB - lpA);
        var lpF = Mod(lpD - lpC);
        var lpG = Mod(lpD + lpC);
        var lpH = Mod(lpB + lpA);
        return new Point(
            Mod(lpE * lpF),
            Mod(lpG * lpH),
            Mod(lpF * lpG),
            Mod(lpE * lpH));
    }

    private static Point Double(Point point)
    {
        var lpA = Mod(point.X * point.X);
        var lpB = Mod(point.Y * point.Y);
        var lpC = Mod(2 * point.Z * point.Z);
        var lpD = Mod(-lpA);
        var lpE = Mod(Mod((point.X + point.Y) * (point.X + point.Y)) - lpA - lpB);
        var lpG = Mod(lpD + lpB);
        var lpF = Mod(lpG - lpC);
        var lpH = Mod(lpD - lpB);
        return new Point(
            Mod(lpE * lpF),
            Mod(lpG * lpH),
            Mod(lpF * lpG),
            Mod(lpE * lpH));
    }

    private static Point ScalarMultiply(BigInteger scalar, Point point)
    {
        var lpResult = new Point(0, 1, 1, 0);
        var lpAddend = point;
        var lpRemaining = scalar;
        while (lpRemaining > 0)
        {
            if ((lpRemaining & BigInteger.One) == BigInteger.One)
            {
                lpResult = Add(lpResult, lpAddend);
            }

            lpAddend = Double(lpAddend);
            lpRemaining >>= 1;
        }

        return lpResult;
    }

    private static (BigInteger X, BigInteger Y) ToAffine(Point point)
    {
        var lpInverse = ModInverse(point.Z);
        return (Mod(point.X * lpInverse), Mod(point.Y * lpInverse));
    }

    private static Point? Decompress(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 32)
        {
            return null;
        }

        var lpBytes = encoded.ToArray();
        var lpSign = (lpBytes[31] & 0x80) >> 7;
        lpBytes[31] &= 0x7F;
        var lpY = FromLittleEndian(lpBytes);
        if (lpY >= P)
        {
            return null;
        }

        var lpY2 = Mod(lpY * lpY);
        var lpU = Mod(lpY2 - 1);
        var lpV = Mod(D * lpY2 + 1);
        var lpX2 = Mod(lpU * ModInverse(lpV));
        if (lpX2 == 0)
        {
            return lpSign == 1 ? null : Point.FromAffine(0, lpY);
        }

        var lpX = BigInteger.ModPow(lpX2, (P + 3) / 8, P);
        if (Mod(lpX * lpX) != lpX2)
        {
            lpX = Mod(lpX * SqrtM1);
        }

        if (Mod(lpX * lpX) != lpX2)
        {
            return null;
        }

        if (lpX.IsEven != (lpSign == 0))
        {
            lpX = P - lpX;
        }

        return Point.FromAffine(lpX, lpY);
    }

    private static byte[] Encode(Point point)
    {
        var (lpX, lpY) = ToAffine(point);
        var lpBytes = new byte[32];
        ToLittleEndian(lpY, lpBytes);
        if (!lpX.IsEven)
        {
            lpBytes[31] |= 0x80;
        }

        return lpBytes;
    }

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes)
    {
        var lpBuffer = new byte[bytes.Length + 1];
        bytes.CopyTo(lpBuffer);
        return new BigInteger(lpBuffer);
    }

    private static void ToLittleEndian(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        var lpOffset = 0;
        while (value > 0 && lpOffset < destination.Length)
        {
            destination[lpOffset++] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    private static byte[] Sha512(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default, ReadOnlySpan<byte> third = default)
    {
        using var lpInput = new MemoryStream();
        lpInput.Write(first);
        if (!second.IsEmpty)
        {
            lpInput.Write(second);
        }

        if (!third.IsEmpty)
        {
            lpInput.Write(third);
        }

        lpInput.Position = 0;
        return SHA512.HashData(lpInput);
    }
}
