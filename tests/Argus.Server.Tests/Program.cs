using Argus.Server.Game;
using Argus.Server.Networking;
using Argus.Server.Recording;
using Microsoft.Extensions.Logging.Abstractions;

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
            LayoutTests.Register(tests);
            GeneratorTests.Register(tests);
            ExplorationTests.Register(tests);
        }
        else if (args.Length == 1 && args[0] == "determinism")
        {
            DeterminismTests.Register(tests);
            FixedTests.Register(tests);
            ClientParityTests.Register(tests);
            GeneratorTests.Register(tests);
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
        TestTools.Check(Match.Deploy(Player, Match.Layout.Camp.X.ToDouble(), Match.Layout.Camp.Y.ToDouble()));
        TestTools.Advance(Match, rules.DeploySeconds + 0.01);
        TestTools.Equal(Player.State, "alive");
    }
}

// 검사용 RoomHost가 방마다 쓰는 시드다. 정수 1, 2, 3, …으로 만든 시드를 만든 순서대로 내준다.
// 그래서 검사를 몇 번 돌려도 같은 순서로 만든 방은 같은 맵과 같은 적으로 시작한다.
internal sealed class TestSeeds
{
    private int _last;

    public byte[] Next()
    {
        int number = Interlocked.Increment(ref _last);
        return SeededRandom.SeedFromNumber(number);
    }

    // 지금까지 내준 시드 수다. RoomHost는 맵을 만들 때마다 시드를 하나 받으므로, 거절한 요청이 맵을 만들지 않았는지 볼 때 쓴다.
    public int Issued
    {
        get
        {
            return Volatile.Read(ref _last);
        }
    }
}

internal static class TestTools
{
    private static int _id = 20000;

    // 시드를 정해 둔 RoomHost다. 운영 서버는 방마다 무작위 시드로 맵을 만들지만, 검사가 무작위 맵에서 돌면
    // 실행마다 결과가 달라지고 실패한 맵을 다시 볼 수 없다. 검사의 RoomHost는 모두 이것으로 만든다.
    // seeds를 넘기면 검사가 시드를 내주는 방식을 정할 수 있다(내준 시드 수 세기, 맵을 만드는 도중에 끼어들기 등).
    public static RoomHost NewHost(RecordingStore? recordings = null, bool allowHandmade = false, Func<byte[]>? seeds = null)
    {
        if (seeds == null)
        {
            TestSeeds numbered = new TestSeeds();
            seeds = numbered.Next;
        }
        return new RoomHost(NullLogger<RoomHost>.Instance, recordings, allowHandmade, seeds);
    }

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

    // 살아 있는 대원을 point로 옮겨 한 틱 진행하고 제자리로 돌려놓는다. 그 틱에 대원의 시야로 point 둘레가 분대가 본 칸이 된다.
    // 안 본 칸은 스냅샷에 실리지 않고 투입도 받지 않으므로, 캠프에서 먼 곳을 다루는 검사는 먼저 그곳을 본다.
    // 한 틱이 지나고 point의 상자를 쓸 수도 있으므로, 상자 자리가 아닌 곳을 고른다.
    public static void Scout(Match match, Player player, FixedVector point)
    {
        Check(player.State == "alive", "살아 있는 대원만 둘러볼 수 있습니다");
        FixedVector home = player.Position;
        player.Position = point;
        match.Step();
        player.Position = home;
        Check(match.Explored(point), $"{point}를 보지 못했습니다");
    }

    public static void Check(bool condition, string message = "Expected condition to be true")
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    // action이 TException(또는 그것을 물려받은 예외)을 내야 통과한다.
    public static void Throws<TException>(Func<object> action) where TException : Exception
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
        return Prepare(fixture, player, fixture.Match.Rules.Skill(skill), age);
    }

    // 기술 정의를 직접 받는다. id만 바꾼 정의처럼 검사가 만든 기술로도 쓸 수 있다.
    public static PlayerAction Prepare(Fixture fixture, Player player, SkillDefinition skill, double age = 0)
    {
        _id++;
        // age초 전에 시작한 기술이다. 틱으로 올려 센다.
        PlayerAction action = new PlayerAction(_id, skill, 1, fixture.Match.Tick - Rules.ToTicks(age));
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

    // 실제 적 정의로 만든 적이 그 정의의 기술 skill을 쓰는 중이다. 기술은 arrivesIn초 뒤에 닿는다(BeginAction).
    public static Enemy Threat(Fixture fixture, EnemyDefinition definition, EnemySkill skill, double arrivesIn = 0.05)
    {
        Check(Array.IndexOf(definition.Skills, skill) >= 0, $"{definition.Id} 정의에 없는 기술입니다");
        Enemy enemy = Target(fixture, definition);
        BeginAction(fixture, enemy, skill, arrivesIn);
        return enemy;
    }

    // enemy가 skill로 새 행동을 시작한다. 기술이 arrivesIn초 뒤에 닿도록 시작 틱을 예고 시간만큼 앞당긴다.
    // 그래서 예고를 다 기다리지 않고도 실제 기술의 종류·티어·부분 대응 피해·사거리로 판정받는다. 선딜 배율은 1이다.
    public static void BeginAction(Fixture fixture, Enemy enemy, EnemySkill skill, double arrivesIn = 0.05)
    {
        _id++;
        long startTick = fixture.Match.Tick + Rules.ToTicks(arrivesIn) - Rules.ToTicks(skill.Windup);
        enemy.Action = new EnemyAction(_id, skill, Angle.FromRadians(Math.PI), startTick);
    }
}
