using System.Numerics;
using Argus.Server.Game;
using static TestTools;

internal static class EncounterTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("The first mission has only tier-one enemies, including late reinforcements", FirstMission));
        tests.Add(new TestCase("AI telegraphs and actual hits both wait for the longer mission windup", LongerWindup));
        tests.Add(new TestCase("Reinforcements never appear inside the squad's shared sight", HiddenSpawns));
        tests.Add(new TestCase("An enemy whose body clips a wall corner on an open sight line still reaches the player", CornerSlip));
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
        enemy.NextActionAt = double.MaxValue;
        f.Match.Enemies.Add(enemy);
        Advance(f.Match, 2);
        Check(enemy.Position.X > 1560, "모서리에 걸려 멈췄습니다");
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
                enemy.NextActionAt = f.Match.Now;
                Advance(f.Match, Rules.Step);
                Check(enemy.Action != null);
                EnemyAction action = enemy.Action!;
                double expected = skill.Windup * rules.EnemyWindupMultiplier;
                Near(action.ContactAt - action.StartedAt, expected);
                SnapshotMessage snapshot = Snapshot.Create(f.Match, f.Player);
                ActionSnapshot tell = snapshot.Enemies[0].Action!;
                Near(tell.ContactAt - tell.StartedAt, expected);
                Equal(tell.Phase, "telegraph");
                Advance(f.Match, expected - Rules.Step);
                Near(f.Player.Hp, f.Player.MaxHp);
                Advance(f.Match, Rules.Step * 2);
                Near(f.Player.Hp, f.Player.MaxHp - 1);
            }
        }
    }
}
