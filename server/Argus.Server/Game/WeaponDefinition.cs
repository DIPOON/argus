namespace Argus.Server.Game;

// 무기 수치는 서버에서만 정한다. 클라이언트는 어떤 무기를 쓸지만 선택한다.
public sealed class WeaponDefinition
{
    public int Magazine { get; }
    public double ReloadSeconds { get; }
    public double ShotInterval { get; }
    public double BulletSpeed { get; }
    public double Damage { get; }
    public double Range { get; }
    public int Pellets { get; }
    public double Spread { get; }
    public int EnemyHits { get; }

    public WeaponDefinition(int magazine, double reloadSeconds, double shotInterval,
        double bulletSpeed, double damage, double range,
        int pellets = 1, double spread = 0, int enemyHits = 1)
    {
        Magazine = magazine;
        ReloadSeconds = reloadSeconds;
        ShotInterval = shotInterval;
        BulletSpeed = bulletSpeed;
        Damage = damage;
        Range = range;
        Pellets = pellets;
        Spread = spread;
        EnemyHits = enemyHits;
    }
}
