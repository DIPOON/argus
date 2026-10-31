using System.Text.Json.Serialization;

namespace Argus.Server.Game;

public sealed class SkillDefinition
{
    public string Id { get; }
    public string Kind { get; }
    public int Mana { get; }
    public double ContactAfter { get; }
    public double ActiveSeconds { get; }
    public double Duration { get; }
    // 사거리와 반폭은 클라이언트에 그대로 보내는 설정 값이다. 판정은 아래의 고정소수점 값으로 한다.
    public double Range { get; }
    public double HalfWidth { get; }
    // 판정용 고정소수점 사거리와 반폭이다. 설정 값에서 한 번만 바꿔 둔다. 통신과 상태 요약의 JSON에는 넣지 않는다.
    [JsonIgnore]
    public Fixed RangeFixed { get; }
    [JsonIgnore]
    public Fixed HalfWidthFixed { get; }
    // 기술별 동작은 id가 아니라 아래 속성으로 판단한다. 같은 종류의 새 기술도 속성만 정하면 같은 판정을 받는다.
    // 먼저 치지 않고, 유효 시간 동안 앞에서 들어오는 공격에만 반응한다.
    public bool GuardsFront { get; }
    // 유효 시간이 끝나면 상성 없는 일반 반격을 낸다.
    public bool HasFollowup { get; }
    // 처음 닿은 가장 가까운 적 하나만 대상으로 정하고 그 적만 판정한다.
    public bool SingleTarget { get; }
    // 접촉 중에 시설과 파괴 가능한 벽을 부순다.
    public bool BreaksStructures { get; }
    // 유효 시간 뒤의 반격이 시설과 파괴 가능한 벽을 부순다. HasFollowup인 기술에만 의미가 있다.
    public bool FollowupBreaksStructures { get; }
    // 조준하지 않는 둘레 기술이다. 범위가 조준 방향의 사각형이 아니라 대원 둘레의 원이고, 사거리가 그 반지름이다(반폭은 쓰지 않는다).
    // 방어(GuardsFront)는 앞만이 아니라 모든 방향에서 들어오는 공격을 막는다. 벽은 조준 방향으로만 치므로 둘레 기술은 벽을 치지 않는다.
    public bool Around { get; }

    public SkillDefinition(string id, string kind, int mana, double contactAfter,
        double activeSeconds, double duration, double range, double halfWidth,
        bool guardsFront, bool hasFollowup, bool singleTarget, bool breaksStructures, bool followupBreaksStructures,
        bool around)
    {
        Id = id;
        Kind = kind;
        Mana = mana;
        ContactAfter = contactAfter;
        ActiveSeconds = activeSeconds;
        Duration = duration;
        Range = range;
        HalfWidth = halfWidth;
        RangeFixed = Fixed.FromDouble(range);
        HalfWidthFixed = Fixed.FromDouble(halfWidth);
        GuardsFront = guardsFront;
        HasFollowup = hasFollowup;
        SingleTarget = singleTarget;
        BreaksStructures = breaksStructures;
        FollowupBreaksStructures = followupBreaksStructures;
        Around = around;
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
    // 2티어 행동에 맞는 대응이 하나뿐일 때(부분 대응) 적이 받는 피해다. 이 피해로도 쓰러진다.
    public int PartialDamage { get; }
    public double Windup { get; }
    // 사거리와 반폭은 클라이언트에 그대로 보내는 설정 값이고, 판정은 고정소수점 값으로 한다.
    public double Range { get; }
    public double HalfWidth { get; }
    [JsonIgnore]
    public Fixed RangeFixed { get; }
    [JsonIgnore]
    public Fixed HalfWidthFixed { get; }

    public EnemySkill(string kind, int tier, int partialDamage, double windup, double range, double halfWidth)
    {
        Kind = kind;
        Tier = tier;
        PartialDamage = partialDamage;
        Windup = windup;
        Range = range;
        HalfWidth = halfWidth;
        RangeFixed = Fixed.FromDouble(range);
        HalfWidthFixed = Fixed.FromDouble(halfWidth);
    }
}

public sealed class EnemyDefinition
{
    public string Id { get; }
    public string Name { get; }
    // 몸의 반지름이다. 판정에 쓰는 고정소수점 값이고, 스냅샷에는 실수로 바꿔 보낸다.
    public Fixed Radius { get; }
    public int Health { get; }
    // 초당 이동 거리(설정 값)다. 판정은 한 틱에 움직이는 거리(StepPerTick)로 한다.
    public double Speed { get; }
    // 한 틱에 움직이는 거리다. 1/30을 곱하지 않고 30으로 나눈다. 1/30을 고정소수점으로 반올림하면 0.3% 어긋난다.
    public Fixed StepPerTick { get; }
    public EnemySkill[] Skills { get; }
    // 2티어 기술이 하나라도 있으면 엘리트다. 엘리트를 끈 작전(Rules.AllowEliteEnemies가 false)에는 나오지 않는다.
    public bool IsElite { get; }

