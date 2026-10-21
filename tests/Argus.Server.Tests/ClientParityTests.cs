using System.Globalization;
using System.Text;
using Argus.Server.Game;
using static TestTools;

// 클라이언트 예측(client/src/fixed.ts, movement.ts)이 서버와 같은 계산을 하는지 대조할 자료를 만든다.
// 서버가 지금 계산한 값을 client/tests/fixed-vectors.json에 적어 두고, 브라우저 검사(client/tests/fixed.spec.ts)가 같은 값을 얻는지 본다.
// 이동 시나리오는 실제 Match에 입력을 넣고 한 틱씩 진행해 얻은 위치라, 클라이언트가 서버의 진행 순서까지 따라 하는지 확인한다.
// 서버 계산이 바뀌면 이 검사가 실패한다. 의도한 변경이면 `dotnet run --project tests/Argus.Server.Tests -- vectors`로 다시 만든다.
internal static class ClientParityTests
{
    private const string Regenerate = "dotnet run --project tests/Argus.Server.Tests -- vectors";
    // 2^26 - 1이다. 성분이 이보다 작으면 성분끼리 곱해도, 두 제곱을 더해도 2^53 - 1을 넘지 않는다.
    private const long VectorLimit = (1L << 26) - 1;

    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("The client parity vectors hold exactly what the server computes now", VectorsMatch));
    }

    private static void VectorsMatch()
    {
        string path = FindPath();
        // 저장소는 줄바꿈을 LF로 두지만(.gitattributes), 다른 설정으로 받았어도 내용이 같으면 통과한다.
        string saved = File.ReadAllText(path).Replace("\r\n", "\n");
        string current = Build();
        Check(saved == current, $"{path}가 서버가 지금 계산한 값과 다릅니다. 의도한 변경이면 `{Regenerate}`로 다시 만드세요");
    }

    // 대조 자료 파일을 지금 계산한 값으로 다시 쓴다.
    public static int Write()
    {
        string path = FindPath();
        File.WriteAllText(path, Build());
        Console.WriteLine($"{path}에 대조 자료를 썼습니다");
        return 0;
    }

    // 저장소의 client/tests 폴더를 현재 폴더에서 위로 올라가며 찾는다. 못 찾으면 실행 파일이 있는 폴더에서 다시 찾는다.
    private static string FindPath()
    {
        string? found = FindFrom(Directory.GetCurrentDirectory());
        if (found == null)
        {
            found = FindFrom(AppContext.BaseDirectory);
        }
        if (found == null)
        {
            throw new InvalidOperationException("저장소의 client/tests 폴더를 찾지 못했습니다. 저장소 안에서 검사를 돌리세요");
        }
        return found;
    }

    private static string? FindFrom(string start)
    {
        DirectoryInfo? folder = new DirectoryInfo(start);
        while (folder != null)
        {
            string tests = Path.Combine(folder.FullName, "client", "tests");
            if (Directory.Exists(tests))
            {
                return Path.Combine(tests, "fixed-vectors.json");
            }
            folder = folder.Parent;
        }
        return null;
    }

    public static string Build()
    {
        StringBuilder text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"note\": \"서버(C#)가 계산한 값이다. 손으로 고치지 말고 ").Append(Regenerate).Append("로 다시 만든다.\",\n");
        AppendRows(text, "multiply", MultiplyRows());
        AppendRows(text, "divide", DivideRows());
        AppendRows(text, "divideInt", DivideIntRows());
        AppendRows(text, "floorDivide", FloorDivideRows());
        AppendRows(text, "ceilingDivide", CeilingDivideRows());
        AppendRows(text, "sqrt", SqrtRows());
        AppendRows(text, "fromDouble", FromDoubleRows());
        AppendRows(text, "vectors", VectorRows());
        AppendRows(text, "throws", ThrowRows());
        AppendMap(text);
        AppendScenarios(text);
        text.Append("}\n");
        return text.ToString();
    }

    // 경계 근처의 Raw 값이다. 딱 절반(2048), 1 근처(4096), 대원·적의 이동 거리와 반지름, 맵 크기 근처를 고른다.
    private static readonly long[] Edges = new long[]
    {
        0, 1, -1, 2, -2, 3, -3, 2047, 2048, 2049, -2047, -2048, -2049, 4095, 4096, 4097, -4096, 6144, -6144,
        10240, 24576, 27307, 53248, -53248, 1 << 20, -(1 << 20) + 7, 8191999, -8191999, VectorLimit, -VectorLimit
    };

    private static List<string> MultiplyRows()
    {
        List<string> rows = new List<string>();
        for (int i = 0; i < Edges.Length; i++)
        {
            for (int j = i; j < Edges.Length; j++)
            {
                rows.Add(Multiply(Edges[i], Edges[j]));
            }
        }
        NumberSequence numbers = new NumberSequence(11);
        for (int i = 0; i < 120; i++)
        {
            long a = numbers.Between(VectorLimit);
            long b = numbers.Between(VectorLimit);
            rows.Add(Multiply(a, b));
        }
        return rows;
    }

    private static string Multiply(long a, long b)
    {
        Fixed product = Fixed.FromRaw(a) * Fixed.FromRaw(b);
        return Row(a, b, product.Raw);
    }

    private static List<string> DivideRows()
    {
        List<string> rows = new List<string>();
        // 딱 절반이 되는 몫(1 / 2, 3 / 2)과 음수, 1보다 작은 나누는 수를 넣는다.
        long[,] pairs = new long[,]
        {
            { 4096, 3 }, { -4096, 3 }, { 1, 2 }, { -1, 2 }, { 3, 2 }, { -3, 2 }, { 1, 8192 }, { 3, 8192 }, { -3, 8192 },
            { 6144, 4096 }, { 2896, 4096 }, { 2896, 4095 }, { 17376, 24576 }, { -17376, 24576 }, { 8191999, 1 }, { 1, 8191999 }
        };
        for (int i = 0; i < pairs.GetLength(0); i++)
        {
            rows.Add(Divide(pairs[i, 0], pairs[i, 1]));
        }
        NumberSequence numbers = new NumberSequence(12);
        for (int i = 0; i < 120; i++)
        {
            // 나뉘는 수에 4096을 곱해도 2^53을 넘지 않게 2^40 안에서 고른다.
            long a = numbers.Between(1L << 40);
            long b = numbers.Between(1L << 30);
            if (b == 0)
            {
                b = 1;
            }
            rows.Add(Divide(a, b));
        }
        return rows;
    }

    private static string Divide(long a, long b)
    {
        Fixed quotient = Fixed.FromRaw(a) / Fixed.FromRaw(b);
        return Row(a, b, quotient.Raw);
    }

    private static List<string> DivideIntRows()
    {
        List<string> rows = new List<string>();
        int[] divisors = new int[] { 1, 2, 3, 4, 5, 7, 30, -3, -30, 1000003 };
        // 30으로 나눌 때 딱 절반이 되는 15·45와 초당 200의 Raw(819200)를 넣는다.
        long[] values = new long[] { 1, -1, 2, -2, 3, -3, 15, -15, 45, -45, 737280, 819200, -819200, 1L << 50 };
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = 0; j < divisors.Length; j++)
            {
                rows.Add(DivideInt(values[i], divisors[j]));
            }
        }
        NumberSequence numbers = new NumberSequence(13);
        for (int i = 0; i < 60; i++)
        {
            long a = numbers.Between(1L << 50);
            int b = divisors[i % divisors.Length];
            rows.Add(DivideInt(a, b));
        }
        return rows;
    }

    private static string DivideInt(long a, int b)
    {
        Fixed quotient = Fixed.FromRaw(a) / b;
        return Row(a, b, quotient.Raw);
    }

    private static List<long> DivisionValues(int seed)
    {
        List<long> values = new List<long>();
        int[] divisors = new int[] { 1, 4, 48 };
        // 나누어떨어지는 값과 그 앞뒤 Raw 하나를 양쪽 부호로 넣는다.
        for (int i = 0; i < divisors.Length; i++)
        {
            long whole = divisors[i] * Fixed.Scale;
            for (long k = -3; k <= 3; k++)
            {
                values.Add(k * whole - 1);
                values.Add(k * whole);
                values.Add(k * whole + 1);
            }
        }
        NumberSequence numbers = new NumberSequence((ulong)seed);
        for (int i = 0; i < 40; i++)
        {
            values.Add(numbers.Between(1L << 40));
        }
        return values;
    }

    private static List<string> FloorDivideRows()
    {
        List<string> rows = new List<string>();
        List<long> values = DivisionValues(14);
        int[] divisors = new int[] { 1, 4, 48 };
        for (int i = 0; i < values.Count; i++)
        {
            for (int j = 0; j < divisors.Length; j++)
            {
                int floored = Fixed.FromRaw(values[i]).FloorDivide(divisors[j]);
                rows.Add(Row(values[i], divisors[j], floored));
            }
        }
        return rows;
    }

    private static List<string> CeilingDivideRows()
    {
        List<string> rows = new List<string>();
        List<long> values = DivisionValues(15);
        int[] divisors = new int[] { 1, 4, 48 };
        for (int i = 0; i < values.Count; i++)
        {
            for (int j = 0; j < divisors.Length; j++)
            {
                int ceiled = Fixed.FromRaw(values[i]).CeilingDivide(divisors[j]);
                rows.Add(Row(values[i], divisors[j], ceiled));
            }
        }
        return rows;
    }

    private static List<string> SqrtRows()
    {
        List<string> rows = new List<string>();
        List<long> values = new List<long>();
        for (long value = 0; value <= 30; value++)
        {
            values.Add(value);
        }
        // 제곱수와 그 앞뒤, (r + 0.5)² 근처, 2^53 바로 아래를 넣는다.
        long[] roots = new long[] { 1000, 4096, 94906265 };
        for (int i = 0; i < roots.Length; i++)
        {
            long square = roots[i] * roots[i];
            values.Add(square - 1);
            values.Add(square);
            values.Add(square + 1);
            values.Add(square + roots[i]);
            values.Add(square + roots[i] + 1);
        }
        values.Add(Fixed.SafeLimit);
        NumberSequence numbers = new NumberSequence(16);
        for (int i = 0; i < 60; i++)
        {
            long value = (long)(numbers.Next() % (ulong)Fixed.SafeLimit);
            values.Add(value);
        }
        for (int i = 0; i < values.Count; i++)
        {
            rows.Add(Row(values[i], Fixed.RoundedSqrt(values[i])));
        }
        return rows;
    }

    private static List<string> FromDoubleRows()
    {
        List<double> values = new List<double>();
        // Raw의 딱 절반(1/8192의 홀수 배)은 0에서 먼 쪽으로 간다. 0.49999999999999994/4096은 절반보다 아주 조금 작다.
        double[] fixedValues = new double[]
        {
            0, 1.0 / 8192, -1.0 / 8192, 3.0 / 8192, -3.0 / 8192, 5.0 / 8192, -5.0 / 8192, 0.49999999999999994 / 4096,
            -0.49999999999999994 / 4096, 1 / Math.Sqrt(2), -1 / Math.Sqrt(2), 0.6, 0.8, -0.3, 0.4, 1.01, -1.01, 0.01,
            13, 180, 200, 300, 123.456, -123.456, 466, 1919.999, 1_000_000_000, -1_000_000_000
        };
        for (int i = 0; i < fixedValues.Length; i++)
        {
            values.Add(fixedValues[i]);
        }
        NumberSequence numbers = new NumberSequence(17);
        for (int i = 0; i < 80; i++)
        {
            // 맵 안팎의 좌표처럼 정수 부분과 소수 부분을 함께 가진 실수다.
            double whole = numbers.Between(1_000_000);
            double fraction = (double)(numbers.Next() >> 11) / (1UL << 53);
            values.Add(whole + fraction);
        }
        List<string> rows = new List<string>();
        for (int i = 0; i < values.Count; i++)
        {
            Fixed converted = Fixed.FromDouble(values[i]);
            rows.Add("[" + Number(values[i]) + ", " + Number(converted.Raw) + "]");
        }
        return rows;
    }

    private static List<string> VectorRows()
    {
        List<string> rows = new List<string>();
        long[,] fixedVectors = new long[,]
        {
            { 0, 0 }, { 1, 0 }, { 0, -1 }, { 2, 4 }, { 3, 5 }, { -79, 79 }, { 2896, 2896 }, { 2458, 3277 }, { -1229, 1638 },
            { 4096, 41 }, { 4137, 0 }, { 24576, 0 }, { VectorLimit, VectorLimit }, { -VectorLimit, 3 }
        };
        for (int i = 0; i < fixedVectors.GetLength(0); i++)
        {
            rows.Add(VectorRow(fixedVectors[i, 0], fixedVectors[i, 1]));
        }
        NumberSequence numbers = new NumberSequence(18);
        for (int i = 0; i < 80; i++)
        {
            // 짧은 벡터(입력 방향)와 긴 벡터(좌표 차이)를 반씩 고른다.
            long limit = VectorLimit;
            if (i % 2 == 0)
            {
                limit = 8192;
            }
            rows.Add(VectorRow(numbers.Between(limit), numbers.Between(limit)));
        }
        return rows;
    }

    private static string VectorRow(long x, long y)
    {
        FixedVector vector = new FixedVector(Fixed.FromRaw(x), Fixed.FromRaw(y));
        FixedVector normal = vector.Normalize();
        return Row(x, y, vector.LengthSquared().Raw, vector.Length().Raw, normal.X.Raw, normal.Y.Raw);
    }

    // 서버가 예외를 내는 계산이다. 클라이언트도 같은 계산에서 예외를 내야 한다. 적은 값이 정말 예외를 내는지도 여기서 확인한다.
    private static List<string> ThrowRows()
    {
        List<string> rows = new List<string>();
        rows.Add(ExpectThrow("multiply", 1L << 27, 1L << 27, () => Fixed.FromRaw(1L << 27) * Fixed.FromRaw(1L << 27)));
        rows.Add(ExpectThrow("divide", 1L << 42, 4096, () => Fixed.FromRaw(1L << 42) / Fixed.FromRaw(4096)));
        rows.Add(ExpectThrow("divide", 4096, 0, () => Fixed.FromRaw(4096) / Fixed.FromRaw(0)));
        rows.Add(ExpectThrow("divideInt", 4096, 0, () => Fixed.FromRaw(4096) / 0));
        rows.Add(ExpectThrow("length", 1L << 27, 0, () => new FixedVector(Fixed.FromRaw(1L << 27), Fixed.Zero).Length()));
        rows.Add(ExpectThrow("floorDivide", 4096, 0, () => Fixed.FromRaw(4096).FloorDivide(0)));
        rows.Add(ExpectThrow("ceilingDivide", 4096, -4, () => Fixed.FromRaw(4096).CeilingDivide(-4)));
        rows.Add(ExpectThrow("fromDouble", 10_000_000_000, 0, () => Fixed.FromDouble(10_000_000_000)));
        return rows;
    }

    private static string ExpectThrow(string operation, long a, long b, Func<object> calculation)
    {
        bool threw = false;
        try
        {
            calculation();
        }
        catch (OverflowException)
        {
            threw = true;
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        catch (DivideByZeroException)
        {
            threw = true;
        }
        Check(threw, $"{operation}({a}, {b})가 예외를 내지 않았습니다");
        return "[\"" + operation + "\", " + Number(a) + ", " + Number(b) + "]";
    }

    private static void AppendMap(StringBuilder text)
    {
        BattleMap map = new BattleMap();
        text.Append("  \"map\": { \"cell\": ").Append(Number(BattleMap.Cell));
        text.Append(", \"columns\": ").Append(Number(BattleMap.Columns));
        text.Append(", \"rows\": ").Append(Number(BattleMap.Rows));
        text.Append(", \"walls\": \"").Append(Convert.ToBase64String(map.Tiles)).Append("\" },\n");
    }

    // 실제 Match에 입력을 넣고 한 틱씩 진행한 위치다. 열린 땅의 여러 방향, 벽 모서리와 벽 면, 적, 맵 가장자리,
    // 걸음 수가 다른 이동 속도(한 틱에 3·6·6.667·10)를 고른다. 대각선 입력은 클라이언트처럼 (1, 1)을 길이로 나눈 값이다.
    private static void AppendScenarios(StringBuilder text)
    {
        double diagonal = 1 / Math.Sqrt(2);
        List<string> scenarios = new List<string>();

        List<double[]> open = new List<double[]>();
        AddInputs(open, 8, 1, 0);
        AddInputs(open, 8, diagonal, diagonal);
        AddInputs(open, 8, 0.6, -0.8);
        AddInputs(open, 6, -0.3, 0.4);
        AddInputs(open, 6, 1, 0.01);
        AddInputs(open, 3, 0, 0);
        AddInputs(open, 8, -1, 0);
        AddInputs(open, 8, -diagonal, -diagonal);
        scenarios.Add(RunScenario("열린 땅에서 여러 방향", 180, FixedVector.FromInt(300, 730), new List<FixedVector>(), new List<EnemyDefinition>(), open));

        // 장애물 칸 (10, 10)·(10, 11)은 x 480~528, y 480~576이다. 왼쪽 위 꼭짓점을 대각선으로 스친 뒤 왼쪽 면을 따라 내려간다.
        List<double[]> corner = new List<double[]>();
        AddInputs(corner, 16, diagonal, diagonal);
        AddInputs(corner, 8, 0, 1);
        scenarios.Add(RunScenario("벽 모서리를 스치고 벽 면을 따라 내려감", 180, FixedVector.FromInt(440, 452), new List<FixedVector>(), new List<EnemyDefinition>(), corner));

        List<double[]> slide = new List<double[]>();
        AddInputs(slide, 12, 1, 0.25);
        AddInputs(slide, 8, 1, -0.25);
        scenarios.Add(RunScenario("벽 면에 비스듬히 밀며 미끄러짐", 180, FixedVector.FromInt(455, 530), new List<FixedVector>(), new List<EnemyDefinition>(), slide));

        List<FixedVector> bodies = new List<FixedVector>();
        List<EnemyDefinition> kinds = new List<EnemyDefinition>();
        bodies.Add(FixedVector.FromInt(352, 730));
        kinds.Add(EnemyDefinition.Grunt);
        bodies.Add(FixedVector.FromInt(392, 690));
        kinds.Add(EnemyDefinition.Warden);
        List<double[]> blocked = new List<double[]>();
        AddInputs(blocked, 8, 1, 0);
        AddInputs(blocked, 8, diagonal, -diagonal);
        AddInputs(blocked, 8, 1, 0);
        AddInputs(blocked, 6, 0, 1);
        scenarios.Add(RunScenario("멈춘 적에 막히고 돌아감", 180, FixedVector.FromInt(300, 730), bodies, kinds, blocked));

        List<double[]> uneven = new List<double[]>();
        AddInputs(uneven, 8, 0.6, 0.8);
        AddInputs(uneven, 8, diagonal, diagonal);
        AddInputs(uneven, 8, 1, 0);
        AddInputs(uneven, 8, -0.3, 0.4);
        AddInputs(uneven, 8, 0, -1);
        scenarios.Add(RunScenario("나누어떨어지지 않는 걸음(초당 200)", 200, FixedVector.FromInt(300, 730), new List<FixedVector>(), new List<EnemyDefinition>(), uneven));

        List<double[]> edge = new List<double[]>();
        AddInputs(edge, 10, -1, 0.1);
        AddInputs(edge, 6, -diagonal, -diagonal);
        scenarios.Add(RunScenario("맵 가장자리 벽에 막힘", 180, FixedVector.FromInt(75, 730), new List<FixedVector>(), new List<EnemyDefinition>(), edge));

        List<double[]> slow = new List<double[]>();
        AddInputs(slow, 10, 0.6, 0.8);
        AddInputs(slow, 10, -diagonal, diagonal);
        scenarios.Add(RunScenario("한 걸음으로 끝나는 이동(초당 90)", 90, FixedVector.FromInt(300, 730), new List<FixedVector>(), new List<EnemyDefinition>(), slow));

        List<double[]> fast = new List<double[]>();
        AddInputs(fast, 10, diagonal, -diagonal);
        AddInputs(fast, 6, 1, 0);
        scenarios.Add(RunScenario("세 걸음으로 나누는 빠른 이동(초당 300)", 300, FixedVector.FromInt(420, 640), new List<FixedVector>(), new List<EnemyDefinition>(), fast));

        text.Append("  \"scenarios\": [\n");
        for (int i = 0; i < scenarios.Count; i++)
        {
            text.Append(scenarios[i]);
            if (i < scenarios.Count - 1)
            {
                text.Append(',');
            }
            text.Append('\n');
        }
        text.Append("  ]\n");
    }

    private static void AddInputs(List<double[]> inputs, int count, double moveX, double moveY)
    {
        for (int i = 0; i < count; i++)
        {
            inputs.Add(new double[] { moveX, moveY });
        }
    }

    private static string RunScenario(string name, double speed, FixedVector start, List<FixedVector> bodies, List<EnemyDefinition> kinds,
        List<double[]> inputs)
    {
        Rules rules = new Rules { MaxEnemies = 0, PlayerSpeed = speed };
        Match match = new Match("PARITY", rules, 73);
        Player player = match.Join("p", "pilot")!;
        player.Connected = true;
        Check(match.Deploy(player, BattleMap.Camp.X.ToDouble(), BattleMap.Camp.Y.ToDouble()));
        Advance(match, rules.DeploySeconds + 0.01);
        Equal(player.State, "alive");
        player.Position = start;
        // 막는 몸은 움직이지도 행동하지도 않는 적이다. 클라이언트는 스냅샷의 적 위치로 같은 몸을 본다.
        for (int i = 0; i < bodies.Count; i++)
        {
            EnemyDefinition kind = kinds[i];
            EnemyDefinition still = new EnemyDefinition(kind.Id, kind.Name, kind.Radius, kind.Health, 0, kind.Skills);
            Enemy enemy = new Enemy(9000 + i, bodies[i], still);
            enemy.NextActionTick = long.MaxValue;
            match.Enemies.Add(enemy);
        }

        StringBuilder text = new StringBuilder();
        text.Append("    {\n");
        text.Append("      \"name\": \"").Append(name).Append("\",\n");
        text.Append("      \"speed\": ").Append(Number(speed)).Append(",\n");
        text.Append("      \"radius\": ").Append(Number(Rules.PlayerRadius.Raw)).Append(",\n");
        text.Append("      \"start\": ").Append(Row(start.X.Raw, start.Y.Raw)).Append(",\n");
        text.Append("      \"bodies\": [");
        for (int i = 0; i < match.Enemies.Count; i++)
        {
            Enemy enemy = match.Enemies[i];
            if (i > 0)
            {
                text.Append(", ");
            }
            text.Append(Row(enemy.Position.X.Raw, enemy.Position.Y.Raw, enemy.Definition.Radius.Raw));
        }
        text.Append("],\n");
        // 한 줄이 한 틱이다. 보낸 이동 입력과 그 틱을 진행한 뒤의 위치(Raw)를 적는다.
        text.Append("      \"ticks\": [\n");
        for (int i = 0; i < inputs.Count; i++)
        {
            double moveX = inputs[i][0];
            double moveY = inputs[i][1];
            Check(match.Input(player, i, moveX, moveY, 0, 0), $"{name}: {i}번째 입력을 받지 않았습니다");
            match.Step();
            text.Append("        [").Append(Number(moveX)).Append(", ").Append(Number(moveY)).Append(", ");
            text.Append(Number(player.Position.X.Raw)).Append(", ").Append(Number(player.Position.Y.Raw)).Append(']');
            if (i < inputs.Count - 1)
            {
                text.Append(',');
            }
            text.Append('\n');
        }
        text.Append("      ]\n");
        text.Append("    }");
        return text.ToString();
    }

    // 한 줄에 행 하나씩 적는다.
    private static void AppendRows(StringBuilder text, string name, List<string> rows)
    {
        text.Append("  \"").Append(name).Append("\": [\n");
        for (int i = 0; i < rows.Count; i++)
        {
            text.Append("    ").Append(rows[i]);
            if (i < rows.Count - 1)
            {
                text.Append(',');
            }
            text.Append('\n');
        }
        text.Append("  ],\n");
    }

    private static string Row(params long[] values)
    {
        StringBuilder text = new StringBuilder();
        text.Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }
            text.Append(Number(values[i]));
        }
        text.Append(']');
        return text.ToString();
    }

    private static string Number(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    // 실수는 되읽으면 같은 값이 되는 가장 짧은 표기로 적는다. JavaScript의 JSON.parse도 같은 실수로 읽는다.
    private static string Number(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    // 대조 자료에 쓸 수를 고르는 정해진 수열(SplitMix64)이다. System.Random은 .NET 버전에 따라 수열이 바뀔 수 있어 쓰지 않는다.
    private sealed class NumberSequence
    {
        private ulong _state;

        public NumberSequence(ulong seed)
        {
            _state = seed;
        }

        public ulong Next()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                ulong value = _state;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }

        // -limit 이상 limit 이하의 정수다.
        public long Between(long limit)
        {
            ulong span = (ulong)limit * 2 + 1;
            long value = (long)(Next() % span);
            return value - limit;
        }
    }
}
