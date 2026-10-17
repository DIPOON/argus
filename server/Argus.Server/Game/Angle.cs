namespace Argus.Server.Game;

// 방향(각도)이다. 한 바퀴를 65536칸으로 나눈 정수 Units로 나타낸다(이진 각도). 한 칸은 약 0.0055°다.
// 0은 오른쪽(+X), 16384는 아래쪽(+Y, 화면 좌표)이다. 한 바퀴를 넘거나 음수가 된 값은 0~65535로 감아 두므로
// 각도를 더하고 빼는 계산이 정확하다. 사인·코사인·아크탄젠트를 운영체제의 수학 라이브러리 없이 정수 연산으로만 구한다.
// 중간값이 2^62까지 커지므로, JavaScript로 그대로 옮기려면 BigInt가 필요하다. 다만 클라이언트 예측(결정론 4단계)은
// 자기 이동만 다시 계산하고 각도는 서버가 보낸 값을 그리기만 하므로, 이 계산은 서버에만 있으면 된다.
public readonly struct Angle : IEquatable<Angle>
{
    public const int UnitsPerTurn = 65536;
    public const int QuarterTurn = 16384;
    public const int HalfTurn = 32768;
    private const int Mask = UnitsPerTurn - 1;

    // 중간 계산은 2^30을 1로 보는 정수로 한다. 결과(1/4096 단위)보다 훨씬 촘촘해서 반올림 오차가 결과에 거의 남지 않는다.
    private const long One = 1L << 30;
    // π/2 × 2^30
    private const long HalfPi = 1686629713;
    // tan(π/8) × 2^30. 아크탄젠트는 이 값보다 큰 비율을 45° 기준으로 옮겨 계산한다.
    private const long TanEighthPi = 444758426;
    // 라디안(2^30 단위)에 이 값을 곱하고 2^46으로 나누면 칸 수다. 65536 / 2π × 2^16이다.
    private const long RadiansToUnits = 683565276;
    private const long RadiansToUnitsShift = 1L << 46;

    // 사인의 테일러 급수 계수(1/3!, 1/5!, … 1/11!)를 2^30 단위로 적었다. 부호는 계산에서 번갈아 붙인다.
    // 11차에서 멈춘 오차는 90°에서도 6 × 10^-8 미만이다.
    private static readonly long[] SineTerms = new long[] { 178956971, 8947849, 213044, 2959, 27 };
    // 아크탄젠트의 급수 계수(1/3, 1/5, … 1/15)다. |u| ≤ tan(π/8)에서 15차에서 멈춘 오차는 2 × 10^-8 라디안 미만이다.
    private static readonly long[] ArctangentTerms = new long[] { 357913941, 214748365, 153391689, 119304647, 97612893, 82595525, 71582788 };

    // 0 이상 65536 미만이다.
    public int Units { get; }

    private Angle(int units)
    {
        Units = units & Mask;
    }

    public static Angle Zero { get; } = new Angle(0);

    public static Angle FromUnits(long units)
    {
        return new Angle((int)(units & Mask));
    }

    // 클라이언트가 보낸 조준 방향처럼 라디안 실수로 들어온 값을 바꾼다. 게임 진행 중의 계산에는 쓰지 않는다.
    // 먼저 바퀴 수로 바꿔 한 바퀴 안의 몫만 남긴다. 실수의 사칙연산·내림·반올림만 써서 어디서나 같다.
    public static Angle FromRadians(double radians)
    {
        if (!double.IsFinite(radians))
        {
            throw new ArgumentOutOfRangeException(nameof(radians), radians, "각도로 바꿀 수 없는 값입니다");
        }
        double turns = radians / Math.Tau;
        double fraction = turns - Math.Floor(turns);
        double units = Math.Round(fraction * UnitsPerTurn, MidpointRounding.AwayFromZero);
        return FromUnits((long)units);
    }

    // -32768~32767칸(-180° 이상 180° 미만)으로 나타낸 값이다.
    public int SignedUnits()
    {
        if (Units >= HalfTurn)
        {
            return Units - UnitsPerTurn;
        }
        return Units;
    }

    // 화면과 통신 형식으로 보낼 때만 쓴다. -π 이상 π 미만이다.
    public double ToRadians()
    {
        return SignedUnits() * Math.Tau / UnitsPerTurn;
    }

    public static Angle operator +(Angle a, Angle b)
    {
        return new Angle(a.Units + b.Units);
    }

    public static Angle operator -(Angle a, Angle b)
    {
        return new Angle(a.Units - b.Units);
    }

    public static Angle operator -(Angle value)
    {
        return new Angle(-value.Units);
    }

    public static bool operator ==(Angle a, Angle b)
    {
        return a.Units == b.Units;
    }

    public static bool operator !=(Angle a, Angle b)
    {
        return a.Units != b.Units;
    }

    // 두 방향 사이의 각도 크기다. 0~32768칸(0°~180°)이다.
    public static int Between(Angle a, Angle b)
    {
        Angle difference = a - b;
        return Math.Abs(difference.SignedUnits());
    }

    public Fixed Sin()
    {
        // 사분면의 대칭으로 0°~90°만 계산한다. 2사분면과 4사분면은 90°에서 거꾸로 센다.
        int quadrant = Units / QuarterTurn;
        int offset = Units % QuarterTurn;
        if (quadrant == 1 || quadrant == 3)
        {
            offset = QuarterTurn - offset;
        }
        long value = QuarterSine(offset);
        if (quadrant >= 2)
        {
            value = -value;
        }
        // 2^30 단위를 고정소수점(2^12 단위)으로 반올림한다.
        return Fixed.FromRaw(Fixed.DivideRounded(value, One / Fixed.Scale));
    }

    public Fixed Cos()
    {
        Angle shifted = new Angle(Units + QuarterTurn);
        return shifted.Sin();
    }

    // 이 방향으로 길이가 1인 벡터다. 길이는 반올림 때문에 1과 1/4096쯤 다를 수 있다.
    public FixedVector Direction()
    {
        return new FixedVector(Cos(), Sin());
    }

    // 원점에서 (x, y)를 향하는 방향이다. 둘 다 0이면 0이다.
    public static Angle Atan2(Fixed y, Fixed x)
    {
        long ax = Math.Abs(x.Raw);
        long ay = Math.Abs(y.Raw);
        if (ax == 0 && ay == 0)
        {
            return Zero;
        }
        // 0°~45°로 옮긴다. 큰 쪽으로 작은 쪽을 나눈 비율 t(0~1)의 아크탄젠트를 구한 뒤 대칭으로 되돌린다.
        bool steep = ay > ax;
        long small = ay;
        long big = ax;
        if (steep)
        {
            small = ax;
            big = ay;
        }
        // 비율만 쓰므로 아주 큰 값은 같이 줄여 2^30을 곱해도 넘치지 않게 한다.
        while (big >= 1L << 32)
        {
            small >>= 1;
            big >>= 1;
        }
        long ratio = Fixed.DivideRounded(small * One, big);
        long units = OctantArctangent(ratio);
        if (steep)
        {
            units = QuarterTurn - units;
        }
        if (x.Raw < 0)
        {
            units = HalfTurn - units;
        }
        if (y.Raw < 0)
        {
            units = -units;
        }
        return FromUnits(units);
    }

    public static Angle Of(FixedVector vector)
    {
        return Atan2(vector.Y, vector.X);
    }

    // offset칸(0~16384, 0°~90°)의 사인을 2^30 단위로 구한다.
    private static long QuarterSine(int offset)
    {
        // 칸 수를 라디안(2^30 단위)으로 바꾼다. 16384칸이 π/2다.
        long x = Fixed.DivideRounded(offset * HalfPi, QuarterTurn);
        long square = Multiply(x, x);
        // sin x = x × (1 - x²/3! + x⁴/5! - x⁶/7! + x⁸/9! - x¹⁰/11!)를 안쪽 괄호부터 계산한다.
        long sum = 0;
        for (int i = SineTerms.Length - 1; i >= 0; i--)
        {
            long term = SineTerms[i];
            if (i % 2 == 0)
            {
                term = -term;
            }
            sum = term + Multiply(sum, square);
        }
        sum = One + Multiply(sum, square);
        return Multiply(sum, x);
    }

    // 비율 t(2^30 단위, 0~1)의 아크탄젠트를 칸 수(0~8192)로 구한다.
    private static long OctantArctangent(long ratio)
    {
        // 급수는 0 근처에서 빨리 맞으므로, tan(π/8)보다 큰 비율은 atan(t) = π/4 + atan((t - 1) / (t + 1))로 옮긴다.
        // π/4는 정확히 8192칸이다.
        long baseUnits = 0;
        long u = ratio;
        if (ratio > TanEighthPi)
        {
            baseUnits = HalfTurn / 4;
            u = Fixed.DivideRounded((ratio - One) * One, ratio + One);
        }
        long square = Multiply(u, u);
        // atan u = u × (1 - u²/3 + u⁴/5 - … - u¹⁴/15)를 안쪽 괄호부터 계산한다.
        long sum = 0;
        for (int i = ArctangentTerms.Length - 1; i >= 0; i--)
        {
            long term = ArctangentTerms[i];
            if (i % 2 == 0)
            {
                term = -term;
            }
            sum = term + Multiply(sum, square);
        }
        sum = One + Multiply(sum, square);
        long radians = Multiply(sum, u);
        return baseUnits + Fixed.DivideRounded(radians * RadiansToUnits, RadiansToUnitsShift);
    }

    // 2^30 단위 수 둘을 곱한다.
    private static long Multiply(long a, long b)
    {
        return Fixed.DivideRounded(checked(a * b), One);
    }

    public bool Equals(Angle other)
    {
        return Units == other.Units;
    }

    public override bool Equals(object? obj)
    {
        if (obj is Angle other)
        {
            return Units == other.Units;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return Units;
    }

    public override string ToString()
    {
        return $"{Units}칸";
    }
}
