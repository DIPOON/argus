namespace Argus.Server.Game;

// 플레이 시험을 위한 초기 수치다. 확정한 판정 규칙은 docs/combat.md에 기록한다.
public sealed class Rules
{
    // 게임은 1초에 30틱, 한 틱에 정확히 Step초씩 진행한다.
    public const int TicksPerSecond = 30;
    public const double Step = 1.0 / TicksPerSecond;

    // 초로 정한 기간을 틱 수로 바꾼다. 그 시간이 다 지나는 첫 틱까지 세므로 올림한다. 0.18초는 6틱이다.
    // 실수 곱셈의 오차로 정수여야 할 값이 150.00000000000003처럼 나와 한 틱 늘지 않도록 아주 작은 여유를 뺀다.
    // 시각은 모두 틱 번호로 저장하고 비교한다. 초로 저장한 마감을 비교하면 같은 5초가 시작 틱에 따라 150틱도 되고 151틱도 된다.
    public static long ToTicks(double seconds)
    {
        return (long)Math.Ceiling(seconds * TicksPerSecond - 0.000001);
    }

    // 틱 수를 초로 바꾼다. 스냅샷처럼 초로 보여 줄 때만 쓴다.
    public static double ToSeconds(long ticks)
    {
        return (double)ticks / TicksPerSecond;
    }
    public const int MaxPlayers = 4;

    // 운영 방(생성 작전)의 규칙이다. 엘리트가 나오고 적의 준비 동작은 기본 길이(1배)다.
    // 기본 규칙(new Rules())은 손으로 만든 맵의 검사가 쓰는 튜토리얼 수준(병사만, 준비 동작 두 배)이다.
    public static Rules ForGeneratedMission()
    {
        return new Rules { AllowEliteEnemies = true, EnemyWindupMultiplier = 1 };
    }
    // 대원 몸의 반지름이다. 판정에 쓰는 고정소수점 값이고, 스냅샷의 규칙에는 실수로 바꿔 보낸다.
    public static readonly Fixed PlayerRadius = Fixed.FromInt(13);
    public double DeploySeconds { get; init; } = 5;
    public double ExtractionSeconds { get; init; } = 20;
    public double ReconnectSeconds { get; init; } = 120;
    // HTTP 참가 후 첫 접속까지 기다리는 시간이다. 연결이 끊긴 대원의 복구 유예와 별개다.
    public double FirstConnectSeconds { get; init; } = 15;
    public double SpawnInterval { get; init; } = 7;
    public int MaxEnemies { get; init; } = 60;
    // 첫 작전에서는 기본 상성을 익히도록 일반 적만 내보내고 준비 시간을 늘린다.
    public bool AllowEliteEnemies { get; init; } = false;
    public double EnemyWindupMultiplier { get; init; } = 2;
    public double PlayerSpeed { get; init; } = 180;
    public double PlayerHealth { get; init; } = 6;
    public int MaxMana { get; init; } = 24;
    public double Vision { get; init; } = 410;
    public double SupplyCooldown { get; init; } = 18;
    // 캠프 상자로 채울 수 있는 횟수다. 분대가 함께 쓰고 한 번에 쓴 사람만 채운다. 대원마다 세지 않는 것은, 새 게스트로 다시
    // 들어온 사람을 서버가 같은 사람으로 알아볼 수 없어 다시 채울 수 있기 때문이다. 횟수가 없으면 캠프 옆에서 버텨 작전이 끝나지 않는다.
    public int CampSupplyUses { get; init; } = 4;
    // 들판의 일반 보급 상자 하나로 채울 수 있는 횟수다. 캠프 상자처럼 분대가 함께 쓰고, 한 번에 쓴 사람만 채운다.
    public int FieldSupplyUses { get; init; } = 2;
    // 시설을 부순 자리에 생기는 보급 상자의 횟수다. 들판 상자처럼 쓴 사람만 채운다.
    public int FacilitySupplyUses { get; init; } = 2;
    public double GuardHalfAngle { get; init; } = Math.PI / 3;

