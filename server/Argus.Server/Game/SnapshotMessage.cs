namespace Argus.Server.Game;

// 클라이언트에 보내는 데이터 구조다. JSON으로 바꿀 때 속성 이름은 camelCase가 된다.
public sealed class SnapshotMessage
{
    public string Type { get; set; } = "snapshot";
    public int Version { get; set; } = 1;
    public string Room { get; set; } = "";
    public double Now { get; set; }
    public double Elapsed { get; set; }
    public string Phase { get; set; } = "";
    public string? Result { get; set; }
    public string You { get; set; } = "";
    public int Pulse { get; set; }
    public double NextPulse { get; set; }
    public ExtractionSnapshot Extraction { get; set; } = new ExtractionSnapshot();
    public byte[] Walls { get; set; } = Array.Empty<byte>();
    public int MapRevision { get; set; }
    public List<PlayerSnapshot> Players { get; set; } = new List<PlayerSnapshot>();
    public List<EnemySnapshot> Enemies { get; set; } = new List<EnemySnapshot>();
    public List<BulletSnapshot> Bullets { get; set; } = new List<BulletSnapshot>();
    public List<GrenadeSnapshot> Grenades { get; set; } = new List<GrenadeSnapshot>();
    public List<Effect> Effects { get; set; } = new List<Effect>();
    public List<List<double>> Sight { get; set; } = new List<List<double>>();
    public List<TurretSnapshot> Turrets { get; set; } = new List<TurretSnapshot>();
    public List<FacilitySnapshot> Facilities { get; set; } = new List<FacilitySnapshot>();
    public List<SupplySnapshot> Supplies { get; set; } = new List<SupplySnapshot>();
    public List<Notice> Notices { get; set; } = new List<Notice>();
    public MapSnapshot Map { get; set; } = new MapSnapshot();
    public RulesSnapshot Rules { get; set; } = new RulesSnapshot();
}

public sealed class PlayerSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Passive { get; set; } = "";
    public string Weapon { get; set; } = "";
    public string Secondary { get; set; } = "";
    public string State { get; set; } = "";
    public bool Connected { get; set; }
    public bool HasDeployed { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Aim { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public int Ammo { get; set; }
    public int Grenades { get; set; }
    public double Reload { get; set; }
    public int Magazine { get; set; }
    public double ReloadSeconds { get; set; }
    public double TurretCooldown { get; set; }
    public double Deploy { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public long Ack { get; set; }
    public double LandingX { get; set; }
    public double LandingY { get; set; }
}

public sealed class EnemySnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Kind { get; set; } = "";
    public double Hp { get; set; }
}

public sealed class BulletSnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public bool Hostile { get; set; }
    public string Kind { get; set; } = "";
}

public sealed class GrenadeSnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Remaining { get; set; }
}

public sealed class TurretSnapshot
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Aim { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public double Remaining { get; set; }
}

public sealed class FacilitySnapshot
{
    public int Id { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public double Hp { get; set; }
    public int MaxHp { get; set; } = 340;
}

public sealed class SupplySnapshot
{
    public int Id { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public bool Objective { get; set; }
    public bool Collected { get; set; }
    public double Cooldown { get; set; }
}

public sealed class ExtractionSnapshot
{
    public float X { get; set; }
    public float Y { get; set; }
    public double Progress { get; set; }
    public double Duration { get; set; }
    public bool Unlocked { get; set; }
}

public sealed class MapSnapshot
{
    public int Width { get; set; } = BattleMap.Width;
    public int Height { get; set; } = BattleMap.Height;
    public int Cell { get; set; } = BattleMap.Cell;
    public int Columns { get; set; } = BattleMap.Columns;
    public int Rows { get; set; } = BattleMap.Rows;
    public float CampX { get; set; } = BattleMap.Camp.X;
    public float CampY { get; set; } = BattleMap.Camp.Y;
}

public sealed class RulesSnapshot
{
    public double Deploy { get; set; }
    public double Reload { get; set; }
    public int Magazine { get; set; }
    public int Grenades { get; set; }
    public double Speed { get; set; }
    public float TurretPlacement { get; set; }
    public double TurretCooldown { get; set; }
}
