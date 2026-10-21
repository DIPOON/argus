using Argus.Server.Game;

internal static class Program
{
    public static int Main(string[] args)
    {
        // 인자 없이 돌리면 모든 검사를 등록한다. "determinism"을 주면 운영체제와 CPU에 따라 달라질 수 있는 계산의 검사만 돌린다.
        // CI는 리눅스에서 모두 돌리고, Windows와 macOS에서는 이것만 돌린다. 서버는 리눅스에서만 운영하고,
        // 실제 시간과 소켓에 기대는 검사는 공유 실행기의 타이머 해상도와 일정 흔들림 때문에 가끔 실패할 수 있어서다.
        // "vectors"를 주면 검사 대신 클라이언트 예측의 대조 자료(client/tests/fixed-vectors.json)를 지금 계산한 값으로 다시 쓴다.
        if (args.Length == 1 && args[0] == "vectors")
        {
            return ClientParityTests.Write();
        }
        List<TestCase> tests = new List<TestCase>();
        if (args.Length == 0)
        {
            LifecycleTests.Register(tests);
            EquipmentTests.Register(tests);
            CombatTests.Register(tests);
            EncounterTests.Register(tests);
            DeterminismTests.Register(tests);
            RecordingTests.Register(tests);
            FixedTests.Register(tests);
            ClientParityTests.Register(tests);
        }
        else if (args.Length == 1 && args[0] == "determinism")
        {
            DeterminismTests.Register(tests);
            FixedTests.Register(tests);
            ClientParityTests.Register(tests);
        }
        else
        {
            Console.Error.WriteLine("사용법: dotnet run --project tests/Argus.Server.Tests [-- determinism | -- vectors]");
            return 2;
        }
        int failed = 0;
        for (int i = 0; i < tests.Count; i++)
        {
            TestCase test = tests[i];
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}\n{error}");
            }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} checks passed");
        if (failed > 0)
        {
            return 1;
        }
        return 0;
    }
}

internal sealed class TestCase
{
    public string Name { get; }
    public Action Run { get; }

    public TestCase(string name, Action run)
    {
        Name = name;
        Run = run;
    }
}

internal sealed class Fixture
{
    public Match Match { get; }
    public Player Player { get; }

    public Fixture(Rules? rules = null)
    {
        if (rules == null)
        {
            rules = new Rules { MaxEnemies = 0 };
        }
        Match = new Match("TEST01", rules, 73);
        Player = Match.Join("p", "pilot")!;
        Player.Connected = true;
        TestTools.Check(Match.Deploy(Player, BattleMap.Camp.X.ToDouble(), BattleMap.Camp.Y.ToDouble()));
        TestTools.Advance(Match, rules.DeploySeconds + 0.01);
        TestTools.Equal(Player.State, "alive");
    }
}

internal static class TestTools
{
    private static int _id = 20000;

    // 운영 서버처럼 정수 틱으로만 진행한다. 한 틱에 못 미치는 나머지는 한 틱으로 올려 센다(Rules.ToTicks).
    // 그래서 실제로 진행한 시간은 seconds 이상이고, 그보다 한 틱 미만으로 길다.
    public static void Advance(Match match, double seconds)
    {
        long ticks = Rules.ToTicks(seconds);
        for (long i = 0; i < ticks; i++)
        {
            match.Step();
        }
    }

    public static void Check(bool condition, string message = "Expected condition to be true")
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    public static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new Exception($"Expected {expected}, got {actual}");
        }
    }

    // 두 고정소수점 좌표 사이의 거리를 실수로 돌려준다. 검사에서 거리를 허용 오차와 비교할 때 쓴다.
    public static double Distance(FixedVector a, FixedVector b)
    {
        return FixedVector.Distance(a, b).ToDouble();
    }

    public static void Near(double actual, double expected, double tolerance = 0.001)
    {
        if (Math.Abs(actual - expected) > tolerance)
        {
            throw new Exception($"Expected {expected} ±{tolerance}, got {actual}");
        }
    }

    public static Player Ally(Fixture fixture, string id)
    {
        Player ally = fixture.Match.Join(id, id)!;
        ally.State = "alive";
        ally.HasDeployed = true;
        ally.Position = fixture.Player.Position;
        return ally;
    }

    public static PlayerAction Prepare(Fixture fixture, Player player, string skill, double age = 0)
    {
        _id++;
        // age초 전에 시작한 기술이다. 틱으로 올려 센다.
        PlayerAction action = new PlayerAction(_id, fixture.Match.Rules.Skill(skill), 1, fixture.Match.Tick - Rules.ToTicks(age));
        player.Actions.Add(action);
        player.BusyUntilTick = action.EndsTick;
        return action;
    }

    public static Enemy Target(Fixture fixture, EnemyDefinition? definition = null)
    {
        if (definition == null)
        {
            definition = EnemyDefinition.Grunt;
        }
        EnemyDefinition still = new EnemyDefinition(definition.Id, definition.Name, definition.Radius,
            definition.Health, 0, definition.Skills);
        _id++;
        Enemy enemy = new Enemy(_id, fixture.Player.Position + FixedVector.FromInt(50, 0), still);
        enemy.NextActionTick = long.MaxValue;
        fixture.Match.Enemies.Add(enemy);
        return enemy;
    }

    public static Enemy Threat(Fixture fixture, string kind, int tier = 1, int partialDamage = 0, double arrivesIn = 0.05)
    {
        EnemyDefinition definition = EnemyDefinition.Grunt;
        if (tier == 2)
        {
            definition = EnemyDefinition.Breaker;
        }
        Enemy enemy = Target(fixture, definition);
        _id++;
        enemy.Action = new EnemyAction(_id, new EnemySkill(kind, tier, partialDamage, arrivesIn, 85, 25), Angle.FromRadians(Math.PI), fixture.Match.Tick);
        return enemy;
    }
}
