using System.Numerics;

namespace Argus.Server.Game;

public sealed class Player
{
    public string Id { get; }
    public string Name { get; set; }
    public string[] Slots { get; set; } = new string[] { "strike", "parry", "grab", "strike" };
    public Vector2 Position;
    public double Aim;
    public string State { get; set; } = "waiting";
    public bool HasDeployed;
    public double DeployAt;
    public Vector2 Landing;
    public double Hp;
    public double MaxHp;
    public int Mana;
    public int MaxMana;
    public double BusyUntil;
    public int RequestedSlot;
    public List<PlayerAction> Actions { get; } = new List<PlayerAction>();
    public double LastInputAt = -100;
    public long LastSequence = -1;
    public Vector2 Move;
    public long Connection;
    public bool Connected;
    public double? DisconnectedAt;
    public int Kills;
    public int Deaths;

    public Player(string id, string name)
    {
        Id = id;
        Name = name;
    }
}

public sealed class PlayerAction
{
    public int Id { get; }
    public SkillDefinition Skill { get; }
    public int Slot { get; }
    public double StartedAt { get; }
    public double ContactAt { get; }
    public double ActiveUntil { get; }
    public double EndsAt { get; }
    public bool Cancelled;
    public int? Target;
    public HashSet<int> JudgedEnemies { get; } = new HashSet<int>();
    public HashSet<int> FollowupHits { get; } = new HashSet<int>();
    public HashSet<int> HitFacilities { get; } = new HashSet<int>();
    public HashSet<int> HitWalls { get; } = new HashSet<int>();

    public PlayerAction(int id, SkillDefinition skill, int slot, double now)
    {
        Id = id;
        Skill = skill;
        Slot = slot;
        StartedAt = now;
        ContactAt = now + skill.ContactAfter;
        ActiveUntil = now + skill.ActiveSeconds;
        EndsAt = now + skill.Duration;
    }

    public bool IsActive(double now)
    {
        return !Cancelled && now >= StartedAt && now < ActiveUntil;
    }

    public bool IsFollowup(double now, double duration)
    {
        return !Cancelled && Skill.Id == "parry" && now >= ActiveUntil && now < ActiveUntil + duration;
    }
}

public sealed class EnemyAction
{
    public int Id { get; }
    public EnemySkill Skill { get; }
    public double Aim { get; }
    public double StartedAt { get; }
    public double ContactAt { get; }
    public double ActiveUntil { get; }
    public double EndsAt { get; }
    public HashSet<string> HitPlayers { get; } = new HashSet<string>();

    public EnemyAction(int id, EnemySkill skill, double aim, double now, double windupMultiplier = 1)
    {
        Id = id;
        Skill = skill;
        Aim = aim;
        StartedAt = now;
        // 판정과 클라이언트의 예고가 같은 시간을 사용하도록 실제 접촉 시각에 배율을 적용한다.
        ContactAt = now + skill.Windup * windupMultiplier;
        ActiveUntil = ContactAt + 0.25;
        EndsAt = ActiveUntil + 0.65;
    }

    public bool IsActive(double now)
    {
        // 적의 준비 동작에도 이미 상성 종류가 있다.
        return now >= StartedAt && now < ActiveUntil;
    }
}

public sealed class Enemy
{
    public int Id { get; }
    public Vector2 Position;
    public EnemyDefinition Definition { get; }
    public double Hp;
    public double NextActionAt;
    public EnemyAction? Action;

    public Enemy(int id, Vector2 position, EnemyDefinition definition)
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
    public Vector2 Position { get; }
    public double Hp = 4;
    public double MaxHp = 4;

    public Facility(int id, Vector2 position)
    {
        Id = id;
        Position = position;
    }
}

public sealed class Supply
{
    public int Id { get; }
    public Vector2 Position { get; }
    public bool Objective { get; }
    public bool Collected;
    public Dictionary<string, double> ReadyAt { get; } = new Dictionary<string, double>();

    public Supply(int id, Vector2 position, bool objective)
    {
        Id = id;
        Position = position;
        Objective = objective;
    }

    public double AvailableAt(Player player)
    {
        if (ReadyAt.TryGetValue(player.Id, out double ready))
        {
            return ready;
        }
        return 0;
    }
}

public sealed class Effect
{
    public int Id { get; }
    public double X { get; }
    public double Y { get; }
    public string Kind { get; }
    public double Until { get; }

    public Effect(int id, double x, double y, string kind, double until)
    {
        Id = id;
        X = x;
        Y = y;
        Kind = kind;
        Until = until;
    }
}

public sealed class Notice
{
    public double At { get; }
    public string Text { get; }

    public Notice(double at, string text)
    {
        At = at;
        Text = text;
    }
}
