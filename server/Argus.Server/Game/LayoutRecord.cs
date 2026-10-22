namespace Argus.Server.Game;

// 기록 머리에 적는 작전 배치다. 재생할 때 이것으로 같은 배치를 다시 만든다. 좌표는 고정소수점의 Raw다.
// 처음 적 자리와 증원 타원은 숨은 정보지만, 기록 파일은 시드처럼 서버 밖으로 내보내지 않는다(CLAUDE.md "서버 권위와 시야").
public sealed class LayoutRecord
{
    public int Columns { get; set; }
    public int Rows { get; set; }
    // 칸마다의 처음 벽 종류다. JSON에는 Base64 글자로 적힌다.
    public byte[] Tiles { get; set; } = Array.Empty<byte>();
    public PointRecord Camp { get; set; } = new PointRecord();
    public PointRecord Extraction { get; set; } = new PointRecord();
    public List<PointRecord> Facilities { get; set; } = new List<PointRecord>();
    public List<SupplyRecord> Supplies { get; set; } = new List<SupplyRecord>();
    public List<EnemySpotRecord> InitialEnemies { get; set; } = new List<EnemySpotRecord>();
    public PointRecord ReinforcementCenter { get; set; } = new PointRecord();
    public int ReinforcementRadiusX { get; set; }
    public int ReinforcementRadiusY { get; set; }

    public static LayoutRecord From(MissionLayout layout)
    {
        LayoutRecord record = new LayoutRecord();
        record.Columns = layout.Columns;
        record.Rows = layout.Rows;
        record.Tiles = new byte[layout.Tiles.Count];
        for (int i = 0; i < layout.Tiles.Count; i++)
        {
            record.Tiles[i] = layout.Tiles[i];
        }
        record.Camp = PointRecord.From(layout.Camp);
        record.Extraction = PointRecord.From(layout.Extraction);
        for (int i = 0; i < layout.Facilities.Count; i++)
        {
            record.Facilities.Add(PointRecord.From(layout.Facilities[i]));
        }
        for (int i = 0; i < layout.Supplies.Count; i++)
        {
            SupplyRecord supply = new SupplyRecord();
            supply.X = layout.Supplies[i].Position.X.Raw;
            supply.Y = layout.Supplies[i].Position.Y.Raw;
            supply.Objective = layout.Supplies[i].Objective;
            record.Supplies.Add(supply);
        }
        for (int i = 0; i < layout.InitialEnemySpots.Count; i++)
        {
            EnemySpotRecord spot = new EnemySpotRecord();
            spot.X = layout.InitialEnemySpots[i].Position.X.Raw;
            spot.Y = layout.InitialEnemySpots[i].Position.Y.Raw;
            spot.Kind = layout.InitialEnemySpots[i].Kind.Id;
            record.InitialEnemies.Add(spot);
        }
        record.ReinforcementCenter = PointRecord.From(layout.Reinforcements.Center);
        record.ReinforcementRadiusX = layout.Reinforcements.RadiusX;
        record.ReinforcementRadiusY = layout.Reinforcements.RadiusY;
        return record;
    }

    // 적어 둔 값으로 배치를 다시 만든다. 배치의 생성자가 처음 만들 때와 같은 검사를 다시 한다.
    public MissionLayout ToLayout()
    {
        FixedVector[] facilities = new FixedVector[Facilities.Count];
        for (int i = 0; i < Facilities.Count; i++)
        {
            facilities[i] = Facilities[i].ToPoint();
        }
        SupplySpot[] supplies = new SupplySpot[Supplies.Count];
        for (int i = 0; i < Supplies.Count; i++)
        {
            FixedVector position = new FixedVector(Fixed.FromRaw(Supplies[i].X), Fixed.FromRaw(Supplies[i].Y));
            supplies[i] = new SupplySpot(position, Supplies[i].Objective);
        }
        EnemySpot[] enemies = new EnemySpot[InitialEnemies.Count];
        for (int i = 0; i < InitialEnemies.Count; i++)
        {
            FixedVector position = new FixedVector(Fixed.FromRaw(InitialEnemies[i].X), Fixed.FromRaw(InitialEnemies[i].Y));
            enemies[i] = new EnemySpot(position, EnemyDefinition.FromId(InitialEnemies[i].Kind));
        }
        SpawnRing ring = new SpawnRing(ReinforcementCenter.ToPoint(), ReinforcementRadiusX, ReinforcementRadiusY);
        return new MissionLayout(Columns, Rows, Tiles, Camp.ToPoint(), Extraction.ToPoint(), facilities, supplies, enemies, ring);
    }
}

public sealed class PointRecord
{
    public long X { get; set; }
    public long Y { get; set; }

    public static PointRecord From(FixedVector point)
    {
        PointRecord record = new PointRecord();
        record.X = point.X.Raw;
        record.Y = point.Y.Raw;
        return record;
    }

    public FixedVector ToPoint()
    {
        return new FixedVector(Fixed.FromRaw(X), Fixed.FromRaw(Y));
    }
}

public sealed class SupplyRecord
{
    public long X { get; set; }
    public long Y { get; set; }
    public bool Objective { get; set; }
}

public sealed class EnemySpotRecord
{
    public long X { get; set; }
    public long Y { get; set; }
    // 적 정의의 id(grunt·breaker·warden)다.
    public string Kind { get; set; } = "";
}