    public EnemyDefinition(string id, string name, Fixed radius, int health, double speed, EnemySkill[] skills)
    {
        // 몸 지름은 한 칸보다 작아야 한다. 적의 길(흐름장)은 칸 가운데를 이으므로, 지름이 한 칸이면(반지름 24) 칸 가운데 줄이
        // 벽 모서리에 딱 붙어 모서리와 한 칸 틈에서 멈춘다(EncounterTests의 NarrowGap). 검사의 종류 목록에 넣지 않은 새 적도 여기서 막는다.
        if (radius * 2 >= Fixed.FromInt(BattleMap.Cell))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, $"적의 몸 지름은 한 칸({BattleMap.Cell})보다 작아야 합니다");
        }
        Id = id;
        Name = name;
        Radius = radius;
        Health = health;
        Speed = speed;
        StepPerTick = Fixed.FromDouble(speed) / Rules.TicksPerSecond;
        Skills = skills;
        for (int i = 0; i < skills.Length; i++)
        {
            if (skills[i].Tier >= 2)
            {
                IsElite = true;
            }
        }
    }

    // id로 정의를 찾는다. 기록에 적은 적 종류를 되읽을 때 쓴다. 모르는 id면 예외를 낸다.
    public static EnemyDefinition FromId(string id)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i].Id == id)
            {
                return All[i];
            }
        }
        throw new ArgumentException($"적 종류 {id}를 모릅니다", nameof(id));
    }

    public static readonly EnemyDefinition Grunt = new EnemyDefinition("grunt", "병사", Fixed.FromInt(13), 1, 100,
        new EnemySkill[]
        {
            new EnemySkill("strike", 1, 0, 0.85, 65, 15),
            new EnemySkill("block", 1, 0, 0.95, 62, 15),
            new EnemySkill("channel", 1, 0, 0.95, 60, 13)
        });

    // 분쇄자는 타격만 강화한다. 혼자 받아치기로 막으면(부분 대응) 피해 1을 받는다.
    public static readonly EnemyDefinition Breaker = new EnemyDefinition("breaker", "분쇄자", Fixed.FromInt(22), 2, 82,
        new EnemySkill[]
        {
            new EnemySkill("strike", 2, 1, 1.1, 82, 24),
            new EnemySkill("block", 1, 0, 0.95, 70, 20),
            new EnemySkill("channel", 1, 0, 0.95, 68, 18)
        });

    // 철위병은 타격과 방어를 강화한다. 두 행동 모두 부분 대응하면 피해 1을 받는다.
    // 몸 반지름은 칸의 절반(24)보다 작게 둔다. 반지름이 24면 적이 따라가는 칸 가운데 줄이 벽 모서리에 딱 붙어,
    // 반올림으로 조금만 벗어나도 모서리에 걸린 채 멈춘다(EncounterTests의 NarrowGap).
    public static readonly EnemyDefinition Warden = new EnemyDefinition("warden", "철위병", Fixed.FromInt(23), 2, 76,
        new EnemySkill[]
        {
            new EnemySkill("strike", 2, 1, 1.1, 82, 24),
            new EnemySkill("block", 2, 1, 1.2, 78, 24),
            new EnemySkill("channel", 1, 0, 1, 68, 18)
        });

    // 모든 적 종류다. 새 종류를 만들면 여기에 넣는다. 기록의 적 종류 되읽기(FromId), 가장 큰 몸(LargestRadius),
    // 모든 종류를 도는 검사가 이 목록을 쓴다. 정적 필드는 적힌 순서대로 만들어지므로 위의 정의들보다 뒤에 둔다.
    public static readonly IReadOnlyList<EnemyDefinition> All = Array.AsReadOnly(new EnemyDefinition[] { Grunt, Breaker, Warden });
    // 가장 큰 몸의 반지름이다. 증원 타원에 이 몸이 설 자리가 있는지 볼 때 쓴다.
    public static readonly Fixed LargestRadius = FindLargestRadius();

    private static Fixed FindLargestRadius()
    {
        Fixed largest = All[0].Radius;
        for (int i = 1; i < All.Count; i++)
        {
            if (All[i].Radius > largest)
            {
                largest = All[i].Radius;
            }
        }
        return largest;
    }
}
