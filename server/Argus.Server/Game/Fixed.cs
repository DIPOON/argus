using System.Globalization;
using System.Numerics;

namespace Argus.Server.Game;

// 고정소수점 수다. 정수 Raw를 4096(2의 12제곱)으로 나눈 값을 뜻한다. Raw가 6144이면 1.5다.
// 게임 진행 계산을 정수로만 해서, 운영체제와 CPU가 달라도 같은 결과가 나오게 한다.
// 실수의 삼각함수는 운영체제의 수학 라이브러리가 계산해 마지막 자리가 다를 수 있고, 정수 연산은 어디서나 같다.
//
// 반올림 규칙: 곱셈·나눗셈·제곱근은 가장 가까운 값으로 반올림하고, 딱 절반이면 0에서 먼 쪽으로 간다.
// 그래서 부호만 바꾼 값의 결과는 부호만 바뀐다(-a × b = -(a × b)). 왼쪽과 오른쪽 움직임이 똑같이 계산된다.
// 범위를 넘으면 값이 조용히 감기지 않고 예외(OverflowException)를 낸다.
//
// 브라우저(JavaScript)도 같은 계산을 할 수 있게, 값과 중간값(곱, 나눗셈 전의 수, 제곱의 합)을 모두 ±(2^53 - 1) 안으로 제한한다.
// JavaScript의 수는 이 범위의 정수만 정확히 다루고, 넘으면 아래 자리를 소리 없이 잃는다. 그래서 서버에서 이 범위를 넘으면 예외를 낸다.
// 맵 좌표(2000 미만)는 Raw로 2^23 미만이라, 좌표끼리 곱해도 2^46이고 제곱을 더해도 2^47이다.
public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>
{
    public const int FractionBits = 12;
    public const long Scale = 4096;
    // JavaScript가 정확히 다루는 정수의 한계(2^53 - 1)다.
    public const long SafeLimit = 9_007_199_254_740_991;
    // 실수에서 바꿀 수 있는 가장 큰 크기다. Raw가 2^53 미만이라 실수로 정확히 나타낼 수 있다.
    private const double MaxConvertible = 1_000_000_000;

    public long Raw { get; }

    // 모든 값이 여기를 지나므로, 결과가 JavaScript의 범위를 넘으면 여기서 예외가 난다.
    private Fixed(long raw)
    {
        Raw = Safe(raw);
    }

    public static Fixed Zero { get; } = new Fixed(0);
    public static Fixed One { get; } = new Fixed(Scale);

    public static Fixed FromRaw(long raw)
    {
        return new Fixed(raw);
    }

    public static Fixed FromInt(int value)
    {
        return new Fixed(value * Scale);
    }

    // 설정 값이나 클라이언트가 보낸 값처럼 실수로 들어온 값을 바꾼다. 게임 진행 중의 계산에는 쓰지 않는다.
    // 4096을 곱하는 것은 2의 거듭제곱이라 정확하고, 반올림도 실수 규칙(IEEE 754)으로 정해져 있어 어디서나 같다.
    public static Fixed FromDouble(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxConvertible)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "고정소수점으로 바꿀 수 없는 값입니다");
        }
        double scaled = Math.Round(value * Scale, MidpointRounding.AwayFromZero);
        return new Fixed((long)scaled);
    }

    // 화면과 통신 형식으로 보낼 때만 쓴다. 4096으로 나누는 것도 정확하다.
    public double ToDouble()
    {
        return (double)Raw / Scale;
    }

    public static Fixed operator +(Fixed a, Fixed b)
    {
        return new Fixed(checked(a.Raw + b.Raw));
    }

    public static Fixed operator -(Fixed a, Fixed b)
    {
        return new Fixed(checked(a.Raw - b.Raw));
    }

    public static Fixed operator -(Fixed value)
    {
        return new Fixed(checked(-value.Raw));
    }

    public static Fixed operator *(Fixed a, Fixed b)
    {
        long product = Safe(checked(a.Raw * b.Raw));
        return new Fixed(ShiftRounded(product, FractionBits));
    }

    // 정수를 곱할 때는 반올림할 것이 없다.
    public static Fixed operator *(Fixed a, int b)
    {
        return new Fixed(checked(a.Raw * b));
    }

    public static Fixed operator *(int a, Fixed b)
    {
        return new Fixed(checked(a * b.Raw));
    }

    public static Fixed operator /(Fixed a, Fixed b)
    {
        long numerator = Safe(checked(a.Raw * Scale));
        return new Fixed(SafeDivide(numerator, b.Raw));
    }

    public static Fixed operator /(Fixed a, int b)
    {
        return new Fixed(SafeDivide(a.Raw, b));
    }

    public static bool operator ==(Fixed a, Fixed b)
    {
        return a.Raw == b.Raw;
    }

    public static bool operator !=(Fixed a, Fixed b)
    {
        return a.Raw != b.Raw;
    }

    public static bool operator <(Fixed a, Fixed b)
    {
        return a.Raw < b.Raw;
    }

    public static bool operator >(Fixed a, Fixed b)
    {
        return a.Raw > b.Raw;
    }

    public static bool operator <=(Fixed a, Fixed b)
    {
        return a.Raw <= b.Raw;
    }

    public static bool operator >=(Fixed a, Fixed b)
    {
        return a.Raw >= b.Raw;
    }

    public static Fixed Abs(Fixed value)
    {
        if (value.Raw < 0)
        {
            return -value;
        }
        return value;
    }

    public static Fixed Min(Fixed a, Fixed b)
    {
        if (b.Raw < a.Raw)
        {
            return b;
        }
        return a;
    }

    public static Fixed Max(Fixed a, Fixed b)
    {
        if (b.Raw > a.Raw)
        {
            return b;
        }
        return a;
    }

    public static Fixed Clamp(Fixed value, Fixed min, Fixed max)
    {
        if (value.Raw < min.Raw)
        {
            return min;
        }
        if (value.Raw > max.Raw)
        {
            return max;
        }
        return value;
    }

    public int Sign()
    {
        return Math.Sign(Raw);
    }

    // 작은 쪽 정수로 내린다. -0.5는 -1이다. 좌표로 타일 번호를 구할 때 쓴다.
    public int FloorToInt()
    {
        // 오른쪽 시프트는 음수에서도 작은 쪽으로 내린다.
        return checked((int)(Raw >> FractionBits));
    }

    // 큰 쪽 정수로 올린다. -0.5는 0이다.
    public int CeilingToInt()
    {
        // 부호를 바꿔 내린 뒤 다시 바꾼다. 가장 작은 long은 부호를 바꿀 수 없으므로 예외를 낸다.
        long floored = checked(-(-Raw >> FractionBits));
        return checked((int)floored);
    }

    // 가장 가까운 정수로 반올림한다. 딱 절반이면 0에서 먼 쪽이다(0.5는 1, 2.5는 3, -0.5는 -1).
    // 실수의 Math.Round는 딱 절반이면 짝수 쪽(2.5는 2)으로 가므로, 다른 연산과 같은 규칙이 되도록 Raw에서 바로 반올림한다.
    public int RoundToInt()
    {
        long rounded = ShiftRounded(Raw, FractionBits);
        return checked((int)rounded);
    }

    // 정수 divisor로 나눈 몫을 작은 쪽 정수로 내린다. 좌표를 타일 크기로 나눠 타일 번호를 구할 때 쓴다.
    // 나눗셈 연산자(/)는 가장 가까운 값으로 반올림하므로, 그 뒤에 내리면 타일 경계 바로 앞의 값이 다음 타일로 넘어갈 수 있다.
    // 그래서 Raw를 (divisor × 4096)으로 직접 나눠 정확히 내린다. 음수는 작은 쪽(-0.5는 -1)으로 내린다.
    public int FloorDivide(int divisor)
    {
        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "양의 정수로만 나눌 수 있습니다");
        }
        long whole = divisor * Scale;
        long quotient = Raw / whole;
        // C#의 정수 나눗셈은 0 쪽으로 버리므로, 음수에서 나머지가 있으면 하나 더 내린다.
        if (Raw < 0 && Raw % whole != 0)
        {
            quotient--;
        }
        return checked((int)quotient);
    }

    // 정수 divisor로 나눈 몫을 큰 쪽 정수로 올린다. 이동을 몇 걸음으로 나눌지 구할 때 쓴다.
    // FloorDivide와 같은 이유로, 반올림하는 나눗셈 연산자(/) 뒤에 올리지 않고 Raw를 (divisor × 4096)으로 직접 나눠 올린다.
    // 음수는 큰 쪽(-0.5는 0)으로 올린다.
    public int CeilingDivide(int divisor)
    {
        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "양의 정수로만 나눌 수 있습니다");
        }
        long whole = divisor * Scale;
        long quotient = Raw / whole;
        // C#의 정수 나눗셈은 0 쪽으로 버리므로, 양수에서 나머지가 있으면 하나 더 올린다.
        if (Raw > 0 && Raw % whole != 0)
        {
            quotient++;
        }
        return checked((int)quotient);
    }

    // 제곱근을 가장 가까운 고정소수점 값으로 구한다. 실수 제곱근을 거치지 않고 정수 연산만 쓴다.
    public static Fixed Sqrt(Fixed value)
    {
        if (value.Raw < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.ToDouble(), "음수의 제곱근은 구할 수 없습니다");
        }
        // sqrt(Raw / 4096) × 4096 = sqrt(Raw × 4096)이다.
        long scaled = Safe(checked(value.Raw * Scale));
        return new Fixed(RoundedSqrt(scaled));
    }

    // 값이 JavaScript가 정확히 다루는 범위(±(2^53 - 1)) 안인지 확인하고 그대로 돌려준다.
    // 넘으면 서버는 계산할 수 있어도 브라우저에서는 결과가 달라지므로 예외를 낸다.
    public static long Safe(long value)
    {
        if (value > SafeLimit || value < -SafeLimit)
        {
            throw new OverflowException($"고정소수점 값 {value}가 2^53 - 1을 넘습니다");
        }
        return value;
    }

    // 반올림 나눗셈의 중간값(나뉘는 수의 크기 + 나누는 수의 절반)도 JavaScript의 범위 안인지 본 뒤 나눈다.
    private static long SafeDivide(long numerator, long denominator)
    {
        Safe(checked(Math.Abs(numerator) + Math.Abs(denominator) / 2));
        return DivideRounded(numerator, denominator);
    }

    // 2^bits로 나누며 0에서 먼 쪽으로 반올림한다. 나누는 수가 2의 거듭제곱이면 DivideRounded와 같은 값이고,
    // 나눗셈 대신 시프트를 써서 빠르다. 오른쪽 시프트는 0 이상의 수를 내림한다.
    public static long ShiftRounded(long value, int bits)
    {
        long half = 1L << (bits - 1);
        if (value >= 0)
        {
            return checked(value + half) >> bits;
        }
        long magnitude = checked(-value + half) >> bits;
        return -magnitude;
    }

    // 0에서 먼 쪽으로 반올림하는 정수 나눗셈이다. 크기만으로 나눈 뒤 부호를 붙인다.
    // 크기에 나누는 수의 절반을 더하고 내리면 가장 가까운 정수가 되고, 딱 절반이면 크기가 큰 쪽이 된다.
    public static long DivideRounded(long numerator, long denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException("고정소수점을 0으로 나눌 수 없습니다");
        }
        bool negative = (numerator < 0) != (denominator < 0);
        long magnitude = Math.Abs(numerator);
        long divisor = Math.Abs(denominator);
        long quotient = checked(magnitude + divisor / 2) / divisor;
        if (negative)
        {
            return -quotient;
        }
        return quotient;
    }

    // 가장 가까운 정수 제곱근이다. 내림 제곱근 r에 대해 value가 (r + 0.5)²보다 크면 하나 올린다.
    // (r + 0.5)² = r² + r + 0.25이고 value는 정수이므로, value - r² > r 이면 올린다. 정확히 절반인 경우는 생기지 않는다.
    public static long RoundedSqrt(long value)
    {
        long root = FloorSqrt(value);
        if (value - root * root > root)
        {
            root++;
        }
        return root;
    }

    // 내림 정수 제곱근이다. 뉴턴 방법으로, 제곱근보다 큰 2의 거듭제곱에서 시작해 줄어들다 멈춘 값이 답이다.
    public static long FloorSqrt(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "음수의 제곱근은 구할 수 없습니다");
        }
        if (value < 2)
        {
            return value;
        }
        // value의 비트 수가 n이면 제곱근은 2^(n/2 + 1)보다 작다.
        int bits = BitOperations.Log2((ulong)value);
        long current = 1L << (bits / 2 + 1);
        while (true)
        {
            long next = (current + value / current) / 2;
            if (next >= current)
            {
                return current;
            }
            current = next;
        }
    }

    public bool Equals(Fixed other)
    {
        return Raw == other.Raw;
    }

    public override bool Equals(object? obj)
    {
        if (obj is Fixed other)
        {
            return Raw == other.Raw;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return Raw.GetHashCode();
    }

    public int CompareTo(Fixed other)
    {
        return Raw.CompareTo(other.Raw);
    }

    // 검사 실패와 오류 메시지에서 읽기 쉽게 실수로 보여 준다.
    public override string ToString()
    {
        return ToDouble().ToString(CultureInfo.InvariantCulture);
    }
}
