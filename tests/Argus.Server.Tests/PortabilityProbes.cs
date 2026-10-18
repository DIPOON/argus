using System.Diagnostics;
using System.Numerics;
using static System.Math;

namespace PortabilityProbes;

// 이식성 검사(PortabilityCheck)가 실제로 잡는지 보려고 일부러 금지한 함수를 부르는 예제다. 게임 코드가 아니며 실행하지 않는다.
// 소스를 글자로 찾던 방식이 놓치던 경우(using static, 형식 메서드, Math.SinCos, 람다, 문자열 보간 등)를 모았다.
internal static class Probe
{
    internal static double UsingStatic(double x)
    {
        return Sin(x);
    }

    internal static double TypeMethod(double x)
    {
        return double.Atan2(x, 1);
    }

    internal static double Pair(double x)
    {
        ValueTuple<double, double> pair = Math.SinCos(x);
        return pair.Item1 + pair.Item2;
    }

    internal static double Estimate(double x)
    {
        return Math.ReciprocalEstimate(x);
    }

    internal static Func<double, double> Lambda()
    {
        return value => Math.Atan2(value, 2);
    }

    internal static string Interpolated(double x)
    {
        return $"{Math.Cos(x)}";
    }

    internal static float Fused(float a)
    {
        return MathF.FusedMultiplyAdd(a, a, a);
    }

    internal static Vector2 Blend(Vector2 a, Vector2 b)
    {
        return Vector2.Lerp(a, b, 0.5f);
    }

    internal static int Unseeded()
    {
        Random random = new Random();
        return random.Next();
    }

    internal static long Clock()
    {
        return DateTime.UtcNow.Ticks + Stopwatch.GetTimestamp();
    }

    // 허용한 함수만 쓴다. 검사에 걸리면 안 된다.
    internal static double Allowed(double x)
    {
        return Math.Sqrt(Math.Abs(x)) + Math.Floor(x) + Vector2.Distance(Vector2.Zero, new Vector2(3, 4));
    }
}
