using System.Numerics;

namespace Argus.Server.Game;

public sealed class Player(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; set; } = name;
    public string Passive { get; set; } = "vitality";
    public Vector2 Position;
    public Vector2 Impulse;
    public double Aim;
    public string State { get; set; } = "waiting";
    public bool HasDeployed;
    public double DeployAt;
    public Vector2 Landing;
    public double Hp;
    public double MaxHp => Passive == "vitality" ? 130 : 100;
    public int Ammo;
    public int Grenades;
    public double ReloadUntil;
    public double NextShot;
    public double NextGrenade;
    public double LastInputAt = -100;
    public long LastSequence = -1;
    public Vector2 Move;
    public bool Firing;
    public bool ReloadRequested;
    public bool GrenadeRequested;
    public long Connection;
    public bool Connected;
    public double? DisconnectedAt;
    public int Kills;
    public int Deaths;
}

public sealed class Enemy(int id, Vector2 position, string kind)
{
    public int Id { get; } = id;
    public Vector2 Position = position;
    public string Kind { get; } = kind;
    public double Hp = kind == "ranged" ? 58 : 42;
    public double NextAttack;
    public Vector2 Impulse;
}

public sealed class Bullet(int id, Vector2 position, Vector2 velocity, string? owner, double damage)
{
    public int Id { get; } = id;
    public Vector2 Position = position;
    public Vector2 Velocity = velocity;
    public string? Owner { get; } = owner;
    public double Damage { get; } = damage;
    public double Remaining = 1.1;
}

public sealed class Grenade(int id, Vector2 position, Vector2 velocity, string owner)
{
    public int Id { get; } = id;
    public Vector2 Position = position;
    public Vector2 Velocity = velocity;
    public string Owner { get; } = owner;
    public double Remaining = 1.1;
}

public sealed class Facility(int id, Vector2 position)
{
    public int Id { get; } = id;
    public Vector2 Position { get; } = position;
    public double Hp = 340;
}

public sealed class Supply(int id, Vector2 position, bool objective)
{
    public int Id { get; } = id;
    public Vector2 Position { get; } = position;
    public bool Objective { get; } = objective;
    public bool Collected;
    public double AvailableAt;
}

public sealed record Effect(int Id, double X, double Y, string Kind, double Until);
public sealed record Notice(double At, string Text);
