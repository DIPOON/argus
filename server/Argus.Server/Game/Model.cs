using System.Numerics;

namespace Argus.Server.Game;

public sealed class Player
{
    public string Id { get; }
    public string Name { get; set; }
    public string Passive { get; set; } = "vitality";
    public string Weapon { get; set; } = "rifle";
    public string Secondary { get; set; } = "grenade";
    public Vector2 Position;
    public Vector2 Impulse;
    public double Aim;
    public string State { get; set; } = "waiting";
    public bool HasDeployed;
    public double DeployAt;
    public Vector2 Landing;
    public double Hp;

    public double MaxHp
    {
        get
        {
            if (Passive == "vitality")
            {
                return 130;
            }
            return 100;
        }
    }

    public int Ammo;
    public int Grenades;
    public double ReloadUntil;
    public double NextShot;
    public double NextGrenade;
    public double NextTurret;
    public double LastInputAt = -100;
    public long LastSequence = -1;
    public Vector2 Move;
    public bool Firing;
    public bool ReloadRequested;
    public bool SecondaryRequested;
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

public sealed class Enemy
{
    public int Id { get; }
    public Vector2 Position;
    public string Kind { get; }
    public double Hp;
    public double NextAttack;
    public Vector2 Impulse;

    public Enemy(int id, Vector2 position, string kind)
    {
        Id = id;
        Position = position;
        Kind = kind;
        if (kind == "ranged")
        {
            Hp = 58;
        }
        else
        {
            Hp = 42;
        }
    }
}

public sealed class Bullet
{
    public int Id { get; }
    public Vector2 Position;
    public Vector2 Velocity;
    public string? Owner { get; }
    public double Damage { get; }
    public double Remaining = 1.1;
    public string Kind { get; init; } = "rifle";
    public int EnemyHitsLeft = 1;
    public HashSet<int> HitEnemies { get; } = new HashSet<int>();
    public int? SourceTurret { get; init; }

    public Bullet(int id, Vector2 position, Vector2 velocity, string? owner, double damage)
    {
        Id = id;
        Position = position;
        Velocity = velocity;
        Owner = owner;
        Damage = damage;
    }
}

public sealed class Turret
{
    public int Id { get; }
    public Vector2 Position { get; }
    public string Owner { get; }
    public double Hp;
    public double MaxHp { get; }
    public double ExpiresAt { get; }
    public double Aim;
    public double NextShot;

    public Turret(int id, Vector2 position, string owner, double health, double expiresAt)
    {
        Id = id;
        Position = position;
        Owner = owner;
        Hp = health;
        MaxHp = health;
        ExpiresAt = expiresAt;
    }
}

public sealed class Grenade
{
    public int Id { get; }
    public Vector2 Position;
    public Vector2 Velocity;
    public string Owner { get; }
    public double Remaining = 1.1;

    public Grenade(int id, Vector2 position, Vector2 velocity, string owner)
    {
        Id = id;
        Position = position;
        Velocity = velocity;
        Owner = owner;
    }
}

public sealed class Facility
{
    public int Id { get; }
    public Vector2 Position { get; }
    public double Hp = 340;

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
    public double AvailableAt;

    public Supply(int id, Vector2 position, bool objective)
    {
        Id = id;
        Position = position;
        Objective = objective;
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
