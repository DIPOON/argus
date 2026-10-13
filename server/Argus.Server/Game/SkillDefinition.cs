namespace Argus.Server.Game;

public sealed class SkillDefinition
{
    public string Id { get; }
    public string Kind { get; }
    public int Mana { get; }
    public double ContactAfter { get; }
    public double ActiveSeconds { get; }
    public double Duration { get; }
    public double Range { get; }
    public double HalfWidth { get; }
    // 기술별 동작은 id가 아니라 아래 속성으로 판단한다. 같은 종류의 새 기술도 속성만 정하면 같은 판정을 받는다.
    // 먼저 치지 않고, 유효 시간 동안 앞에서 들어오는 공격에만 반응한다.
    public bool GuardsFront { get; }
    // 유효 시간이 끝나면 상성 없는 일반 반격을 낸다.
    public bool HasFollowup { get; }
    // 처음 닿은 가장 가까운 적 하나만 대상으로 정하고 그 적만 판정한다.
    public bool SingleTarget { get; }
    // 접촉 중에 시설과 파괴 가능한 벽을 부순다.
    public bool BreaksStructures { get; }

    public SkillDefinition(string id, string kind, int mana, double contactAfter,
        double activeSeconds, double duration, double range, double halfWidth,
        bool guardsFront, bool hasFollowup, bool singleTarget, bool breaksStructures)
    {
        Id = id;
        Kind = kind;
        Mana = mana;
        ContactAfter = contactAfter;
        ActiveSeconds = activeSeconds;
        Duration = duration;
        Range = range;
        HalfWidth = halfWidth;
        GuardsFront = guardsFront;
        HasFollowup = hasFollowup;
        SingleTarget = singleTarget;
        BreaksStructures = breaksStructures;
    }
}

// 상성 종류로 승패를 비교하고, 완전 파훼에 필요한 대응 수는 별도로 합산한다.
public static class Affinity
{
    public static bool Beats(string response, string attack)
    {
        return (response == "block" && attack == "strike") ||
            (response == "channel" && attack == "block") ||
            (response == "strike" && attack == "channel");
    }
}

public sealed class EnemySkill
{
    public string Kind { get; }
    public int Tier { get; }
    public int PartialDamage { get; }
    public double Windup { get; }
    public double Range { get; }
    public double HalfWidth { get; }

    public EnemySkill(string kind, int tier, int partialDamage, double windup, double range, double halfWidth)
    {
        Kind = kind;
        Tier = tier;
        PartialDamage = partialDamage;
        Windup = windup;
        Range = range;
        HalfWidth = halfWidth;
    }
}

public sealed class EnemyDefinition
{
    public string Id { get; }
    public string Name { get; }
    public float Radius { get; }
    public int Health { get; }
    public double Speed { get; }
    public EnemySkill[] Skills { get; }

    public EnemyDefinition(string id, string name, float radius, int health, double speed, EnemySkill[] skills)
    {
        Id = id;
        Name = name;
        Radius = radius;
        Health = health;
        Speed = speed;
        Skills = skills;
    }

    public static readonly EnemyDefinition Grunt = new EnemyDefinition("grunt", "병사", 13, 1, 100,
        new EnemySkill[]
        {
            new EnemySkill("strike", 1, 0, 0.85, 65, 15),
            new EnemySkill("block", 1, 0, 0.95, 62, 15),
            new EnemySkill("channel", 1, 0, 0.95, 60, 13)
        });

    // 분쇄자는 타격만 강화한다. 부분 방어로는 분쇄자에게 피해를 주지 못한다.
    public static readonly EnemyDefinition Breaker = new EnemyDefinition("breaker", "분쇄자", 22, 2, 82,
        new EnemySkill[]
        {
            new EnemySkill("strike", 2, 0, 1.1, 82, 24),
            new EnemySkill("block", 1, 0, 0.95, 70, 20),
            new EnemySkill("channel", 1, 0, 0.95, 68, 18)
        });

    // 철위병은 타격과 방어를 강화한다. 부분 파훼로 체력 1까지 깎을 수 있다.
    public static readonly EnemyDefinition Warden = new EnemyDefinition("warden", "철위병", 24, 2, 76,
        new EnemySkill[]
        {
            new EnemySkill("strike", 2, 1, 1.1, 82, 24),
            new EnemySkill("block", 2, 1, 1.2, 78, 24),
            new EnemySkill("channel", 1, 0, 1, 68, 18)
        });
}