    // 대원 기술이다. 기술은 규칙마다 바꾸지 않으므로 모든 규칙이 같은 정의를 쓴다.
    // 그래서 참가 요청의 장비 검사(ValidSlots)도 규칙 없이 기술을 찾을 수 있다.
    // 근접 타격: 범위에 닿은 적을 한 번씩 치고 시설과 벽을 부순다.
    private static readonly SkillDefinition StrikeSkill =
        new SkillDefinition("strike", "strike", 1, 0.18, 0.42, 0.66, 54, 10,
            guardsFront: false, hasFollowup: false, singleTarget: false, breaksStructures: true,
            followupBreaksStructures: false, around: false);
    // 받아치기: 전방 방어 뒤 일반 반격. 반격은 시설과 벽도 부순다.
    private static readonly SkillDefinition ParrySkill =
        new SkillDefinition("parry", "block", 1, 0, 0.55, 0.98, 54, 10,
            guardsFront: true, hasFollowup: true, singleTarget: false, breaksStructures: false,
            followupBreaksStructures: true, around: false);
    // 잡기: 가장 가까운 한 명을 붙잡고 동작 내내 그 대상만 판정한다.
    private static readonly SkillDefinition GrabSkill =
        new SkillDefinition("grab", "channel", 1, 0.12, 0.65, 0.85, 48, 8,
            guardsFront: false, hasFollowup: false, singleTarget: true, breaksStructures: false,
            followupBreaksStructures: false, around: false);
    // 아래 셋은 조준하지 않는 둘레 기술이다. 버튼만 눌러 쓸 수 있다. 시간과 마나는 위의 같은 종류 기술과 같다.
    // 회전 베기: 둘레(반지름 48)에 닿은 적을 한 번씩 친다. 둘레의 시설도 부순다. 벽은 치지 않는다.
    private static readonly SkillDefinition SpinSkill =
        new SkillDefinition("spin", "strike", 1, 0.18, 0.42, 0.66, 48, 0,
            guardsFront: false, hasFollowup: false, singleTarget: false, breaksStructures: true,
            followupBreaksStructures: false, around: true);
    // 원형 방어: 모든 방향에서 들어오는 공격을 막는다. 반격이 없으므로 사거리를 쓰지 않는다.
    private static readonly SkillDefinition WardSkill =
        new SkillDefinition("ward", "block", 1, 0, 0.55, 0.98, 0, 0,
            guardsFront: true, hasFollowup: false, singleTarget: false, breaksStructures: false,
            followupBreaksStructures: false, around: true);
    // 낚아채기: 둘레(반지름 48)에서 가장 가까운 한 명을 붙잡고 동작 내내 그 대상만 판정한다.
    private static readonly SkillDefinition SnatchSkill =
        new SkillDefinition("snatch", "channel", 1, 0.12, 0.65, 0.85, 48, 0,
            guardsFront: false, hasFollowup: false, singleTarget: true, breaksStructures: false,
            followupBreaksStructures: false, around: true);

    // 모든 대원 기술이다. 규칙을 보낼 때 이 순서로 보낸다. 새 기술을 만들면 여기에 넣는다.
    // 정적 필드는 적힌 순서대로 만들어지므로 위의 정의들보다 뒤에 둔다.
    public static readonly IReadOnlyList<SkillDefinition> AllSkills = Array.AsReadOnly(new SkillDefinition[]
    {
        StrikeSkill, ParrySkill, GrabSkill, SpinSkill, WardSkill, SnatchSkill
    });

    public SkillDefinition Strike { get; } = StrikeSkill;
    public SkillDefinition Parry { get; } = ParrySkill;
    public SkillDefinition Grab { get; } = GrabSkill;
    public SkillDefinition Spin { get; } = SpinSkill;
    public SkillDefinition Ward { get; } = WardSkill;
    public SkillDefinition Snatch { get; } = SnatchSkill;
    public double ParryFollowupSeconds { get; init; } = 0.18;

    // id로 대원 기술을 찾는다. 모르는 id면 null이다.
    public static SkillDefinition? FindSkill(string? id)
    {
        for (int i = 0; i < AllSkills.Count; i++)
        {
            if (AllSkills[i].Id == id)
            {
                return AllSkills[i];
            }
        }
        return null;
    }

    public SkillDefinition Skill(string id)
    {
        SkillDefinition? skill = FindSkill(id);
        if (skill == null)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }
        return skill;
    }

    // 장비 칸 넷 가운데 앞의 세 칸이 받는 기술 종류다. 차례로 타격·방어·잡기 칸이다.
    private static readonly string[] SlotKinds = new string[] { "strike", "block", "channel" };

    // 앞의 세 칸은 그 칸 종류의 기술 가운데 하나이고, 네 번째 자유 칸은 아무 기술이나 된다(2026-10-31 합의).
    // 그래서 어떤 장비든 세 상성을 모두 하나씩은 든다.
    public static bool ValidSlots(string[]? slots)
    {
        if (slots == null || slots.Length != 4)
        {
            return false;
        }
        for (int i = 0; i < slots.Length; i++)
        {
            SkillDefinition? skill = FindSkill(slots[i]);
            if (skill == null)
            {
                return false;
            }
            if (i < SlotKinds.Length && skill.Kind != SlotKinds[i])
            {
                return false;
            }
        }
        return true;
    }
}
