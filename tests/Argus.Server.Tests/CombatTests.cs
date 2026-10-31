using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

internal static class CombatTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("All nine ordinary RPS matchups have the agreed win/draw/loss results", OrdinaryMatrix));
        tests.Add(new TestCase("Enemy telegraphs already have an affinity; striking a strike is a draw", Telegraph));
        tests.Add(new TestCase("Idle enemies take one ordinary damage, without an automatic execution", Idle));
        tests.Add(new TestCase("Empty parry produces a non-RPS followup even against a channel", EmptyParry));
        tests.Add(new TestCase("Front guards block ordinary followup damage without an RPS result", BlockOrdinaryFollowup));
        tests.Add(new TestCase("Losing a parry cancels its followup and does not kill the opponent", InterruptedParry));
        tests.Add(new TestCase("Expired and rear-facing parries cannot defend", InvalidGuards));
        tests.Add(new TestCase("A guard half-angle of a full turn guards every side and a negative one guards none", GuardAngleLimits));
        tests.Add(new TestCase("A grab has a persistent window and one target per activation", SingleGrab));
        tests.Add(new TestCase("Friendly players can overlap and cannot damage each other", Friendly));
        tests.Add(new TestCase("All three tier-two actions can be broken by two overlapping responses", CooperativeMatrix));
        tests.Add(new TestCase("A complete break protects C irrespective of player iteration order", ProtectBystander));
        tests.Add(new TestCase("An expired response cannot be saved up toward a later tier-two attack", ExpiredContribution));
        tests.Add(new TestCase("Prepared independent effects from one owner also count toward the total", IndependentActions));
        tests.Add(new TestCase("Partial counters cost the player one and deal the skill's partial damage, which can finish an elite", PartialVariants));
        tests.Add(new TestCase("Every elite falls to solo partial counters against each of its tier-two actions", PartialsFinishElites));
        tests.Add(new TestCase("Grabs landing apart on one warden shield are separate partial judgments, each costing the warden one", StaggeredPartials));
        tests.Add(new TestCase("Tier-two draws deal two to each drawing player and one per draw to the elite, with one draw effect per judgment", EliteDraw));
        tests.Add(new TestCase("Reduced HP leaves skill tiers unchanged; a weak skill can still be fully broken", WeakPhase));
        tests.Add(new TestCase("Wall cover prevents melee hits between otherwise clear endpoints", Cover));
        tests.Add(new TestCase("The wall ray finds exact corners, edges, starting walls and both directions without rounding", RayWallEdges));
        tests.Add(new TestCase("Melee destroys walls and mission facilities through normal inputs", Structures));
        tests.Add(new TestCase("Every hit on a breakable wall shows a visible hit effect in front of the wall", WallHitEffect));
        tests.Add(new TestCase("Player-enemy and enemy-enemy bodies block movement while skills allow walking", Bodies));
        tests.Add(new TestCase("Catching up after a stall runs whole ticks, preserving short active windows and preventing body tunneling", LongStep));
        tests.Add(new TestCase("A new skill id with guard flags still guards and follows up like a parry", SkillFlags));
        tests.Add(new TestCase("Skills around the player hit, guard and grab in every direction without aiming", AroundSkills));
        tests.Add(new TestCase("A flank outside an enemy action's area deals ordinary damage without its affinity", Flank));
        tests.Add(new TestCase("A player the shield shove reaches answers it with affinity even beside the shield", ShoveReach));
        tests.Add(new TestCase("A kill goes to the player whose draw or partial damage finished the elite", DrawCredit));
        tests.Add(new TestCase("A grab takes the nearest enemy at contact, including one that came closer during windup", GrabRetarget));
        tests.Add(new TestCase("A grab picks its target on the contact tick itself and forgets one that left its reach", GrabContactTarget));
    }

    private static void OrdinaryMatrix()
    {
        string[] skills = new string[] { "strike", "parry", "grab" };
        string[] kinds = new string[] { "strike", "block", "channel" };
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                Fixture f = new Fixture();
                Enemy enemy = Threat(f, kinds[j]);
                PlayerAction action = Prepare(f, f.Player, skills[i]);
                Advance(f.Match, 0.08);
                if (Affinity.Beats(kinds[i], kinds[j]))
                {
                    Near(enemy.Hp, 0);
                    Near(f.Player.Hp, 6);
                }
                else if (i == j)
                {
                    Near(enemy.Hp, 0);
                    Near(f.Player.Hp, 5);
                }
                else
                {
                    Near(enemy.Hp, 1);
                    Near(f.Player.Hp, 5);
                    Check(action.Cancelled);
                }
            }
        }
    }

    private static void Telegraph()
    {
        Fixture f = new Fixture();
        Enemy enemy = Threat(f, "strike", arrivesIn: 2);
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.2);
        Near(enemy.Hp, 0);
        Near(f.Player.Hp, 5);
    }

    private static void Idle()
    {
        Fixture f = new Fixture();
        Enemy enemy = Target(f, EnemyDefinition.Breaker);
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.7);
        Near(enemy.Hp, 1);
        Near(f.Player.Hp, 6);
        Near(enemy.Definition.Skills[0].Tier, 2);
    }

    private static void EmptyParry()
    {
        Fixture f = new Fixture();
        Enemy enemy = Threat(f, "channel", 2, arrivesIn: 2);
        Prepare(f, f.Player, "parry");
        Advance(f.Match, 0.5);
        Near(enemy.Hp, 2);
        Advance(f.Match, 0.3);
        Near(enemy.Hp, 1);
        Near(f.Player.Hp, 6);
        Check(enemy.Action != null);
        Check(enemy.Action!.IsActive(f.Match.Tick));
    }

    private static void InterruptedParry()
    {
        Fixture f = new Fixture();
        Enemy enemy = Threat(f, "channel");
        PlayerAction action = Prepare(f, f.Player, "parry");
        Advance(f.Match, 0.8);
        Near(f.Player.Hp, 5);
        Near(enemy.Hp, 1);
        Check(action.Cancelled);
    }

    private static void BlockOrdinaryFollowup()
    {
        Fixture front = new Fixture();
        Enemy guarding = Threat(front, "block", 2, arrivesIn: 2);
        PlayerAction parry = Prepare(front, front.Player, "parry");
        Advance(front.Match, 0.6);
        Near(guarding.Hp, 2);
        Near(front.Player.Hp, 6);
        Check(!parry.Cancelled);
        // 막힌 반격 한 번이 방어 종료 뒤 같은 동작에서 다시 피해를 주지 않는다.
        guarding.Action = null;
        Advance(front.Match, 0.1);
        Near(guarding.Hp, 2);

        Fixture rear = new Fixture();
        Enemy facingAway = Threat(rear, "block", 2, arrivesIn: 2);
        facingAway.Action = new EnemyAction(999, facingAway.Action!.Skill, Angle.Zero, rear.Match.Tick);
        Prepare(rear, rear.Player, "parry");
        Advance(rear.Match, 0.8);
        Near(facingAway.Hp, 1);
        Near(rear.Player.Hp, 6);
    }

    private static void GuardAngleLimits()
    {
        // 방어 반각은 방향이 아니라 각도의 크기라 한 바퀴로 감지 않는다(Angle.SpanUnits).
        // 감으면 2π가 0이 되어 정면만 막고, 음수는 거의 한 바퀴가 되어 모든 방향을 막는다.
        Fixture behind = new Fixture(new Rules { MaxEnemies = 0, GuardHalfAngle = Math.Tau });
        Enemy striker = Threat(behind, "strike");
        Prepare(behind, behind.Player, "parry");
        // 등을 돌려도 모든 방향을 막는 방어는 받아친다.
        behind.Player.Aim = Angle.FromRadians(Math.PI);
        Advance(behind.Match, 0.1);
        Near(behind.Player.Hp, 6);
        Check(striker.Hp <= 0, "모든 방향을 막는 방어가 뒤에서 온 타격을 받아치지 못했습니다");

        // 정확히 등 뒤에서 와도 막는다. 방향 벡터는 1/4096 단위로 반올림해 길이가 1보다 조금 길 수 있다(조준 4칸이 그런 예다).
        // 그러면 코사인 -1과 내적으로만 비교할 때 바로 뒤쪽을 놓친다. 조준 π는 방향이 (-1, 0)이라 길이가 정확히 1이어서 이 경우를 보지 못한다.
        Fixture exact = new Fixture(new Rules { MaxEnemies = 0, GuardHalfAngle = Math.Tau });
        exact.Player.Aim = Angle.FromUnits(4);
        FixedVector rounded = exact.Player.Aim.Direction();
        Check(rounded.X.Raw * rounded.X.Raw + rounded.Y.Raw * rounded.Y.Raw > Fixed.Scale * Fixed.Scale, "조준 4칸의 방향 벡터가 1보다 길지 않습니다");
        Enemy behindExactly = Threat(exact, "strike");
        behindExactly.Position = exact.Player.Position - exact.Player.AimDirection * 50;
        // 적은 대원을 향해 친다. 대원의 조준과 같은 방향이다.
        behindExactly.Action = new EnemyAction(behindExactly.Action!.Id, behindExactly.Action.Skill, exact.Player.Aim, exact.Match.Tick);
        Prepare(exact, exact.Player, "parry");
        Advance(exact.Match, 0.1);
        Near(exact.Player.Hp, 6);
        Check(behindExactly.Hp <= 0, "모든 방향을 막는 방어가 정확히 등 뒤에서 온 타격을 받아치지 못했습니다");

        Fixture front = new Fixture(new Rules { MaxEnemies = 0, GuardHalfAngle = -1 });
        Enemy facing = Threat(front, "strike");
        Prepare(front, front.Player, "parry");
        // 정면을 봐도 반각이 음수면 막지 못한다.
        Advance(front.Match, 0.1);
        Near(front.Player.Hp, 5);
        Near(facing.Hp, 1);
    }

    private static void InvalidGuards()
    {
        Fixture rear = new Fixture();
        Enemy enemy = Threat(rear, "strike");
        Prepare(rear, rear.Player, "parry");
        rear.Player.Aim = Angle.FromRadians(Math.PI);
        Advance(rear.Match, 0.1);
        Near(rear.Player.Hp, 5);
        Near(enemy.Hp, 1);
        Fixture expired = new Fixture();
        Enemy second = Threat(expired, "strike", arrivesIn: 0.56);
        Prepare(expired, expired.Player, "parry");
        // 반격 사거리 밖에서 적의 공격만 닿게 하여, 종료된 방어 창을 검사한다.
        second.Position = expired.Player.Position + FixedVector.FromInt(80, 0);
        Advance(expired.Match, 0.6);
        Near(expired.Player.Hp, 5);
        Near(second.Hp, 1);
    }

    private static void SingleGrab()
    {
        Fixture f = new Fixture();
        Enemy first = Target(f);
        Enemy second = Target(f);
        first.Position += FixedVector.FromInt(0, 14);
        second.Position -= FixedVector.FromInt(0, 14);
        PlayerAction grab = Prepare(f, f.Player, "grab");
        Advance(f.Match, 0.6);
        Near(first.Hp + second.Hp, 1);
        Check(grab.Target.HasValue);
        Fixture later = new Fixture();
        Enemy approaching = Target(later);
        approaching.Position += FixedVector.FromInt(200, 0);
        Prepare(later, later.Player, "grab");
        Advance(later.Match, 0.3);
        approaching.Position = later.Player.Position + FixedVector.FromInt(40, 0);
        Advance(later.Match, 0.1);
        Near(approaching.Hp, 0);
    }

    private static void Friendly()
    {
        Fixture f = new Fixture();
        Player ally = Ally(f, "ally");
        ally.Position += FixedVector.FromInt(30, 0);
        f.Player.Mana = 12;
        for (int i = 0; i < 80; i++)
        {
            f.Match.Input(f.Player, i, 1, 0, 0, 1);
            f.Match.Step();
        }
        Check(f.Player.Position.X > ally.Position.X);
        Near(ally.Hp, ally.MaxHp);
        Near(f.Player.Hp, f.Player.MaxHp);
    }

    private static void CooperativeMatrix()
    {
        string[] kinds = new string[] { "strike", "block", "channel" };
        string[] counters = new string[] { "parry", "grab", "strike" };
        for (int i = 0; i < 3; i++)
        {
            Fixture f = new Fixture();
            Player ally = Ally(f, "ally");
            Enemy enemy = Threat(f, kinds[i], 2);
            Prepare(f, f.Player, counters[i]);
            Prepare(f, ally, counters[i]);
            Advance(f.Match, 0.08);
            Near(enemy.Hp, 0);
            Near(f.Player.Hp, 6);
            Near(ally.Hp, 6);
        }
    }

    private static void ProtectBystander()
    {
        for (int reversed = 0; reversed < 2; reversed++)
        {
            Fixture f = new Fixture();
            Player ally = Ally(f, "ally");
            Player bystander = Ally(f, "bystander");
            Enemy enemy = Threat(f, "strike", 2);
            Prepare(f, f.Player, "parry");
            Prepare(f, ally, "parry");
            if (reversed == 1)
            {
                f.Match.Players.Reverse();
            }
            Advance(f.Match, 0.1);
            Near(enemy.Hp, 0);
            Near(bystander.Hp, 6);
            Near(ally.Hp, 6);
            Near(f.Player.Hp, 6);
        }
    }

    private static void ExpiredContribution()
    {
        Fixture f = new Fixture();
        Player expired = Ally(f, "expired");
        Enemy enemy = Threat(f, "strike", 2);
        enemy.Position = f.Player.Position + FixedVector.FromInt(80, 0);
        Prepare(f, f.Player, "parry");
        Prepare(f, expired, "parry", 0.55);
        Advance(f.Match, 0.1);
        Near(enemy.Hp, 2);
        Near(f.Player.Hp, 5);
        Near(expired.Hp, 4);
    }

    private static void IndependentActions()
    {
        Fixture f = new Fixture();
        Enemy enemy = Threat(f, "strike", 2);
        // 현재 기본기는 겹쳐 시전할 수 없다. 합산 자체는 소유자 수가 아니라 유효한 기술 수를 센다.
        Prepare(f, f.Player, "parry");
        Prepare(f, f.Player, "parry");
        Advance(f.Match, 0.1);
        Near(enemy.Hp, 0);
        Near(f.Player.Hp, 6);
    }

    private static void PartialVariants()
    {
        // 2티어 행동에 맞는 대응이 하나뿐이면(부분 대응) 대원은 피해 1을 받고 기술이 중단된다. 적은 기술별 부분 대응 피해를 받는다.
        for (int partial = 0; partial <= 1; partial++)
        {
            Fixture f = new Fixture();
            Enemy enemy = Threat(f, "strike", 2, partial);
            PlayerAction parry = Prepare(f, f.Player, "parry");
            Advance(f.Match, 0.8);
            Near(enemy.Hp, 2 - partial);
            Near(f.Player.Hp, 5);
            Check(parry.Cancelled);
            // 체력 1인 적에게 다시 부분 대응한다. 부분 대응 피해가 있으면 그 피해로 쓰러지고, 처치는 부분 대응한 대원이 가져간다.
            enemy.Hp = 1;
            enemy.Action = new EnemyAction(999, new EnemySkill("strike", 2, partial, 0.05, 85, 25), Angle.FromRadians(Math.PI), f.Match.Tick);
            Prepare(f, f.Player, "parry");
            Advance(f.Match, 0.1);
            Near(enemy.Hp, 1 - partial);
            Equal(f.Player.Kills, partial);
        }
        Fixture none = new Fixture();
        Enemy uncountered = Threat(none, "strike", 2);
        Advance(none.Match, 0.5);
        Near(none.Player.Hp, 4);
        Near(uncountered.Hp, 2);
    }

    private static void PartialsFinishElites()
    {
        // 엘리트의 2티어 행동마다, 혼자 맞는 대응을 맞히면(부분 대응) 대원과 엘리트가 함께 피해 1을 받는다.
        // 그래서 체력만큼 부분 대응하면 엘리트가 쓰러진다. 2026-10-28 전에는 부분 대응으로 쓰러지지 않았다.
        // 분쇄자는 피해가 없었고, 철위병은 체력 1에서 멈췄다.
        // 받아치기(타격 Ⅱ)는 닿는 공격을 막을 때, 잡기(방어 Ⅱ)는 예고 중에 먼저 닿을 때 판정받으므로 행동마다 따로 본다.
        int checkedSkills = 0;
        for (int i = 0; i < EnemyDefinition.All.Count; i++)
        {
            EnemyDefinition definition = EnemyDefinition.All[i];
            for (int j = 0; j < definition.Skills.Length; j++)
            {
                EnemySkill skill = definition.Skills[j];
                if (skill.Tier != 2)
                {
                    continue;
                }
                checkedSkills++;
                Fixture f = new Fixture();
                string counter = CounterFor(f.Match.Rules, skill.Kind);
                Enemy elite = Target(f, definition);
                for (int round = 1; round <= definition.Health; round++)
                {
                    f.Player.Hp = f.Player.MaxHp;
                    BeginAction(f, elite, skill, 0.2);
                    Prepare(f, f.Player, counter);
                    Advance(f.Match, 0.3);
                    Near(f.Player.Hp, f.Player.MaxHp - 1);
                    Near(elite.Hp, definition.Health - round);
                }
                Equal(f.Player.Kills, 1);
                Check(!f.Match.Enemies.Contains(elite), $"{definition.Name} 처치 뒤에도 적 목록에 남아 있습니다({skill.Kind})");
            }
        }
        Check(checkedSkills > 0, "2티어 행동이 있는 적 종류가 없습니다");
    }

    private static void StaggeredPartials()
    {
        // 철위병의 방어 Ⅱ는 예고 1.2초와 밀어치기 0.25초 동안 상성이 있다. 그동안 잡기가 따로 닿으면 판정도 따로 난다.
        // 맞는 대응이 따로 닿을 때마다 부분 대응이고, 철위병도 판정마다 부분 대응 피해를 받는다(2026-10-31 결정).
        EnemySkill shield = EnemyDefinition.Warden.Skills[1];
        Equal(shield.Kind, "block");
        Equal(shield.Tier, 2);

        // 0.1초 차이로 잡는다. 앞 잡기가 닿을 때(0.12초) 뒤 잡기가 이미 시작해 있어 한 판정에 함께 들어간다.
        // 맞는 대응이 둘이라 완전 파훼다. 아무도 다치지 않고 먼저 시작한 대원이 처치를 가져간다.
        Fixture together = new Fixture();
        Player close = Ally(together, "close");
        Enemy first = Threat(together, EnemyDefinition.Warden, shield, shield.Windup);
        Prepare(together, together.Player, "grab");
        Advance(together.Match, 0.1);
        Prepare(together, close, "grab");
        Advance(together.Match, 0.1);
        Near(first.Hp, 0);
        Near(together.Player.Hp, together.Player.MaxHp);
        Near(close.Hp, close.MaxHp);
        Equal(together.Player.Kills, 1);
        Equal(close.Kills, 0);

        // 0.5초 차이로 잡는다. 앞 잡기가 혼자 판정받은 뒤 뒤 잡기가 따로 판정받아, 두 번 모두 부분 대응이다.
        Fixture apart = new Fixture();
        Player late = Ally(apart, "late");
        Enemy second = Threat(apart, EnemyDefinition.Warden, shield, shield.Windup);
        Prepare(apart, apart.Player, "grab");
        Advance(apart.Match, 0.5);
        Near(apart.Player.Hp, apart.Player.MaxHp - 1);
        Near(second.Hp, 1);
        Prepare(apart, late, "grab");
        Advance(apart.Match, 0.2);
        Near(late.Hp, late.MaxHp - 1);
        Near(second.Hp, 0);
        Equal(apart.Player.Kills, 0);
        Equal(late.Kills, 1);

        // 혼자 두 번 잡는다. 첫 잡기가 부분 대응으로 중단된 뒤 같은 예고 안에 다시 잡아, 대원은 두 번 다 피해 1을 받는다.
        Fixture alone = new Fixture();
        Enemy third = Threat(alone, EnemyDefinition.Warden, shield, shield.Windup);
        Prepare(alone, alone.Player, "grab");
        Advance(alone.Match, 0.5);
        Near(alone.Player.Hp, alone.Player.MaxHp - 1);
        Near(third.Hp, 1);
        Prepare(alone, alone.Player, "grab");
        Advance(alone.Match, 0.2);
        Near(alone.Player.Hp, alone.Player.MaxHp - 2);
        Near(third.Hp, 0);
        Equal(alone.Player.Kills, 1);
    }

    // 남아 있는 효과 가운데 kind 종류의 수다. 판정 하나에 같은 효과가 겹쳐 생기지 않았는지 볼 때 쓴다.
    private static int CountEffects(Match match, string kind)
    {
        int count = 0;
        for (int i = 0; i < match.Effects.Count; i++)
        {
            if (match.Effects[i].Kind == kind)
            {
                count++;
            }
        }
        return count;
    }

    // 적 행동의 종류를 이기는 대원 기술의 id다.
    private static string CounterFor(Rules rules, string attackKind)
    {
        string[] ids = new string[] { "strike", "parry", "grab" };
        for (int i = 0; i < ids.Length; i++)
        {
            if (Affinity.Beats(rules.Skill(ids[i]).Kind, attackKind))
            {
                return ids[i];
            }
        }
        throw new Exception($"이기는 기술이 없는 적 행동 종류입니다: {attackKind}");
    }

    private static void EliteDraw()
    {
        Fixture f = new Fixture();
        Enemy enemy = Threat(f, "strike", 2);
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.6);
        Near(f.Player.Hp, 4);
        Near(enemy.Hp, 1);

        // 두 대원이 한 판정에 함께 비기면 엘리트 피해는 비긴 대원 수만큼 쌓이고(임시 처리), 먼저 시작한 대원이 처치를 가져간다.
        // 비김 효과는 판정 하나에 하나다. 효과는 적 자리에 그려지므로 대원마다 만들면 같은 모양이 겹쳐 그려진다.
        Fixture pair = new Fixture();
        Player partner = Ally(pair, "partner");
        Enemy shared = Threat(pair, "strike", 2);
        Prepare(pair, pair.Player, "strike");
        Prepare(pair, partner, "strike");
        Advance(pair.Match, 0.1);
        Near(pair.Player.Hp, 4);
        Near(partner.Hp, 4);
        Near(shared.Hp, 0);
        Equal(pair.Player.Kills, 1);
        Equal(partner.Kills, 0);
        Equal(CountEffects(pair.Match, "draw"), 1);
    }

    private static void WeakPhase()
    {
        Fixture f = new Fixture();
        Enemy elite = Target(f, EnemyDefinition.Warden);
        elite.Action = new EnemyAction(999, elite.Definition.Skills[2], Angle.FromRadians(Math.PI), f.Match.Tick);
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.2);
        Near(elite.Hp, 0);
        Near(f.Player.Hp, 6);
        Equal(elite.Definition.Skills[0].Tier, 2);
        Equal(elite.Definition.Skills[1].Tier, 2);
    }

    private static void RayWallEdges()
    {
        // 장애물 타일 (10, 10)은 x 480~528, y 480~528이다. 경계를 지나는 순서를 나누지 않고 엇갈려 곱해 비교하므로
        // 꼭짓점이나 경계 위를 지나는 선분도 반올림 없이 판정한다. 비율은 1/4096 단위로 반올림한 값이다.
        BattleMap map = new BattleMap(MissionLayout.CreateHandmade());
        int tile = 10 * map.Columns + 10;
        bool layout = map.Solid(10, 10) && !map.Solid(9, 10) && !map.Solid(10, 9);
        Check(layout, "검사 조건(타일 배치)이 바뀌었습니다");

        // 꼭짓점 (480, 480)을 정확히 지나는 대각선이다. 양옆 타일이 비어 있으므로 대각선 너머의 벽 타일에 닿는다. 비율은 80 / 200 = 0.4다.
        FixedVector cornerFrom = FixedVector.FromInt(400, 400);
        FixedVector cornerTo = FixedVector.FromInt(600, 600);
        WallHit corner = map.RayWall(cornerFrom, cornerTo);
        Check(corner.Hit, "꼭짓점을 지나는 선분이 벽을 지나쳤습니다");
        Equal(corner.Tile, tile);
        Equal(corner.Fraction.Raw, 1638L);

        // 타일 윗면(y = 480) 위를 따라가는 선분은 그 줄(y 480~528)에 있으므로 벽에 닿는다. Raw 하나 위는 윗줄이라 닿지 않는다.
        FixedVector edgeFrom = FixedVector.FromInt(400, 480);
        FixedVector edgeTo = FixedVector.FromInt(600, 480);
        WallHit along = map.RayWall(edgeFrom, edgeTo);
        Check(along.Hit, "경계 위를 지나는 선분이 벽을 지나쳤습니다");
        Equal(along.Tile, tile);
        Equal(along.Fraction.Raw, 1638L);
        Fixed above = Fixed.FromRaw(480 * Fixed.Scale - 1);
        FixedVector aboveFrom = new FixedVector(Fixed.FromInt(400), above);
        FixedVector aboveTo = new FixedVector(Fixed.FromInt(600), above);
        bool aboveClear = map.LineOfSight(aboveFrom, aboveTo);
        Check(aboveClear, "경계 바로 위의 선분이 막혔습니다");

        // 반대 방향(오른쪽에서 왼쪽)으로는 오른쪽 면 x = 528에서 닿는다. 비율은 72 / 200 = 0.36(1474.56칸)이라 1475다.
        FixedVector backFrom = FixedVector.FromInt(600, 500);
        FixedVector backTo = FixedVector.FromInt(400, 500);
        WallHit back = map.RayWall(backFrom, backTo);
        Check(back.Hit, "반대 방향 선분이 벽을 지나쳤습니다");
        Equal(back.Tile, tile);
        Equal(back.Fraction.Raw, 1475L);

        // 벽 안에서 시작하면 비율 0에서 닿는다.
        FixedVector insideFrom = FixedVector.FromInt(500, 500);
        FixedVector insideTo = FixedVector.FromInt(700, 500);
        WallHit inside = map.RayWall(insideFrom, insideTo);
        Check(inside.Hit, "벽 안에서 시작한 선분이 닿지 않았습니다");
        Equal(inside.Tile, tile);
        Equal(inside.Fraction, Fixed.Zero);

        // 벽 면(x = 480) 바로 앞에서 끝나면 닿지 않고, 벽 면에서 끝나면 끝점(비율 1)에서 닿는다.
        FixedVector approach = FixedVector.FromInt(400, 500);
        FixedVector beforeFace = FixedVector.FromInt(479, 500);
        FixedVector onFace = FixedVector.FromInt(480, 500);
        WallHit stopsShort = map.RayWall(approach, beforeFace);
        Check(!stopsShort.Hit, "벽 앞에서 끝난 선분이 닿았습니다");
        WallHit reachesFace = map.RayWall(approach, onFace);
        Check(reachesFace.Hit, "벽 면에서 끝난 선분이 닿지 않았습니다");
        Equal(reachesFace.Tile, tile);
        Equal(reachesFace.Fraction, Fixed.One);
    }

    private static void Cover()
    {
        Fixture f = new Fixture();
        f.Player.Position = FixedVector.FromInt(464, 498);
        f.Player.Aim = Angle.FromRadians(313 * Math.PI / 180);
        Enemy enemy = Target(f);
        enemy.Position = FixedVector.FromInt(495, 465);
        Check(f.Match.Map.CanStand(f.Player.Position, Rules.PlayerRadius));
        Check(f.Match.Map.CanStand(enemy.Position, enemy.Definition.Radius));
        Check(!f.Match.Map.LineOfSight(f.Player.Position, enemy.Position));
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.3);
        Near(enemy.Hp, 1);
    }

    private static void Structures()
    {
        Fixture f = new Fixture();
        f.Player.Position = FixedVector.FromInt(680, 500);
        f.Player.Mana = 12;
        for (int i = 0; i < 70; i++)
        {
            f.Match.Input(f.Player, i, 0, 0, 0, 1);
            f.Match.Step();
        }
        Check(!f.Match.Map.Solid(15, 10));
        Check(!f.Match.Map.Damage(0, 100));
        f.Player.Position = f.Match.Facilities[0].Position - FixedVector.FromInt(55, 0);
        for (int i = 70; i < 170; i++)
        {
            f.Match.Input(f.Player, i, 0, 0, 0, 1);
            f.Match.Step();
        }
        Near(f.Match.Facilities[0].Hp, 0);
    }

    private static void WallHitEffect()
    {
        // 오른쪽 벽(왼쪽 면 x = 720)을 마주 보고 한 번 친다. 부술 수 있는 벽은 체력이 3이라 한 번에 부서지지 않는다.
        Fixture f = new Fixture();
        f.Player.Position = FixedVector.FromInt(680, 500);
        f.Player.Aim = Angle.Zero;
        f.Player.Mana = 1;
        f.Match.Input(f.Player, 1, 0, 0, 0, 1);
        Advance(f.Match, 0.3);
        Check(f.Match.Map.Solid(15, 10), "한 번에 벽이 부서졌습니다");
        // 시설을 칠 때처럼 벽을 칠 때도 효과가 보여야 한다. 스냅샷에는 시야 안의 효과만 담긴다.
        SnapshotMessage shared = Snapshot.CreateShared(f.Match);
        int visible = 0;
        for (int i = 0; i < shared.Effects.Count; i++)
        {
            EffectSnapshot effect = shared.Effects[i];
            if (effect.Kind == "hit" && effect.X > 680 && effect.X < 720)
            {
                visible++;
            }
        }
        Equal(visible, 1);
    }

    private static void Bodies()
    {
        Fixture f = new Fixture();
        Enemy enemy = Target(f);
        f.Player.Mana = 5;
        for (int i = 0; i < 20; i++)
        {
            f.Match.Input(f.Player, i, 1, 0, Math.PI, 2);
            f.Match.Step();
        }
        Check(f.Player.Position.X > Fixed.FromInt(300));
        Check(f.Player.Position.X <= enemy.Position.X - enemy.Definition.Radius - Rules.PlayerRadius + Fixed.FromRaw(4));
        Check(f.Player.Mana < 5);
        Fixture moving = new Fixture();
        Enemy lead = new Enemy(1, FixedVector.FromInt(390, 730), EnemyDefinition.Grunt);
        Enemy following = new Enemy(2, FixedVector.FromInt(420, 730), EnemyDefinition.Grunt);
        moving.Match.Enemies.Add(lead);
        moving.Match.Enemies.Add(following);
        for (int i = 0; i < 90; i++)
        {
            moving.Match.Step();
            Check(Distance(lead.Position, following.Position) >= 26 - 0.001);
            Check(Distance(lead.Position, moving.Player.Position) >= 26 - 0.001);
        }
    }

    // 같은 수치와 속성에 id만 바꾼 새 기술을 만든다.
    private static SkillDefinition Renamed(SkillDefinition skill, string id)
    {
        return new SkillDefinition(id, skill.Kind, skill.Mana, skill.ContactAfter, skill.ActiveSeconds,
            skill.Duration, skill.Range, skill.HalfWidth, skill.GuardsFront, skill.HasFollowup,
            skill.SingleTarget, skill.BreaksStructures, skill.FollowupBreaksStructures, skill.Around);
    }

    private static PlayerAction Use(Fixture f, SkillDefinition skill, int id)
    {
        PlayerAction action = new PlayerAction(id, skill, 1, f.Match.Tick);
        f.Player.Actions.Add(action);
        f.Player.BusyUntilTick = action.EndsTick;
        return action;
    }

    private static void SkillFlags()
    {
        // 판정이 id 문자열에 묶여 있으면, id만 다른 새 기술은 아래 동작을 조용히 잃는다.
        Rules rules = new Rules();
        SkillDefinition shield = Renamed(rules.Parry, "shield");
        SkillDefinition hook = Renamed(rules.Grab, "hook");
        SkillDefinition smash = Renamed(rules.Strike, "smash");

        // 전방 방어: 조준에서 40도 벗어난 곳의 타격도 막는다. 먼저 치는 기술의 좁은 범위로는 닿지 않는 곳이다.
        Fixture guarded = new Fixture();
        Enemy striker = Target(guarded);
        double side = 40 * Math.PI / 180;
        striker.Position = guarded.Player.Position + Angle.FromRadians(side).Direction() * 50;
        striker.Action = new EnemyAction(30010, new EnemySkill("strike", 1, 0, 0.05, 85, 25), Angle.FromRadians(side + Math.PI), guarded.Match.Tick);
        Use(guarded, shield, 30011);
        Advance(guarded.Match, 0.08);
        Near(striker.Hp, 0);
        Near(guarded.Player.Hp, 6);

        // 방어 창 안에서는 무행동인 적을 치지 않고, 창이 끝나면 일반 반격이 나간다.
        Fixture countered = new Fixture();
        Enemy idle = Target(countered);
        Use(countered, shield, 30012);
        Advance(countered.Match, 0.3);
        Near(idle.Hp, 1);
        Advance(countered.Match, shield.ActiveSeconds - 0.3 + 0.1);
        Near(idle.Hp, 0);

        // 단일 대상: 범위 안에 둘이 있어도 가까운 한 명만 판정한다.
        Fixture held = new Fixture();
        Enemy near = Target(held);
        Enemy far = Target(held);
        near.Position = held.Player.Position + FixedVector.FromInt(40, 0);
        far.Position = held.Player.Position + FixedVector.FromInt(58, 0);
        Use(held, hook, 30013);
        Advance(held.Match, 0.2);
        Near(near.Hp, 0);
        Near(far.Hp, 1);

        // 구조물 파괴: 접촉 중에 시설을 부순다.
        Fixture breaker = new Fixture();
        Facility facility = breaker.Match.Facilities[0];
        breaker.Player.Position = facility.Position - FixedVector.FromInt(40, 0);
        Use(breaker, smash, 30014);
        Advance(breaker.Match, 0.25);
        Near(facility.Hp, facility.MaxHp - 1);

        // 반격의 구조물 파괴도 속성으로 정한다. 받아치기를 본뜬 기술의 반격은 시설을 부순다.
        Fixture countering = new Fixture();
        Facility struck = countering.Match.Facilities[0];
        countering.Player.Position = struck.Position - FixedVector.FromInt(40, 0);
        Use(countering, shield, 30015);
        Advance(countering.Match, shield.ActiveSeconds + 0.1);
        Near(struck.Hp, struck.MaxHp - 1);

        // 반격은 있지만 구조물은 부수지 않는 기술은 시설을 그대로 둔다.
        SkillDefinition deflect = new SkillDefinition("deflect", rules.Parry.Kind, rules.Parry.Mana, rules.Parry.ContactAfter,
            rules.Parry.ActiveSeconds, rules.Parry.Duration, rules.Parry.Range, rules.Parry.HalfWidth,
            true, true, false, false, false, false);
        Fixture deflecting = new Fixture();
        Facility kept = deflecting.Match.Facilities[0];
        deflecting.Player.Position = kept.Position - FixedVector.FromInt(40, 0);
        Use(deflecting, deflect, 30016);
        Advance(deflecting.Match, deflect.ActiveSeconds + 0.1);
        Near(kept.Hp, kept.MaxHp);
    }

    private static void AroundSkills()
    {
        // 둘레 기술은 조준하지 않는다. 판정이 id에 묶이지 않았는지 보려고 id만 바꾼 정의를 쓴다.
        Rules rules = new Rules();
        SkillDefinition whirl = Renamed(rules.Spin, "whirl");
        SkillDefinition bulwark = Renamed(rules.Ward, "bulwark");
        SkillDefinition lasso = Renamed(rules.Snatch, "lasso");

        // 회전 베기: 둘레 네 방향의 무행동 적을 한 번에 모두 친다. 반지름(48)에 적의 몸(13)을 더한 거리 밖의 적은 맞지 않는다.
        Fixture spun = new Fixture();
        Enemy[] ring = new Enemy[4];
        for (int i = 0; i < ring.Length; i++)
        {
            ring[i] = Target(spun);
            ring[i].Position = spun.Player.Position + Angle.FromFraction(i, ring.Length).Direction() * 50;
        }
        Enemy beyond = Target(spun);
        beyond.Position = spun.Player.Position + Angle.FromFraction(1, 8).Direction() * 70;
        Prepare(spun, spun.Player, whirl);
        Advance(spun.Match, 0.3);
        for (int i = 0; i < ring.Length; i++)
        {
            Near(ring[i].Hp, 0);
        }
        Near(beyond.Hp, 1);

        // 둘레의 원도 벽 너머에는 닿지 않는다. Cover와 같은 자리라 원 안이지만 두 몸 중심 사이의 사선이 벽 모서리에 가린다.
        Fixture covered = new Fixture();
        covered.Player.Position = FixedVector.FromInt(464, 498);
        Enemy hidden = Target(covered);
        hidden.Position = FixedVector.FromInt(495, 465);
        Check(Distance(covered.Player.Position, hidden.Position) < 48 + 13, "적이 원 밖에 있습니다");
        Check(!covered.Match.Map.LineOfSight(covered.Player.Position, hidden.Position));
        Prepare(covered, covered.Player, whirl);
        Advance(covered.Match, 0.3);
        Near(hidden.Hp, 1);

        // 등 뒤에서 오는 타격: 적은 대원의 오른쪽에서 대원 쪽으로 치고, 대원은 왼쪽을 본다.
        // 받아치기는 앞(120°)만 막으므로 대응하지 못하고 맞는다. 원형 방어는 막아 이긴다.
        Fixture facingAway = new Fixture();
        Enemy unblocked = Threat(facingAway, "strike");
        facingAway.Player.Aim = Angle.FromRadians(Math.PI);
        Prepare(facingAway, facingAway.Player, rules.Parry);
        Advance(facingAway.Match, 0.1);
        Near(unblocked.Hp, 1);
        Near(facingAway.Player.Hp, facingAway.Player.MaxHp - 1);

        Fixture warded = new Fixture();
        Enemy blocked = Threat(warded, "strike");
        warded.Player.Aim = Angle.FromRadians(Math.PI);
        Prepare(warded, warded.Player, bulwark);
        Advance(warded.Match, 0.1);
        Near(blocked.Hp, 0);
        Near(warded.Player.Hp, warded.Player.MaxHp);

        // 원형 방어에는 반격이 없다. 방어 창이 끝나도 앞의 무행동 적을 치지 않는다. 받아치기는 반격으로 친다(SkillFlags).
        Fixture quiet = new Fixture();
        Enemy idle = Target(quiet);
        Prepare(quiet, quiet.Player, bulwark);
        Advance(quiet.Match, bulwark.Duration + 0.1);
        Near(idle.Hp, 1);

        // 낚아채기는 둘레에서 가장 가까운 적을 잡는다. 등 뒤의 적이 더 가까우면 그 적이다. 앞만 보는 잡기는 앞의 적을 잡는다.
        Fixture snatched = new Fixture();
        Enemy behind = Target(snatched);
        behind.Position = snatched.Player.Position + FixedVector.FromInt(-35, 0);
        Enemy ahead = Target(snatched);
        ahead.Position = snatched.Player.Position + FixedVector.FromInt(45, 0);
        Prepare(snatched, snatched.Player, lasso);
        Advance(snatched.Match, 0.2);
        Near(behind.Hp, 0);
        Near(ahead.Hp, 1);

        Fixture grabbed = new Fixture();
        Enemy rear = Target(grabbed);
        rear.Position = grabbed.Player.Position + FixedVector.FromInt(-35, 0);
        Enemy front = Target(grabbed);
        front.Position = grabbed.Player.Position + FixedVector.FromInt(45, 0);
        Prepare(grabbed, grabbed.Player, rules.Grab);
        Advance(grabbed.Match, 0.2);
        Near(rear.Hp, 1);
        Near(front.Hp, 0);

        // 회전 베기는 둘레의 시설을 부순다. 시설이 등 뒤에 있어도 된다. 조준 방향의 타격은 등 뒤의 시설에 닿지 않는다.
        Fixture razing = new Fixture();
        Facility razed = razing.Match.Facilities[0];
        razing.Player.Position = razed.Position - FixedVector.FromInt(40, 0);
        razing.Player.Aim = Angle.FromRadians(Math.PI);
        Prepare(razing, razing.Player, whirl);
        Advance(razing.Match, 0.3);
        Near(razed.Hp, razed.MaxHp - 1);

        Fixture missing = new Fixture();
        Facility untouched = missing.Match.Facilities[0];
        missing.Player.Position = untouched.Position - FixedVector.FromInt(40, 0);
        missing.Player.Aim = Angle.FromRadians(Math.PI);
        Prepare(missing, missing.Player, rules.Strike);
        Advance(missing.Match, 0.3);
        Near(untouched.Hp, untouched.MaxHp);

        // 벽은 조준 방향으로만 친다. 부술 수 있는 벽(체력 3) 바로 옆에서 회전 베기를 세 번 해도 벽은 그대로다.
        // 같은 자리에서 근접 타격으로 치면 부서진다(Structures, WallHitEffect).
        Fixture walled = new Fixture();
        walled.Player.Position = FixedVector.FromInt(680, 500);
        for (int i = 0; i < 3; i++)
        {
            Prepare(walled, walled.Player, whirl);
            Advance(walled.Match, whirl.Duration + 0.05);
        }
        Check(walled.Match.Map.Solid(15, 10), "회전 베기가 벽을 쳤습니다");
    }

    private static void Flank()
    {
        // B를 향해 방어를 예고하는 적을 A가 뒤에서 친다. 방패가 A를 향하지 않으므로 상성 없이 일반 피해만 준다.
        Fixture shielded = new Fixture();
        Player front = Ally(shielded, "front");
        Enemy blocker = Target(shielded);
        blocker.Position = FixedVector.FromInt(420, 730);
        front.Position = FixedVector.FromInt(380, 730);
        shielded.Player.Position = FixedVector.FromInt(460, 730);
        shielded.Player.Aim = Angle.FromRadians(Math.PI);
        blocker.Action = new EnemyAction(30050, new EnemySkill("block", 1, 0, 1, 62, 15), Angle.FromRadians(Math.PI), shielded.Match.Tick);
        PlayerAction behind = Prepare(shielded, shielded.Player, "strike", 0.2);
        Advance(shielded.Match, Rules.Step);
        Near(blocker.Hp, 0);
        Near(shielded.Player.Hp, 6);
        Check(!behind.Cancelled, "측면 공격이 패배로 취소되었습니다");

        // B를 향한 타격이 닿는 같은 틱에 A가 측면에서 쓰러뜨리면 그 공격은 무효가 되어 B도 다치지 않는다.
        Fixture flanked = new Fixture();
        Player target = Ally(flanked, "target");
        Enemy striker = Target(flanked);
        striker.Position = FixedVector.FromInt(420, 730);
        target.Position = FixedVector.FromInt(380, 730);
        flanked.Player.Position = FixedVector.FromInt(460, 730);
        flanked.Player.Aim = Angle.FromRadians(Math.PI);
        striker.Action = new EnemyAction(30051, new EnemySkill("strike", 1, 0, 0.05, 85, 25), Angle.FromRadians(Math.PI), flanked.Match.Tick - Rules.ToTicks(0.05));
        Prepare(flanked, flanked.Player, "strike", 0.2);
        Advance(flanked.Match, Rules.Step);
        Near(striker.Hp, 0);
        Near(flanked.Player.Hp, 6);
        Near(target.Hp, 6);
    }

    private static void ShoveReach()
    {
        // 방패는 왼쪽(π)을 향하고 대원은 적 몸 옆 70° 자리에 붙어 있다. 방패의 전방 120° 밖이지만 밀어치기는 닿는다.
        FixedVector beside = Angle.FromRadians(Math.PI + 1.22).Direction() * 27;
        // 대원은 적 쪽(beside의 반대 방향)을 본다.
        Angle aim = Angle.Of(-beside);

        // 밀어치기가 닿는 대원은 준비 중인 잡기로도 받아칠 수 있어야 한다. 잡기는 방어를 이긴다.
        Fixture grabbing = new Fixture();
        Enemy shover = Target(grabbing);
        shover.Position = FixedVector.FromInt(420, 730);
        grabbing.Player.Position = shover.Position + beside;
        grabbing.Player.Aim = aim;
        shover.Action = new EnemyAction(30052, new EnemySkill("block", 1, 0, 0.05, 62, 15), Angle.FromRadians(Math.PI), grabbing.Match.Tick - Rules.ToTicks(0.05));
        PlayerAction grab = Prepare(grabbing, grabbing.Player, "grab");
        Advance(grabbing.Match, Rules.Step);
        Check(grabbing.Match.Tick < grab.ContactTick, "잡기가 준비 동작을 마쳤습니다");
        Near(shover.Hp, 0);
        Near(grabbing.Player.Hp, 6);

        // 같은 자리에서 친 타격은 측면 공격이 아니다. 방어가 타격을 이긴다.
        Fixture striking = new Fixture();
        Enemy guard = Target(striking);
        guard.Position = FixedVector.FromInt(420, 730);
        striking.Player.Position = guard.Position + beside;
        striking.Player.Aim = aim;
        guard.Action = new EnemyAction(30053, new EnemySkill("block", 1, 0, 0.05, 62, 15), Angle.FromRadians(Math.PI), striking.Match.Tick - Rules.ToTicks(0.05));
        PlayerAction strike = Prepare(striking, striking.Player, "strike", 0.2);
        Advance(striking.Match, Rules.Step);
        Near(guard.Hp, 1);
        Near(striking.Player.Hp, 5);
        Check(strike.Cancelled, "방어에 진 타격이 취소되지 않았습니다");
    }

    private static void DrawCredit()
    {
        // 같은 2티어 타격 한 번에 대원(f.Player)은 타격으로 비기고 동료(partner)는 받아치기로 부분 대응한다.
        // 엘리트는 부분 대응 피해를 먼저 받고 비김 피해 1을 받는다. 처치는 쓰러뜨린 피해를 준 대원이 가져가고,
        // 부분 대응 피해로 쓰러지면 비김 피해는 더하지 않는다. 대원이 받는 피해는 어느 경우든 같다(비긴 대원 2, 부분 대응한 동료 1).
        // 판정 결과의 효과는 어느 경우든 부분 대응과 비김이 하나씩이다. 피해를 줄 때 같은 효과를 한 번 더 만들지 않는다.
        // 경우마다 행동의 부분 대응 피해, 엘리트의 체력, 비김 피해가 마지막 피해인지다.
        // 0: 부분 대응 피해가 없어 비김 피해로 1→0. 1: 부분 대응 피해로 1→0. 2: 부분 대응 피해로 2→1, 비김 피해로 1→0.
        int[] partials = new int[] { 0, 1, 1 };
        int[] startHps = new int[] { 1, 1, 2 };
        bool[] drawFinishes = new bool[] { true, false, true };
        for (int i = 0; i < partials.Length; i++)
        {
            Fixture f = new Fixture();
            Player partner = Ally(f, "partner");
            Enemy elite = Threat(f, "strike", 2, partials[i]);
            elite.Hp = startHps[i];
            Prepare(f, f.Player, "strike");
            Prepare(f, partner, "parry");
            Advance(f.Match, 0.08);
            Near(elite.Hp, 0);
            Near(f.Player.Hp, f.Player.MaxHp - 2);
            Near(partner.Hp, partner.MaxHp - 1);
            Equal(CountEffects(f.Match, "partial"), 1);
            Equal(CountEffects(f.Match, "draw"), 1);
            if (drawFinishes[i])
            {
                Equal(f.Player.Kills, 1);
                Equal(partner.Kills, 0);
            }
            else
            {
                Equal(f.Player.Kills, 0);
                Equal(partner.Kills, 1);
            }
        }
    }

    private static void GrabRetarget()
    {
        // 처음에는 먼 적 하나만 잡기 범위에 있다.
        Fixture f = new Fixture();
        Enemy far = Target(f);
        far.Position = f.Player.Position + FixedVector.FromInt(45, 0);
        Enemy near = Target(f);
        near.Position = f.Player.Position + FixedVector.FromInt(200, 0);
        Prepare(f, f.Player, "grab");
        Advance(f.Match, Rules.Step);
        // 준비 동작(0.12초) 중에 더 가까운 적이 들어온다. 접촉하는 순간 가장 가까운 적을 잡아야 한다.
        near.Position = f.Player.Position + FixedVector.FromInt(28, 0);
        Advance(f.Match, 0.15);
        Near(near.Hp, 0);
        Near(far.Hp, 1);
    }

    private static void GrabContactTarget()
    {
        // 접촉 바로 전 틱까지는 first만 잡기 범위에 있다.
        Fixture f = new Fixture();
        Enemy first = Target(f);
        first.Position = f.Player.Position + FixedVector.FromInt(58, 0);
        Enemy second = Target(f);
        second.Position = f.Player.Position + FixedVector.FromInt(200, 0);
        PlayerAction grab = Prepare(f, f.Player, "grab");
        Advance(f.Match, 0.1);
        Check(f.Match.Tick < grab.ContactTick, "접촉 전 틱이 아닙니다");
        // 접촉하는 틱 직전에 second가 더 가까이 들어온다. 접촉하는 틱에 가장 가까운 second를 잡아야 한다.
        second.Position = f.Player.Position + FixedVector.FromInt(30, 15);
        Advance(f.Match, Rules.Step);
        Check(f.Match.Tick >= grab.ContactTick, "접촉 틱이 아닙니다");
        Near(second.Hp, 0);
        Near(first.Hp, 1);

        // 준비 동작 중에 대상이 범위를 벗어나면 대상을 비운다. 그 뒤 범위에 들어온 적을 잡아야 한다.
        Fixture g = new Fixture();
        Enemy leaving = Target(g);
        leaving.Position = g.Player.Position + FixedVector.FromInt(45, 0);
        PlayerAction hold = Prepare(g, g.Player, "grab");
        Advance(g.Match, Rules.Step);
        leaving.Position = g.Player.Position + FixedVector.FromInt(300, 0);
        Advance(g.Match, 0.15);
        Check(g.Match.Tick >= hold.ContactTick && g.Match.Tick < hold.ActiveUntilTick, "잡기 유효 시간이 아닙니다");
        Enemy arriving = Target(g);
        arriving.Position = g.Player.Position + FixedVector.FromInt(30, 0);
        Advance(g.Match, Rules.Step);
        Near(arriving.Hp, 0);
        Near(leaving.Hp, 1);
    }

    private static void LongStep()
    {
        // 서버가 가장 많이 밀렸을 때처럼 한 번에 최대 틱 수를 진행한다. 한 틱씩 따로 진행하므로
        // 0.25초 공격 창 안에서 방어가 판정된다.
        using RoomHost host = NewHost();
        Fixture f = new Fixture();
        Room room = new Room(f.Match, null);
        Enemy enemy = Threat(f, "strike");
        Prepare(f, f.Player, "parry");
        host.StepRoom(f.Match.Code, room, TickClock.MaxTicksPerWake, false);
        Near(enemy.Hp, 0);
        Near(f.Player.Hp, 6);
        // 밀린 틱을 몰아서 진행해도 이동 경로가 벽을 통과하지 않는다. 오른쪽 벽의 왼쪽 면은 x = 720이다.
        Fixture walker = new Fixture();
        Room walkerRoom = new Room(walker.Match, null);
        walker.Player.Position = FixedVector.FromInt(680, 500);
        walker.Match.Input(walker.Player, 1, 1, 0, 0, 0);
        host.StepRoom(walker.Match.Code, walkerRoom, TickClock.MaxTicksPerWake, false);
        Check(walker.Player.Position.X < Fixed.FromInt(720), "밀린 틱을 진행하다 벽을 통과했습니다");
        Check(walker.Player.Position.X > Fixed.FromInt(700), "벽까지 이동하지 않았습니다");
    }
}
