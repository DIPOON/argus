using Argus.Server.Game;

internal static class Program
{
    public static int Main()
    {
        List<TestCase> tests = new List<TestCase>();
        LifecycleTests.Register(tests);
        EquipmentTests.Register(tests);
        CombatTests.Register(tests);
        EncounterTests.Register(tests);
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
        TestTools.Check(Match.Deploy(Player, BattleMap.Camp.X, BattleMap.Camp.Y));
        TestTools.Advance(Match, rules.DeploySeconds + 0.01);
        TestTools.Equal(Player.State, "alive");
    }
}

internal static class TestTools
{
    private static int _id = 20000;

    public static void Advance(Match match, double seconds)
    {
        double until = match.Now + seconds;
        while (until - match.Now > 0.00000001)
        {
            match.Step(Math.Min(Rules.Step, until - match.Now));
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
        PlayerAction action = new PlayerAction(_id, fixture.Match.Rules.Skill(skill), 1, fixture.Match.Now - age);
        player.Actions.Add(action);
        player.BusyUntil = action.EndsAt;
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
        Enemy enemy = new Enemy(_id, fixture.Player.Position + new System.Numerics.Vector2(50, 0), still);
        enemy.NextActionAt = double.MaxValue;
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
        enemy.Action = new EnemyAction(_id, new EnemySkill(kind, tier, partialDamage, arrivesIn, 85, 25), Math.PI, fixture.Match.Now);
        return enemy;
    }
}
