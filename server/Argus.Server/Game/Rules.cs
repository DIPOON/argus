namespace Argus.Server.Game;

// 밸런스 조정용 수치다. 게임 판정과 진행 규칙은 서버가 결정한다.
public sealed class Rules
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
    public double ShotInterval { get; init; } = 0.14;
    public double BulletSpeed { get; init; } = 1050;
    public double RifleDamage { get; init; } = 24;
    public double GrenadeDamage { get; init; } = 150;
    public double GrenadeRadius { get; init; } = 155;
    public double TurretCooldown { get; init; } = 20;
    public double TurretLifetime { get; init; } = 45;
    public double TurretHealth { get; init; } = 100;
    public double TurretRange { get; init; } = 360;
    public double TurretDamage { get; init; } = 16;
    public double TurretShotInterval { get; init; } = 0.25;
    public const float TurretPlacementDistance = 44;

    private static readonly WeaponDefinition Shotgun =
        new WeaponDefinition(6, 2.6, 0.75, 850, 12, 300, pellets: 7, spread: 0.42);
    private static readonly WeaponDefinition Piercer =
        new WeaponDefinition(8, 2.8, 0.7, 1400, 60, 1155, enemyHits: 3);

    public static bool ValidWeapon(string weapon)
    {
        return weapon == "rifle" || weapon == "shotgun" || weapon == "piercer";
    }

    public static bool ValidSecondary(string secondary)
    {
        return secondary == "grenade" || secondary == "turret";
    }

    public WeaponDefinition Weapon(string weapon)
    {
        if (weapon == "rifle")
        {
            return new WeaponDefinition(Magazine, ReloadSeconds, ShotInterval,
                BulletSpeed, RifleDamage, BulletSpeed * 1.1);
        }
        if (weapon == "shotgun")
        {
            return Shotgun;
        }
        if (weapon == "piercer")
        {
            return Piercer;
        }
        throw new ArgumentOutOfRangeException(nameof(weapon));
    }
}
