namespace Argus.Server.Game;

// All projectile counts, ranges, damage and cooldowns come from the server.
public sealed record WeaponDefinition(
    int Magazine, double ReloadSeconds, double ShotInterval, double BulletSpeed,
    double Damage, double Range, int Pellets = 1, double Spread = 0, int EnemyHits = 1);
