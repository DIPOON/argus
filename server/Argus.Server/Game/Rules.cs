namespace Argus.Server.Game;

// Prototype balance lives here. Authority and lifecycle rules do not depend on clients.
public sealed record Rules
{
    public const double Step = 1.0 / 30;
    public const int MaxPlayers = 4;
    public double DeploySeconds { get; init; } = 5;
    public double ReloadSeconds { get; init; } = 2;
    public double PulseSeconds { get; init; } = 600;
    public double ExtractionSeconds { get; init; } = 20;
    public double ReconnectSeconds { get; init; } = 120;
    public double SpawnInterval { get; init; } = 3.2;
    public int MaxEnemies { get; init; } = 180;
    public int Magazine { get; init; } = 30;
    public int Grenades { get; init; } = 3;
    public double PlayerSpeed { get; init; } = 180;
    public double Vision { get; init; } = 410;
    public double ShotInterval { get; init; } = .14;
    public double BulletSpeed { get; init; } = 1050;
    public double RifleDamage { get; init; } = 24;
    public double GrenadeDamage { get; init; } = 150;
    public double GrenadeRadius { get; init; } = 155;
}
