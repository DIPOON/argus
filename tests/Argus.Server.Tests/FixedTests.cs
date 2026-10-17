using Argus.Server.Game;
using static TestTools;

internal static class FixedTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("Fixed-point arithmetic rounds to the nearest value, halves away from zero, and keeps sign symmetry", Arithmetic));
        tests.Add(new TestCase("Fixed-point values refuse to wrap around and convert from doubles only within range", Limits));
        tests.Add(new TestCase("Integer square roots are exact and fixed-point square roots round to the nearest value", SquareRoots));
        tests.Add(new TestCase("Vector length keeps short vectors and normalizing gives unit length with mirrored signs", VectorLength));
        tests.Add(new TestCase("Sine and cosine are within half a step of the true value at all 65536 angles", SineAccuracy));
        tests.Add(new TestCase("Arctangent finds directions within half a step for vectors of every size, exactly on axes and diagonals", ArctangentAccuracy));
        tests.Add(new TestCase("Angles from radians wrap into one turn for negative, large and boundary values", AngleConversion));
        tests.Add(new TestCase("Sine and arctangent tables match fixed checksums so every machine computes the same values", MathChecksums));
    }

    private static void Arithmetic()
    {
        Equal(Fixed.FromInt(3).Raw, 12288L);
        Equal(Fixed.FromDouble(1.5).Raw, 6144L);
        Equal(Fixed.FromRaw(6144).ToDouble(), 1.5);
        // 실수에서 바꿀 때도 딱 절반은 0에서 먼 쪽이다. 1/8192는 Raw 0.5다.
        Equal(Fixed.FromDouble(1.0 / 8192).Raw, 1L);
        Equal(Fixed.FromDouble(-1.0 / 8192).Raw, -1L);

        // 곱셈: 0.25칸은 버리고, 0.75칸은 올리고, 딱 0.5칸은 0에서 먼 쪽으로 간다.
        Equal((Fixed.FromRaw(1) * Fixed.FromRaw(1024)).Raw, 0L);
        Equal((Fixed.FromRaw(3) * Fixed.FromRaw(1024)).Raw, 1L);
        Equal((Fixed.FromRaw(1) * Fixed.FromRaw(2048)).Raw, 1L);
        Equal((Fixed.FromRaw(-1) * Fixed.FromRaw(2048)).Raw, -1L);
        Equal(Fixed.FromDouble(1.5) * Fixed.FromDouble(-2.5), Fixed.FromDouble(-3.75));

        // 나눗셈: 1/3은 1365.33칸이라 1365, 2/3은 2730.67칸이라 2731이다.
        Equal((Fixed.One / Fixed.FromInt(3)).Raw, 1365L);
        Equal((Fixed.FromInt(2) / Fixed.FromInt(3)).Raw, 2731L);
        Equal((Fixed.FromRaw(1) / 2).Raw, 1L);
        Equal((Fixed.FromRaw(-1) / 2).Raw, -1L);
        // 초당 속도를 틱당 이동량으로 바꿀 때는 정수로 나눠 1/30초를 고정소수점으로 반올림한 오차를 피한다.
        Equal(Fixed.FromInt(180) / Rules.TicksPerSecond, Fixed.FromInt(6));

        // 부호만 바꾼 값의 결과는 부호만 바뀐다. 왼쪽과 오른쪽 움직임이 똑같이 계산된다는 뜻이다.
        long[] values = new long[] { 1, 3, 2047, 2048, 2049, 4095, 4096, 6143, 12345, 999999 };
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = 0; j < values.Length; j++)
            {
                Fixed a = Fixed.FromRaw(values[i]);
                Fixed b = Fixed.FromRaw(values[j]);
                Equal((-a) * b, -(a * b));
                Equal(a * (-b), -(a * b));
                Equal((-a) / b, -(a / b));
                Equal(a / (-b), -(a / b));
            }
        }

        // 내림은 작은 쪽, 올림은 큰 쪽이다. 타일 번호를 구할 때 음수 좌표도 바르게 내린다.
        Equal(Fixed.FromDouble(2.25).FloorToInt(), 2);
        Equal(Fixed.FromDouble(2.25).CeilingToInt(), 3);
        Equal(Fixed.FromDouble(-0.5).FloorToInt(), -1);
        Equal(Fixed.FromDouble(-0.5).CeilingToInt(), 0);
        Equal(Fixed.FromInt(-2).FloorToInt(), -2);
        Equal(Fixed.FromInt(-2).CeilingToInt(), -2);

        Equal(Fixed.Abs(Fixed.FromInt(-3)), Fixed.FromInt(3));
        Equal(Fixed.Min(Fixed.One, Fixed.Zero), Fixed.Zero);
        Equal(Fixed.Max(Fixed.One, Fixed.Zero), Fixed.One);
        Equal(Fixed.Clamp(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(2)), Fixed.FromInt(2));
        Equal(Fixed.Clamp(Fixed.FromInt(-5), Fixed.Zero, Fixed.FromInt(2)), Fixed.Zero);
    }

    private static void Limits()
    {
        // 넘치면 값이 감겨 엉뚱한 결과로 진행되지 않고 예외가 난다. 방은 이 예외를 결함으로 기록한다.
        Throws<OverflowException>(() => Fixed.FromRaw(long.MaxValue) + Fixed.FromRaw(1));
        Throws<OverflowException>(() => Fixed.FromRaw(1L << 40) * Fixed.FromRaw(1L << 40));
        Throws<DivideByZeroException>(() => Fixed.One / Fixed.Zero);
        Throws<DivideByZeroException>(() => Fixed.One / 0);
        Throws<ArgumentOutOfRangeException>(() => Fixed.FromDouble(double.NaN));
        Throws<ArgumentOutOfRangeException>(() => Fixed.FromDouble(double.PositiveInfinity));
        Throws<ArgumentOutOfRangeException>(() => Fixed.FromDouble(1e12));
        Throws<ArgumentOutOfRangeException>(() => Fixed.Sqrt(Fixed.FromRaw(-1)));
    }

    private static void SquareRoots()
    {
        // 내림 제곱근 r은 r² ≤ n < (r + 1)²이다. 작은 수는 모두, 큰 수는 골고루 골라 확인한다.
        for (long n = 0; n <= 200_000; n++)
        {
            CheckFloorSqrt(n);
        }
        Random random = new Random(11);
        for (int i = 0; i < 20_000; i++)
        {
            long n = random.NextInt64(0, long.MaxValue);
            CheckFloorSqrt(n);
            CheckFloorSqrt(n >> random.Next(0, 62));
        }
        CheckFloorSqrt(long.MaxValue);

        // 반올림 제곱근 r은 가장 가까운 정수다. (r - 0.5)² ≤ n < (r + 0.5)²를 4배 해서 정수로 비교한다.
        for (long n = 0; n <= 200_000; n++)
        {
            CheckRoundedSqrt(n);
        }
        for (int i = 0; i < 20_000; i++)
        {
            CheckRoundedSqrt(random.NextInt64(0, long.MaxValue / 4));
        }

        // √2 = 1.41421…는 5792.6칸이라 5793이다. 정수 제곱근은 정확하다.
        Equal(Fixed.Sqrt(Fixed.FromInt(2)).Raw, 5793L);
        Equal(Fixed.Sqrt(Fixed.FromInt(9)), Fixed.FromInt(3));
        Equal(Fixed.Sqrt(Fixed.Zero), Fixed.Zero);
    }

    private static void CheckFloorSqrt(long n)
    {
        long root = Fixed.FloorSqrt(n);
        Int128 square = (Int128)root * root;
        Int128 next = (Int128)(root + 1) * (root + 1);
        Check(square <= n && n < next, $"내림 제곱근({n}) = {root}이 틀렸습니다");
    }

    private static void CheckRoundedSqrt(long n)
    {
        long root = Fixed.RoundedSqrt(n);
        Int128 low = (Int128)(2 * root - 1) * (2 * root - 1);
        Int128 high = (Int128)(2 * root + 1) * (2 * root + 1);
        Int128 scaled = (Int128)n * 4;
        Check((root == 0 || low <= scaled) && scaled < high, $"반올림 제곱근({n}) = {root}이 틀렸습니다");
    }

    private static void VectorLength()
    {
        Equal(FixedVector.FromInt(3, 4).Length(), Fixed.FromInt(5));
        Equal(FixedVector.FromInt(-3, 4).LengthSquared(), Fixed.FromInt(25));
        // 성분이 1칸(1/4096)인 아주 짧은 벡터도 길이가 0이 되지 않는다. √2칸은 1칸으로 반올림된다.
        FixedVector tiny = new FixedVector(Fixed.FromRaw(1), Fixed.FromRaw(1));
        Equal(tiny.Length().Raw, 1L);
        Check(!tiny.Normalize().IsZero(), "아주 짧은 벡터의 방향을 잃었습니다");
        Equal(FixedVector.Zero.Normalize(), FixedVector.Zero);

        Equal(FixedVector.FromInt(3, 4).Dot(FixedVector.FromInt(-4, 3)), Fixed.Zero);
        Equal(FixedVector.FromInt(1, 0).Cross(FixedVector.FromInt(0, 1)), Fixed.One);
        Equal(FixedVector.Distance(FixedVector.FromInt(1, 1), FixedVector.FromInt(4, 5)), Fixed.FromInt(5));

        // 여러 방향과 길이의 벡터를 정규화하면 길이가 1(4096칸)에서 2칸 안이고, 반대 방향은 부호만 바뀐다.
        int[] lengths = new int[] { 1, 13, 410, 1900 };
        for (int units = 0; units < Angle.UnitsPerTurn; units += 97)
        {
            FixedVector direction = Angle.FromUnits(units).Direction();
            for (int i = 0; i < lengths.Length; i++)
            {
                FixedVector vector = direction * lengths[i];
                FixedVector unit = vector.Normalize();
                long error = Math.Abs(unit.Length().Raw - Fixed.Scale);
                Check(error <= 2, $"{units}칸 방향, 길이 {lengths[i]}의 정규화 길이가 {unit.Length()}입니다");
                Equal((-vector).Normalize(), -unit);
            }
        }
    }

    private static void SineAccuracy()
    {
        // 모든 각도에서 참값과 반 칸(1/8192) 남짓 안이어야 한다. 이 안이면 참값을 반올림한 값이거나, 참값이 두 칸의 한가운데에 아주 가까워 옆 칸이 된 경우다.
        // 참값은 실수 사인으로 구한다. 운영체제마다 마지막 자리가 달라도 이 허용 범위에는 영향이 없다.
        double worst = 0;
        for (int units = 0; units < Angle.UnitsPerTurn; units++)
        {
            Angle angle = Angle.FromUnits(units);
            double radians = units * Math.Tau / Angle.UnitsPerTurn;
            double sineError = Math.Abs(angle.Sin().Raw - Math.Sin(radians) * Fixed.Scale);
            double cosineError = Math.Abs(angle.Cos().Raw - Math.Cos(radians) * Fixed.Scale);
            worst = Math.Max(worst, Math.Max(sineError, cosineError));
            // 사인은 홀함수다. 반대 각도의 사인은 부호만 바뀐다.
            Equal((-angle).Sin(), -angle.Sin());
        }
        Check(worst <= 0.501, $"사인·코사인의 가장 큰 오차가 {worst}칸입니다");

        Equal(Angle.FromUnits(0).Sin(), Fixed.Zero);
        Equal(Angle.FromUnits(Angle.QuarterTurn).Sin(), Fixed.One);
        Equal(Angle.FromUnits(Angle.HalfTurn).Sin(), Fixed.Zero);
        Equal(Angle.FromUnits(3 * Angle.QuarterTurn).Sin(), -Fixed.One);
        Equal(Angle.FromUnits(0).Cos(), Fixed.One);
        // sin 45° = 0.70710678…는 2896.3칸이다.
        Equal(Angle.FromUnits(Angle.QuarterTurn / 2).Sin().Raw, 2896L);
    }

    private static void ArctangentAccuracy()
    {
        // 축과 대각선은 정확하다. 0 벡터는 0이다.
        Equal(Angle.Atan2(Fixed.Zero, Fixed.One).Units, 0);
        Equal(Angle.Atan2(Fixed.One, Fixed.Zero).Units, Angle.QuarterTurn);
        Equal(Angle.Atan2(Fixed.Zero, -Fixed.One).Units, Angle.HalfTurn);
        Equal(Angle.Atan2(-Fixed.One, Fixed.Zero).Units, 3 * Angle.QuarterTurn);
        Equal(Angle.Atan2(Fixed.One, Fixed.One).Units, Angle.QuarterTurn / 2);
        Equal(Angle.Atan2(-Fixed.One, -Fixed.One).Units, 5 * Angle.QuarterTurn / 2);
        Equal(Angle.Atan2(Fixed.Zero, Fixed.Zero).Units, 0);

        // 정수 격자의 모든 방향을 아주 짧은 길이(1칸)부터 맵보다 긴 길이까지 확인한다. 2^32칸을 넘는 값은 줄여서 계산하는 길도 지난다.
        long[] scales = new long[] { 1, Fixed.Scale, 37 * Fixed.Scale, 1900 * Fixed.Scale, 1L << 30 };
        double worst = 0;
        for (int s = 0; s < scales.Length; s++)
        {
            for (int y = -60; y <= 60; y++)
            {
                for (int x = -60; x <= 60; x++)
                {
                    if (x == 0 && y == 0)
                    {
                        continue;
                    }
                    Angle angle = Angle.Atan2(Fixed.FromRaw(y * scales[s]), Fixed.FromRaw(x * scales[s]));
                    double exact = Math.Atan2(y, x) * Angle.UnitsPerTurn / Math.Tau;
                    double error = Math.Abs(angle.SignedUnits() - exact);
                    // -180°와 180°는 같은 방향이다.
                    if (error > Angle.HalfTurn)
                    {
                        error = Angle.UnitsPerTurn - error;
                    }
                    worst = Math.Max(worst, error);
                }
            }
        }
        Check(worst <= 0.501, $"아크탄젠트의 가장 큰 오차가 {worst}칸입니다");

        // 방향 벡터로 되돌리면 같은 방향이다. 방향 벡터의 성분이 1/4096로 반올림된 만큼(2칸 안) 어긋날 수 있다.
        for (int units = 0; units < Angle.UnitsPerTurn; units += 13)
        {
            Angle angle = Angle.FromUnits(units);
            int back = Angle.Between(Angle.Of(angle.Direction()), angle);
            Check(back <= 2, $"{units}칸 방향을 되돌리니 {back}칸 어긋났습니다");
        }
    }

    private static void AngleConversion()
    {
        Equal(Angle.FromRadians(0).Units, 0);
        Equal(Angle.FromRadians(Math.PI).Units, Angle.HalfTurn);
        Equal(Angle.FromRadians(-Math.PI / 2).Units, 3 * Angle.QuarterTurn);
        Equal(Angle.FromRadians(Math.Tau).Units, 0);
        Equal(Angle.FromRadians(5 * Math.Tau + Math.PI / 2).Units, Angle.QuarterTurn);
        // π/3(60°)는 10922.67칸이라 10923칸이다.
        Equal(Angle.FromRadians(Math.PI / 3).Units, 10923);
        // 아주 큰 값도 한 바퀴 안으로 줄이므로 넘치지 않는다.
        Check(Angle.FromRadians(1e300).Units >= 0, "아주 큰 각도를 바꾸지 못했습니다");
        Throws<ArgumentOutOfRangeException>(() => Angle.FromRadians(double.NaN));

        Equal(Angle.FromUnits(Angle.HalfTurn).ToRadians(), -Math.PI);
        Equal(Angle.FromUnits(3 * Angle.QuarterTurn).SignedUnits(), -Angle.QuarterTurn);
        Equal((Angle.FromUnits(65535) + Angle.FromUnits(2)).Units, 1);
        Equal((Angle.FromUnits(1) - Angle.FromUnits(2)).Units, 65535);
        Equal(Angle.Between(Angle.FromUnits(10), Angle.FromUnits(65530)), 16);
        Equal(Angle.Between(Angle.FromUnits(0), Angle.FromUnits(Angle.HalfTurn)), Angle.HalfTurn);
    }

    private static void MathChecksums()
    {
        // 계산 결과 전체를 하나의 수로 섞어 고정값과 비교한다. CI가 Linux·Windows·macOS(ARM64)에서 이 검사를 돌리므로,
        // 어느 기계에서나 모든 값이 같다는 것을 확인한다. 계산 방법을 바꾸면 이 값도 바뀐다. 그때는 기록을 재현할 수 없게 된다.
        long sine = 17;
        for (int units = 0; units < Angle.UnitsPerTurn; units++)
        {
            sine = Mix(sine, Angle.FromUnits(units).Sin().Raw);
        }
        Equal(sine, -8845140281631440879L);

        long arctangent = 17;
        for (int y = -200; y <= 200; y++)
        {
            for (int x = -200; x <= 200; x++)
            {
                arctangent = Mix(arctangent, Angle.Atan2(Fixed.FromInt(y), Fixed.FromInt(x)).Units);
            }
        }
        Equal(arctangent, 7946701177633534451L);

        long roots = 17;
        for (long n = 0; n < 100_000; n++)
        {
            roots = Mix(roots, Fixed.Sqrt(Fixed.FromRaw(n * 977)).Raw);
        }
        Equal(roots, 8116043479591175757L);
    }

    // 값들을 차례로 섞어 하나의 수로 만든다. 한 값만 달라도 결과가 달라진다.
    private static long Mix(long checksum, long value)
    {
        return unchecked(checksum * 1_000_003 + value);
    }

    private static void Throws<TException>(Func<object> action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"{typeof(TException).Name} 예외가 나지 않았습니다");
    }
}
