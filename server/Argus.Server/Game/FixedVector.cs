using System.Globalization;

namespace Argus.Server.Game;

// 고정소수점 좌표 두 개로 된 벡터다. 위치, 이동량, 방향에 쓴다.
public readonly struct FixedVector : IEquatable<FixedVector>
{
    public Fixed X { get; }
    public Fixed Y { get; }

    public FixedVector(Fixed x, Fixed y)
    {
        X = x;
        Y = y;
    }

    public static FixedVector Zero { get; } = new FixedVector(Fixed.Zero, Fixed.Zero);

    public static FixedVector FromInt(int x, int y)
    {
        return new FixedVector(Fixed.FromInt(x), Fixed.FromInt(y));
    }

    // 클라이언트가 보낸 이동 방향처럼 실수로 들어온 값을 바꾼다. 게임 진행 중의 계산에는 쓰지 않는다.
    public static FixedVector FromDouble(double x, double y)
    {
        return new FixedVector(Fixed.FromDouble(x), Fixed.FromDouble(y));
    }

    public static FixedVector operator +(FixedVector a, FixedVector b)
    {
        return new FixedVector(a.X + b.X, a.Y + b.Y);
    }

    public static FixedVector operator -(FixedVector a, FixedVector b)
    {
        return new FixedVector(a.X - b.X, a.Y - b.Y);
    }

    public static FixedVector operator -(FixedVector value)
    {
        return new FixedVector(-value.X, -value.Y);
    }

    public static FixedVector operator *(FixedVector vector, Fixed scale)
    {
        return new FixedVector(vector.X * scale, vector.Y * scale);
    }

    public static FixedVector operator *(FixedVector vector, int scale)
    {
        return new FixedVector(vector.X * scale, vector.Y * scale);
    }

    public static FixedVector operator /(FixedVector vector, Fixed divisor)
    {
        return new FixedVector(vector.X / divisor, vector.Y / divisor);
    }

    public static FixedVector operator /(FixedVector vector, int divisor)
    {
        return new FixedVector(vector.X / divisor, vector.Y / divisor);
    }

    public static bool operator ==(FixedVector a, FixedVector b)
    {
        return a.X == b.X && a.Y == b.Y;
    }

    public static bool operator !=(FixedVector a, FixedVector b)
    {
        return a.X != b.X || a.Y != b.Y;
    }

    public bool IsZero()
    {
        return X.Raw == 0 && Y.Raw == 0;
    }

    public Fixed Dot(FixedVector other)
    {
        return X * other.X + Y * other.Y;
    }

    // 두 벡터가 이루는 평행사변형의 넓이(부호 있음)다. 다른 벡터가 왼쪽(반시계 방향)에 있으면 양수다.
    public Fixed Cross(FixedVector other)
    {
        return X * other.Y - Y * other.X;
    }

    // 길이의 제곱이다. 거리 비교에 쓴다.
    public Fixed LengthSquared()
    {
        return X * X + Y * Y;
    }

    // 길이다. 성분의 제곱을 반올림하지 않은 채(Raw의 제곱, 2^24 단위) 더하고 제곱근을 구해, 아주 짧은 벡터도 길이를 잃지 않는다.
    public Fixed Length()
    {
        // 제곱의 합도 JavaScript의 범위(2^53) 안이어야 브라우저가 같은 길이를 구한다.
        long squared = Fixed.Safe(checked(X.Raw * X.Raw + Y.Raw * Y.Raw));
        return Fixed.FromRaw(Fixed.RoundedSqrt(squared));
    }

    // 길이가 1인 같은 방향의 벡터다. 길이가 0이면 0 벡터를 돌려준다.
    public FixedVector Normalize()
    {
        Fixed length = Length();
        if (length.Raw == 0)
        {
            return Zero;
        }
        return new FixedVector(X / length, Y / length);
    }

    public static Fixed DistanceSquared(FixedVector a, FixedVector b)
    {
        return (a - b).LengthSquared();
    }

    public static Fixed Distance(FixedVector a, FixedVector b)
    {
        return (a - b).Length();
    }

    // a에서 b로 t만큼(0이면 a, 1이면 b) 간 지점이다.
    public static FixedVector Lerp(FixedVector a, FixedVector b, Fixed t)
    {
        return a + (b - a) * t;
    }

    public bool Equals(FixedVector other)
    {
        return X == other.X && Y == other.Y;
    }

    public override bool Equals(object? obj)
    {
        if (obj is FixedVector other)
        {
            return Equals(other);
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X.Raw, Y.Raw);
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"({X}, {Y})");
    }
}
