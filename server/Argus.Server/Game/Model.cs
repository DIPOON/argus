namespace Argus.Server.Game;

public sealed class Player
{
    public string Id { get; }
    public string Name { get; set; }
    public string[] Slots { get; set; } = new string[] { "strike", "parry", "grab", "strike" };
    public FixedVector Position;
    // 조준 방향이다. 클라이언트가 보낸 라디안을 정수 각도로 바꿔 둔다.
    // 방향 벡터(AimDirection)는 판정마다 쓰므로 조준을 바꿀 때 한 번만 구해 둔다. 상태 요약에는 각도만 적는다.
    public Angle Aim
    {
        get
        {
            return _aim;
        }
        set
        {
            _aim = value;
            AimDirection = value.Direction();
        }
    }
    public FixedVector AimDirection { get; private set; } = Angle.Zero.Direction();
    private Angle _aim;
    public string State { get; set; } = "waiting";
    public bool HasDeployed;
    // 투입이 끝나는 틱이다.
    public long DeployTick;
    public FixedVector Landing;
    public double Hp;
    public double MaxHp;
    public int Mana;
    public int MaxMana;
    // 지금 기술이 끝나 다음 기술을 시작할 수 있는 틱이다.
    public long BusyUntilTick;
    public int RequestedSlot;
    public List<PlayerAction> Actions { get; } = new List<PlayerAction>();
    // 입력을 마지막으로 받은 틱이다. 받은 적이 없으면 NoInputTick이다.
    public long LastInputTick = NoInputTick;
    public long LastSequence = -1;
    // 이동 방향이다. 길이는 1 이하이고, 틱마다 이 방향으로 이동 속도만큼 움직인다.
    public FixedVector Move;
    public long Connection;
    public bool Connected;
    // 연결이 끊긴 틱이다. 연결되어 있으면 null이다.
    public long? DisconnectedTick;
    // HTTP로 참가했지만 아직 WebSocket으로 한 번도 접속하지 않은 자리다.
    public bool AwaitingFirstConnection;
    public int Kills;
    public int Deaths;
    // 입력을 받은 적이 없다는 표시다. 입력이 끊겼는지 볼 때 언제나 오래전으로 보이도록 충분히 이른 틱으로 둔다.
    // long.MinValue로 두면 지금 틱에서 뺄 때 넘쳐서 음수가 된다.
    public const long NoInputTick = -1_000_000;

    public Player(string id, string name)
    {
        Id = id;
        Name = name;
    }
}

// 적과 대원의 행동이 함께 가진 id와 시각(틱)이다. 스냅샷이 둘을 같은 모양으로 담을 때 쓴다.
public interface ITimedAction
{
    int Id { get; }
    long StartedTick { get; }
    long ContactTick { get; }
    long ActiveUntilTick { get; }
    long EndsTick { get; }
}

public sealed class PlayerAction : ITimedAction
{
    public int Id { get; }
    public SkillDefinition Skill { get; }
    public int Slot { get; }
    public long StartedTick { get; }
    public long ContactTick { get; }
    public long ActiveUntilTick { get; }
    public long EndsTick { get; }
    public bool Cancelled;
    public int? Target;
    // 잡기처럼 한 명만 판정하는 기술의 대상이 정해졌는지 나타낸다. 정해진 뒤에는 같은 시전에서 바꾸지 않는다.
    public bool TargetLocked;
    public HashSet<int> JudgedEnemies { get; } = new HashSet<int>();
    public HashSet<int> FollowupHits { get; } = new HashSet<int>();
    public HashSet<int> HitFacilities { get; } = new HashSet<int>();
    public HashSet<int> HitWalls { get; } = new HashSet<int>();

    public PlayerAction(int id, SkillDefinition skill, int slot, long startTick)
    {
        Id = id;
        Skill = skill;
        Slot = slot;
        // 기술의 시간은 모두 시작부터 잰 초다. 시작 틱에 각각 올림한 틱 수를 더한다.
        StartedTick = startTick;
        ContactTick = startTick + Rules.ToTicks(skill.ContactAfter);
        ActiveUntilTick = startTick + Rules.ToTicks(skill.ActiveSeconds);
        EndsTick = startTick + Rules.ToTicks(skill.Duration);
    }

    public bool IsActive(long tick)
    {
        return !Cancelled && tick >= StartedTick && tick < ActiveUntilTick;
    }

    // 반격이 끝나는 틱이다. 반격이 없는 기술은 반격 구간이 비어 있어 유효 시간이 끝나는 틱과 같다.
    public long FollowupUntilTick(double followupSeconds)
    {
        if (!Skill.HasFollowup)
        {
            return ActiveUntilTick;
        }
        // 시작부터 잰 초(유효 시간 + 반격 시간)를 한 번에 올림해 구한다.
        // 유효 시간이 끝난 틱에 반격 시간을 따로 올림해 더하면 한 틱 길어질 수 있다.
        return StartedTick + Rules.ToTicks(Skill.ActiveSeconds + followupSeconds);
    }

