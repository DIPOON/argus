using System.Numerics;
using Argus.Server.Game;
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
        tests.Add(new TestCase("A grab has a persistent window and one target per activation", SingleGrab));
        tests.Add(new TestCase("Friendly players can overlap and cannot damage each other", Friendly));
        tests.Add(new TestCase("All three tier-two actions can be broken by two overlapping responses", CooperativeMatrix));
        tests.Add(new TestCase("A complete break protects C irrespective of player iteration order", ProtectBystander));
        tests.Add(new TestCase("An expired response cannot be saved up toward a later tier-two attack", ExpiredContribution));
        tests.Add(new TestCase("Prepared independent effects from one owner also count toward the total", IndependentActions));
        tests.Add(new TestCase("Partial counters deal one to players and preserve each elite's distinct response", PartialVariants));
        tests.Add(new TestCase("Tier-two draws deal two to players and one to the elite only once", EliteDraw));
        tests.Add(new TestCase("Reduced HP leaves skill tiers unchanged; a weak skill can still be fully broken", WeakPhase));
        tests.Add(new TestCase("Wall cover prevents melee hits between otherwise clear endpoints", Cover));
        tests.Add(new TestCase("Melee destroys walls and mission facilities through normal inputs", Structures));
        tests.Add(new TestCase("Player-enemy and enemy-enemy bodies block movement while skills allow walking", Bodies));
        tests.Add(new TestCase("Long server steps preserve short active windows and prevent body tunneling", LongStep));
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
        Check(enemy.Action!.IsActive(f.Match.Now));
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
        facingAway.Action = new EnemyAction(999, facingAway.Action!.Skill, 0, rear.Match.Now);
        Prepare(rear, rear.Player, "parry");
        Advance(rear.Match, 0.8);
        Near(facingAway.Hp, 1);
        Near(rear.Player.Hp, 6);
    }

    private static void InvalidGuards()
    {
        Fixture rear = new Fixture();
        Enemy enemy = Threat(rear, "strike");
        Prepare(rear, rear.Player, "parry");
        rear.Player.Aim = Math.PI;
        Advance(rear.Match, 0.1);
        Near(rear.Player.Hp, 5);
        Near(enemy.Hp, 1);
        Fixture expired = new Fixture();
        Enemy second = Threat(expired, "strike", arrivesIn: 0.56);
        Prepare(expired, expired.Player, "parry");
        // 반격 사거리 밖에서 적의 공격만 닿게 하여, 종료된 방어 창을 검사한다.
        second.Position = expired.Player.Position + new Vector2(80, 0);
        Advance(expired.Match, 0.6);
        Near(expired.Player.Hp, 5);
        Near(second.Hp, 1);
    }

    private static void SingleGrab()
    {
        Fixture f = new Fixture();
        Enemy first = Target(f);
        Enemy second = Target(f);
        first.Position += new Vector2(0, 14);
        second.Position -= new Vector2(0, 14);
        PlayerAction grab = Prepare(f, f.Player, "grab");
        Advance(f.Match, 0.6);
        Near(first.Hp + second.Hp, 1);
        Check(grab.Target.HasValue);
        Fixture later = new Fixture();
        Enemy approaching = Target(later);
        approaching.Position += new Vector2(200, 0);
        Prepare(later, later.Player, "grab");
        Advance(later.Match, 0.3);
        approaching.Position = later.Player.Position + new Vector2(40, 0);
        Advance(later.Match, 0.1);
        Near(approaching.Hp, 0);
    }

    private static void Friendly()
    {
        Fixture f = new Fixture();
        Player ally = Ally(f, "ally");
        ally.Position += new Vector2(30, 0);
        f.Player.Mana = 12;
        for (int i = 0; i < 80; i++)
        {
            f.Match.Input(f.Player, i, 1, 0, 0, 1);
            f.Match.Step(Rules.Step);
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
        enemy.Position = f.Player.Position + new Vector2(80, 0);
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
        for (int partial = 0; partial <= 1; partial++)
        {
            Fixture f = new Fixture();
            Enemy enemy = Threat(f, "strike", 2, partial);
            PlayerAction parry = Prepare(f, f.Player, "parry");
            Advance(f.Match, 0.8);
            Near(enemy.Hp, 2 - partial);
            Near(f.Player.Hp, 5);
            Check(parry.Cancelled);
            enemy.Hp = 1;
            enemy.Action = new EnemyAction(999, new EnemySkill("strike", 2, partial, 0.05, 85, 25), Math.PI, f.Match.Now);
            Prepare(f, f.Player, "parry");
            Advance(f.Match, 0.1);
            Near(enemy.Hp, 1);
        }
        Fixture none = new Fixture();
        Enemy uncountered = Threat(none, "strike", 2);
        Advance(none.Match, 0.5);
        Near(none.Player.Hp, 4);
        Near(uncountered.Hp, 2);
    }

    private static void EliteDraw()
    {
        Fixture f = new Fixture();
        Enemy enemy = Threat(f, "strike", 2);
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.6);
        Near(f.Player.Hp, 4);
        Near(enemy.Hp, 1);
    }

    private static void WeakPhase()
    {
        Fixture f = new Fixture();
        Enemy elite = Target(f, EnemyDefinition.Warden);
        elite.Action = new EnemyAction(999, elite.Definition.Skills[2], Math.PI, f.Match.Now);
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.2);
        Near(elite.Hp, 0);
        Near(f.Player.Hp, 6);
        Equal(elite.Definition.Skills[0].Tier, 2);
        Equal(elite.Definition.Skills[1].Tier, 2);
    }

    private static void Cover()
    {
        Fixture f = new Fixture();
        f.Player.Position = new Vector2(464, 498);
        f.Player.Aim = 313 * Math.PI / 180;
        Enemy enemy = Target(f);
        enemy.Position = new Vector2(495, 465);
        Check(f.Match.Map.CanStand(f.Player.Position));
        Check(f.Match.Map.CanStand(enemy.Position));
        Check(!f.Match.Map.LineOfSight(f.Player.Position, enemy.Position));
        Prepare(f, f.Player, "strike");
        Advance(f.Match, 0.3);
        Near(enemy.Hp, 1);
    }

    private static void Structures()
    {
        Fixture f = new Fixture();
        f.Player.Position = new Vector2(680, 500);
        f.Player.Mana = 12;
        for (int i = 0; i < 70; i++)
        {
            f.Match.Input(f.Player, i, 0, 0, 0, 1);
            f.Match.Step(Rules.Step);
        }
        Check(!f.Match.Map.Solid(15, 10));
        Check(!f.Match.Map.Damage(0, 100));
        f.Player.Position = f.Match.Facilities[0].Position - new Vector2(55, 0);
        for (int i = 70; i < 170; i++)
        {
            f.Match.Input(f.Player, i, 0, 0, 0, 1);
            f.Match.Step(Rules.Step);
        }
        Near(f.Match.Facilities[0].Hp, 0);
    }

    private static void Bodies()
    {
        Fixture f = new Fixture();
        Enemy enemy = Target(f);
        f.Player.Mana = 5;
        for (int i = 0; i < 20; i++)
        {
            f.Match.Input(f.Player, i, 1, 0, Math.PI, 2);
            f.Match.Step(Rules.Step);
        }
        Check(f.Player.Position.X > 300);
        Check(f.Player.Position.X <= enemy.Position.X - enemy.Definition.Radius - Rules.PlayerRadius + 0.001);
        Check(f.Player.Mana < 5);
        Fixture moving = new Fixture();
        Enemy lead = new Enemy(1, new Vector2(390, 730), EnemyDefinition.Grunt);
        Enemy following = new Enemy(2, new Vector2(420, 730), EnemyDefinition.Grunt);
        moving.Match.Enemies.Add(lead);
        moving.Match.Enemies.Add(following);
        for (int i = 0; i < 90; i++)
        {
            moving.Match.Step(Rules.Step);
            Check(Vector2.Distance(lead.Position, following.Position) >= 26 - 0.001);
            Check(Vector2.Distance(lead.Position, moving.Player.Position) >= 26 - 0.001);
        }
    }

    private static void LongStep()
    {
        Fixture f = new Fixture();
        Enemy enemy = Threat(f, "strike");
        Prepare(f, f.Player, "parry");
        f.Match.Step(2);
        Near(enemy.Hp, 0);
        Near(f.Player.Hp, 6);
        Vector2 at = f.Match.Map.Move(new Vector2(680, 500), new Vector2(1000, 0));
        Check(at.X < 720);
    }
}
