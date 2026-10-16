using System.Numerics;
using Argus.Server.Game;
using static TestTools;

internal static class EncounterTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("The first mission has only tier-one enemies, including late reinforcements", FirstMission));
        tests.Add(new TestCase("Every initial enemy is placed at its own spot with its kind, in the first and the elite mission", InitialPlacement));
        tests.Add(new TestCase("AI telegraphs and actual hits both wait for the longer mission windup", LongerWindup));
        tests.Add(new TestCase("Reinforcements never appear inside the squad's shared sight", HiddenSpawns));
        tests.Add(new TestCase("An enemy whose body clips a wall corner on an open sight line still reaches the player", CornerSlip));
        tests.Add(new TestCase("The flow field has no heading inside its own source cell", FlowSource));
        tests.Add(new TestCase("An enemy touching a wall walks straight along it instead of zigzagging", WallHug));
    }

    private static void InitialPlacement()
    {
        // 처음 배치하는 적은 정해진 자리마다 정해진 종류로 하나씩 생성되어야 한다. 빠지거나 다른 자리로 옮겨지면 안 된다.
        // 예전 자리 (970, 570)는 장애물 칸 (20, 11) 안이라 그 적이 한 번도 생성되지 않았고, 엘리트 작전에서는 분쇄자가 빠졌다.
        // 지금은 자리가 막혀 있으면 생성자가 예외를 내므로 이 검사가 바로 실패한다.
        string[] firstKinds = new string[] { "grunt", "grunt", "grunt" };
        string[] eliteKinds = new string[] { "grunt", "breaker", "warden" };
        CheckPlacement(new Match("PLACE1", new Rules(), 73), firstKinds);
        CheckPlacement(new Match("PLACE2", new Rules { AllowEliteEnemies = true }, 73), eliteKinds);
    }

    private static void CheckPlacement(Match match, string[] kinds)
    {
        Equal(Match.InitialEnemySpots.Count, kinds.Length);
        Equal(match.Enemies.Count, kinds.Length);
        for (int i = 0; i < kinds.Length; i++)
        {
            Enemy enemy = match.Enemies[i];
            Vector2 spot = Match.InitialEnemySpots[i];
            Check(enemy.Position == spot, $"{i}번째 적이 {enemy.Position}에 있습니다. 자리는 {spot}입니다");
            Equal(enemy.Definition.Id, kinds[i]);
        }
    }

    private static void FirstMission()
    {
        Rules rules = new Rules();
        Fixture f = new Fixture(rules);
        Check(f.Match.Enemies.Count > 0);
        // 피격으로 작전이 일찍 끝나지 않도록 생성 규칙 검사에서만 충분한 체력을 준다.
        f.Player.Hp = 1000;
        HashSet<int> seen = new HashSet<int>();
        bool lateReinforcement = false;
        for (int step = 0; step < 90; step++)
        {
            for (int i = 0; i < f.Match.Enemies.Count; i++)
            {
                Enemy enemy = f.Match.Enemies[i];
                Equal(enemy.Definition.Id, "grunt");
                for (int j = 0; j < enemy.Definition.Skills.Length; j++)
                {
                    Equal(enemy.Definition.Skills[j].Tier, 1);
                }
                if (seen.Add(enemy.Id) && f.Match.Elapsed > 45)
                {
                    lateReinforcement = true;
                }
            }
            Advance(f.Match, 1);
        }
        Check(lateReinforcement);

        // 이후 작전에서 사용할 엘리트 정의와 생성 경로는 그대로 사용할 수 있다.
        Rules eliteRules = new Rules { AllowEliteEnemies = true, EnemyWindupMultiplier = 1 };
        Match harder = new Match("ELITE1", eliteRules, 73);
        bool containsElite = false;
        for (int i = 0; i < harder.Enemies.Count; i++)
        {
            for (int j = 0; j < harder.Enemies[i].Definition.Skills.Length; j++)
            {
                if (harder.Enemies[i].Definition.Skills[j].Tier > 1)
                {
                    containsElite = true;
                }
            }
        }
        Check(containsElite);
    }

    private static void HiddenSpawns()
    {
        Rules rules = new Rules();
        Fixture f = new Fixture(rules);
        // 생성 규칙만 확인하므로 피격으로 작전이 끝나지 않게 한다.
        f.Player.Hp = 1000;
        // 캠프의 대원 한 명만으로는 생성 타원의 왼쪽 일부만 보인다. 타원 위 세 곳에 관찰자를 세워
        // 타원 대부분이 분대 시야에 들어오게 한다. 시야 조건이 빠지면 대부분의 생성이 보이는 곳에서 일어난다.
        Vector2[] posts = new Vector2[] { new Vector2(1730, 710), new Vector2(755, 1178), new Vector2(755, 242) };
        for (int i = 0; i < posts.Length; i++)
        {
            Check(f.Match.Map.CanStand(posts[i], Rules.PlayerRadius), "관찰자 위치가 막혀 있습니다");
            Player observer = Ally(f, "post" + i);
            observer.Position = posts[i];
            observer.Hp = 1000;
        }
        HashSet<int> seen = new HashSet<int>();
        for (int i = 0; i < f.Match.Enemies.Count; i++)
        {
            seen.Add(f.Match.Enemies[i].Id);
        }
        int observed = 0;
        // 한 틱씩 진행해 적이 생성된 직후의 위치를 확인한다. 생성된 틱에 적이 한 번 움직이므로
        // 그만큼의 거리를 허용해 판단한다.
        float reach = (float)(EnemyDefinition.Grunt.Speed * Rules.Step) + 0.5f;
        for (int step = 0; step < 2700; step++)
        {
            Advance(f.Match, Rules.Step);
            for (int i = 0; i < f.Match.Enemies.Count; i++)
            {
                Enemy enemy = f.Match.Enemies[i];
                if (!seen.Add(enemy.Id))
                {
                    continue;
                }
                observed++;
                Check(!SurelyVisible(f.Match, enemy.Position, reach), "분대 시야 안에서 적이 생성되었습니다");
            }
        }
        Check(observed > 0, "추가 생성된 적이 없어 검사가 아무것도 확인하지 못했습니다");
    }

    // 생성된 틱에 적이 한 번 움직였으므로 생성 위치는 지금 위치에서 reach 안쪽 어딘가다.
    // 그 범위 어느 점에서 봐도 보일 때만 시야 안에서 생성된 것으로 본다. 시야 경계 바로 밖에서 생긴 적이
    // 한 걸음 들어와 보이는 정상적인 경우를 위반으로 잘못 판단하지 않기 위해서다.
    private static bool SurelyVisible(Match match, Vector2 point, float reach)
    {
        if (!match.Visible(point))
        {
            return false;
        }
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.Tau / 8;
            Vector2 around = point + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * reach;
            if (!match.Visible(around))
            {
                return false;
            }
        }
        return true;
    }

    private static void CornerSlip()
    {
        Fixture f = new Fixture();
        f.Player.Hp = 1000;
        // 벽 타일 (32,10)–(32,11)의 아래쪽 면은 y = 576이다. y = 588 직선은 몸 중심의 사선은 열려 있지만
        // 반지름 13인 몸은 모서리에 12만큼 걸린다.
        Vector2 start = new Vector2(1505, 588);
        f.Player.Position = new Vector2(1615, 588);
        Check(f.Match.Map.LineOfSight(start, f.Player.Position), "사선이 막혀 있어 검사 조건이 아닙니다");
        // id가 짝수인 적은 예전 방식에서 위(벽 쪽)로 비켜 가려다 계속 걸렸다. 행동 없이 이동만 본다.
        Enemy enemy = new Enemy(30020, start, EnemyDefinition.Grunt);
        enemy.NextActionTick = long.MaxValue;
        f.Match.Enemies.Add(enemy);
        Advance(f.Match, 2);
        Check(enemy.Position.X > 1560, "모서리에 걸려 멈췄습니다");
    }

    private static void FlowSource()
    {
        // 대원은 캠프에 있고 흐름장도 캠프 칸을 출발점(비용 0)으로 만들어졌다.
        Fixture f = new Fixture();
        // 출발점 칸 안에서는 더 나은 이웃이 없다. 이웃 칸(비용 1)으로 향하면 대원에게서 멀어진다.
        Vector2 inside = f.Match.FlowHeading(BattleMap.Camp);
        Check(inside.LengthSquared() < 0.001f, "출발점 칸 안에서 이웃 칸으로 향합니다");
        // 옆 칸에서는 출발점 칸 쪽을 가리킨다.
        Vector2 beside = f.Match.FlowHeading(BattleMap.Camp + new Vector2(BattleMap.Cell, 0));
        Check(beside.X < 0, "옆 칸에서 출발점 쪽을 가리키지 않습니다");
    }

    private static void WallHug()
    {
        Fixture f = new Fixture();
        f.Player.Hp = 1000;
        // 벽 타일 (32,10)–(32,11)의 왼쪽 면은 x = 1536이다. 반지름 13인 적이 면에 딱 붙어 아래로 내려간다.
        Vector2 start = new Vector2(1523, 470);
        f.Player.Position = new Vector2(1523, 650);
        Enemy enemy = new Enemy(30030, start, EnemyDefinition.Grunt);
        enemy.NextActionTick = long.MaxValue;
        f.Match.Enemies.Add(enemy);
        Advance(f.Match, 1);
        Check(enemy.Position.Y > start.Y + 60, "아래로 내려가지 않았습니다");
        // 몸이 지나갈 수 있는 직선이므로 옆으로 흔들리지 않아야 한다.
        Check(Math.Abs(enemy.Position.X - start.X) < 1, "벽에 붙은 적이 직진하지 않고 옆으로 흔들립니다");
    }

    private static void LongerWindup()
    {
        for (int i = 0; i < EnemyDefinition.Grunt.Skills.Length; i++)
        {
            EnemySkill skill = EnemyDefinition.Grunt.Skills[i];
            for (int setting = 0; setting < 2; setting++)
            {
                Rules rules = new Rules { MaxEnemies = 0 };
                if (setting == 1)
                {
                    rules = new Rules { MaxEnemies = 0, EnemyWindupMultiplier = 1 };
                }
                Fixture f = new Fixture(rules);
                EnemyDefinition singleSkill = new EnemyDefinition("grunt", "병사", 13, 1, 0,
                    new EnemySkill[] { skill });
                Enemy enemy = Target(f, singleSkill);
                enemy.NextActionTick = f.Match.Tick;
                Advance(f.Match, Rules.Step);
                Check(enemy.Action != null);
                EnemyAction action = enemy.Action!;
                double expected = skill.Windup * rules.EnemyWindupMultiplier;
                // 예고 길이는 틱으로 올려 센다. 배율 1의 0.85초 예고는 26틱(약 0.867초)이다.
                // 클라이언트에 보내는 예고도 실제 접촉과 같은 틱을 가리켜야 한다. 보내는 시각은 반올림되어 있으므로
                // 각각 30을 곱해 반올림해 틱으로 되돌린 뒤 비교한다.
                long windupTicks = Rules.ToTicks(expected);
                Equal(action.ContactTick - action.StartedTick, windupTicks);
                SnapshotMessage snapshot = Snapshot.CreateShared(f.Match);
                ActionSnapshot tell = snapshot.Enemies[0].Action!;
                long tellStart = (long)Math.Round(tell.StartedAt * Rules.TicksPerSecond);
                long tellContact = (long)Math.Round(tell.ContactAt * Rules.TicksPerSecond);
                Equal(tellContact - tellStart, windupTicks);
                Equal(tell.Phase, "telegraph");
                Advance(f.Match, expected - Rules.Step);
                Near(f.Player.Hp, f.Player.MaxHp);
                Advance(f.Match, Rules.Step * 2);
                Near(f.Player.Hp, f.Player.MaxHp - 1);
            }
        }
    }
}