    public bool IsFollowup(long tick, double followupSeconds)
    {
        long followupUntil = FollowupUntilTick(followupSeconds);
        return !Cancelled && Skill.HasFollowup && tick >= ActiveUntilTick && tick < followupUntil;
    }
}

public sealed class EnemyAction : ITimedAction
{
    public int Id { get; }
    public EnemySkill Skill { get; }
    public Angle Aim { get; }
    // 행동하는 동안 방향은 바뀌지 않으므로 방향 벡터를 만들 때 한 번만 구해 둔다.
    public FixedVector Direction { get; }
    public long StartedTick { get; }
    public long ContactTick { get; }
    public long ActiveUntilTick { get; }
    public long EndsTick { get; }
    public HashSet<string> HitPlayers { get; } = new HashSet<string>();

    // 예고 → 0.25초 공격 창 → 0.65초 회복 순서로 진행한다.
    public const double ActiveSeconds = 0.25;
    public const double RecoverySeconds = 0.65;

    public EnemyAction(int id, EnemySkill skill, Angle aim, long startTick, double windupMultiplier = 1)
    {
        Id = id;
        Skill = skill;
        Aim = aim;
        Direction = aim.Direction();
        // 판정과 클라이언트의 예고가 같은 시간을 사용하도록 실제 접촉 시각에 배율을 적용한다.
        double windup = skill.Windup * windupMultiplier;
        // 단계마다 올림하면 끝이 한 틱씩 밀릴 수 있으므로, 각 단계의 끝을 시작부터 잰 초로 한 번에 올림한다.
        StartedTick = startTick;
        ContactTick = startTick + Rules.ToTicks(windup);
        ActiveUntilTick = startTick + Rules.ToTicks(windup + ActiveSeconds);
        EndsTick = startTick + Rules.ToTicks(windup + ActiveSeconds + RecoverySeconds);
    }

    public bool IsActive(long tick)
    {
        // 적의 준비 동작에도 이미 상성 종류가 있다.
        return tick >= StartedTick && tick < ActiveUntilTick;
    }
}

public sealed class Enemy
{
    public int Id { get; }
    public FixedVector Position;
    public EnemyDefinition Definition { get; }
    public double Hp;
    // 다음 행동을 고를 수 있는 틱이다.
    public long NextActionTick;
    public EnemyAction? Action;

    public Enemy(int id, FixedVector position, EnemyDefinition definition)
    {
        Id = id;
        Position = position;
        Definition = definition;
        Hp = definition.Health;
    }
}

public sealed class Facility
{
    public int Id { get; }
    public FixedVector Position { get; }
    public double Hp = 4;
    public double MaxHp = 4;

    public Facility(int id, FixedVector position)
    {
        Id = id;
        Position = position;
    }
}

public sealed class Supply
{
    public int Id { get; }
    public FixedVector Position { get; }
    public SupplyKind Kind { get; }
    // 부목표 상자를 분대가 회수했는지다.
    public bool Collected;
    // 남은 횟수다. 0이면 다 쓴 상자다. 부목표 상자는 1에서 시작해 회수하면 0이 된다.
    public int UsesLeft;
    // 대원마다 이 보급을 다시 쓸 수 있게 되는 틱이다.
    public Dictionary<string, long> ReadyTicks { get; } = new Dictionary<string, long>();

    public Supply(int id, FixedVector position, SupplyKind kind, int uses)
    {
        Id = id;
        Position = position;
        Kind = kind;
        UsesLeft = uses;
    }

    public bool Objective
    {
        get
        {
            return Kind == SupplyKind.Objective;
        }
    }

    public long ReadyTick(Player player)
    {
        if (ReadyTicks.TryGetValue(player.Id, out long ready))
        {
            return ready;
        }
        return 0;
    }
}

public sealed class Effect
{
    public int Id { get; }
    public FixedVector Position { get; }
    public string Kind { get; }
    // 이 틱이 되면 지운다. 스냅샷에는 초로 바꿔 보낸다.
    public long UntilTick { get; }

    public Effect(int id, FixedVector position, string kind, long untilTick)
    {
        Id = id;
        Position = position;
        Kind = kind;
        UntilTick = untilTick;
    }
}

public sealed class Notice
{
    // 안내마다 1부터 늘어나는 순번이다. 같은 틱에 같은 문구가 두 번 나와도 클라이언트가 둘을 구분한다.
    // 안내는 공개된 사건에만 나오므로 순번이 숨은 정보를 드러내지 않는다.
    public int Id { get; }
    // 안내가 나온 틱이다. 스냅샷에는 초(at)로 바꿔 보낸다.
    public long AtTick { get; }
    public string Text { get; }

    public Notice(int id, long atTick, string text)
    {
        Id = id;
        AtTick = atTick;
        Text = text;
    }
}
