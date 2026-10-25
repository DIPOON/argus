namespace Argus.Server.Game;

// 보급 상자의 자리와 부목표인지다. 부목표 보급은 탈출 조건에 들고 한 번만 먹을 수 있다.
public sealed class SupplySpot
{
    public FixedVector Position { get; }
    public bool Objective { get; }

    public SupplySpot(FixedVector position, bool objective)
    {
        Position = position;
        Objective = objective;
    }
}

// 처음 배치하는 적의 자리와 종류다. 엘리트를 끈 작전(Rules.AllowEliteEnemies가 false)에서는 엘리트 자리에도 병사를 둔다.
public sealed class EnemySpot
{
    public FixedVector Position { get; }
    public EnemyDefinition Kind { get; }

    public EnemySpot(FixedVector position, EnemyDefinition kind)
    {
        Position = position;
        Kind = kind;
    }
}

// 증원이 생기는 타원이다. 증원은 이 타원 위의 무작위 각도에서, 분대가 보지 않고 막히지 않은 자리에 생긴다.
public sealed class SpawnRing
{
    public FixedVector Center { get; }
    public int RadiusX { get; }
    public int RadiusY { get; }

    public SpawnRing(FixedVector center, int radiusX, int radiusY)
    {
        Center = center;
        RadiusX = radiusX;
        RadiusY = radiusY;
    }
}

// 작전 하나의 맵과 임무 배치다. 맵 크기와 처음 벽, 캠프, 탈출 지점, 시설, 보급, 처음 배치하는 적, 증원 구역을 담는다.
// 진행 중에는 바뀌지 않는다. 진행 중 상태(벽 체력, 시설 체력, 먹은 보급)는 BattleMap과 Match가 따로 들고 있다.
// 생성기가 만든 배치도 받으므로 생성자가 맵 안에 있는지, 설 수 있는지를 검사한다. 고정소수점 계산이 2^53 안에 머문다는
// 근거(아래 MaximumSize)도 좌표가 맵 안이라는 데 기댄다.
public sealed class MissionLayout
{
    // 맵 한 변의 칸 수 범위다. 가장 큰 맵도 한 변이 128칸(6144)이라 좌표는 Raw로 2^25 미만이고,
    // 좌표끼리 곱해도 2^50, 제곱을 더해도 2^51이라 고정소수점이 JavaScript의 범위(2^53) 안에서 계산된다.
    public const int MinimumSize = 8;
    public const int MaximumSize = 128;
    // 증원 타원에서 고르게 살펴보는 점의 수다. 이 가운데 하나라도 가장 큰 적이 설 수 있어야 증원이 나올 수 있다.
    private const int RingSamples = 32;

    public int Columns { get; }
    public int Rows { get; }
    // 칸마다의 처음 벽 종류다. 0은 빈 땅, 1은 부술 수 있는 벽, 2는 부술 수 없는 벽이다.
    public IReadOnlyList<byte> Tiles { get; }
    public FixedVector Camp { get; }
    public FixedVector Extraction { get; }
    public IReadOnlyList<FixedVector> Facilities { get; }
    public IReadOnlyList<SupplySpot> Supplies { get; }
    public IReadOnlyList<EnemySpot> InitialEnemySpots { get; }
    public SpawnRing Reinforcements { get; }

    public MissionLayout(int columns, int rows, byte[] tiles, FixedVector camp, FixedVector extraction, FixedVector[] facilities,
        SupplySpot[] supplies, EnemySpot[] initialEnemySpots, SpawnRing reinforcements)
    {
        if (columns < MinimumSize || columns > MaximumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), columns, $"맵 가로 칸 수가 {MinimumSize}~{MaximumSize}를 벗어납니다");
        }
        if (rows < MinimumSize || rows > MaximumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, $"맵 세로 칸 수가 {MinimumSize}~{MaximumSize}를 벗어납니다");
        }
        if (tiles.Length != columns * rows)
        {
            throw new ArgumentException($"칸 수 {tiles.Length}가 맵 크기 {columns}×{rows}와 맞지 않습니다", nameof(tiles));
        }
        for (int i = 0; i < tiles.Length; i++)
        {
            if (tiles[i] > 2)
            {
                throw new ArgumentException($"{i}번 칸의 벽 종류 {tiles[i]}를 모릅니다", nameof(tiles));
            }
        }
        Columns = columns;
        Rows = rows;
        // 받은 배열을 복사하고 읽기 전용으로 감싼다. 바깥에서 배열을 바꾸거나 형변환해 고칠 수 없게 해,
        // 같은 배치로 만든 방들이 늘 같은 벽에서 시작하게 한다.
        Tiles = Array.AsReadOnly((byte[])tiles.Clone());
        Camp = camp;
        Extraction = extraction;
        Facilities = Array.AsReadOnly((FixedVector[])facilities.Clone());
        Supplies = Array.AsReadOnly((SupplySpot[])supplies.Clone());
        InitialEnemySpots = Array.AsReadOnly((EnemySpot[])initialEnemySpots.Clone());
        Reinforcements = reinforcements;
        Validate();
    }

    // 자리가 모두 맵 안인지, 대원이 서야 하는 곳(캠프, 탈출 지점)에 설 수 있는지, 시설과 보급이 벽 칸이 아닌지 본다.
    // 처음 적은 몸 크기만큼 설 수 있고 서로 겹치지 않는지까지 본다. 그래서 검사를 통과한 배치로는 언제나 방을 만들 수 있다.
    // Match 생성자도 처음 적을 둘 때 다시 확인한다(Match.PlaceInitialEnemy).
    private void Validate()
    {
        BattleMap map = new BattleMap(this);
        RequireStandable(map, Camp, nameof(Camp), "캠프");
        RequireStandable(map, Extraction, nameof(Extraction), "탈출 지점");
        for (int i = 0; i < Facilities.Count; i++)
        {
            RequireOpen(map, Facilities[i], nameof(Facilities), $"시설 {i + 1}");
        }
        for (int i = 0; i < Supplies.Count; i++)
        {
            RequireOpen(map, Supplies[i].Position, nameof(Supplies), $"보급 {i + 1}");
        }
        ValidateEnemySpots(map);
        ValidateRing(map);
    }

    private static void RequireInside(BattleMap map, FixedVector position, string name, string label)
    {
        if (position.X < Fixed.Zero || position.Y < Fixed.Zero || position.X > Fixed.FromInt(map.Width) || position.Y > Fixed.FromInt(map.Height))
        {
            throw new ArgumentException($"{label} {position}가 맵({map.Width}×{map.Height}) 밖입니다", name);
        }
    }

    private static void RequireStandable(BattleMap map, FixedVector position, string name, string label)
    {
        RequireInside(map, position, name, label);
        if (!map.CanStand(position, Rules.PlayerRadius))
        {
            throw new ArgumentException($"{label} {position}에 대원이 설 수 없습니다", name);
        }
    }

    private static void RequireOpen(BattleMap map, FixedVector position, string name, string label)
    {
        RequireInside(map, position, name, label);
        int x = position.X.FloorDivide(BattleMap.Cell);
        int y = position.Y.FloorDivide(BattleMap.Cell);
        if (map.Solid(x, y))
        {
            throw new ArgumentException($"{label} {position}가 벽 칸 안입니다", name);
        }
    }

    // 처음 적은 그 종류의 몸 반지름만큼 벽에 닿지 않고 설 수 있어야 하고, 두 적의 거리는 반지름의 합 이상이어야 한다.
    // 엘리트를 끈 작전에서는 엘리트 자리에 몸이 가장 작은 병사를 두므로, 정해 둔 종류로 확인하면 충분하다.
    private void ValidateEnemySpots(BattleMap map)
    {
        for (int i = 0; i < InitialEnemySpots.Count; i++)
        {
            EnemySpot spot = InitialEnemySpots[i];
            RequireInside(map, spot.Position, nameof(InitialEnemySpots), $"처음 적 자리 {i + 1}");
            if (!map.CanStand(spot.Position, spot.Kind.Radius))
            {
                throw new ArgumentException($"처음 적 자리 {i + 1} {spot.Position}에 {spot.Kind.Id}의 몸(반지름 {spot.Kind.Radius})이 들어가지 않습니다",
                    nameof(InitialEnemySpots));
            }
            for (int j = 0; j < i; j++)
            {
                EnemySpot other = InitialEnemySpots[j];
                Fixed minimum = spot.Kind.Radius + other.Kind.Radius;
                if (FixedVector.DistanceSquared(spot.Position, other.Position) < minimum * minimum)
                {
                    throw new ArgumentException($"처음 적 자리 {j + 1} {other.Position}과 {i + 1} {spot.Position}의 몸이 겹칩니다",
                        nameof(InitialEnemySpots));
                }
            }
        }
    }

    // 증원 타원은 반지름이 1 이상이고 맵 안에 들어가야 한다. 타원 위에 가장 큰 적이 설 수 있는 점이 하나도 없으면
    // 증원이 아무 알림 없이 영영 나오지 않으므로, 처음 배치처럼 결함으로 보고 거절한다.
    private void ValidateRing(BattleMap map)
    {
        SpawnRing ring = Reinforcements;
        if (ring.RadiusX < 1 || ring.RadiusY < 1)
        {
            throw new ArgumentException($"증원 타원의 반지름 {ring.RadiusX}×{ring.RadiusY}는 1 이상이어야 합니다", nameof(Reinforcements));
        }
        FixedVector reach = FixedVector.FromInt(ring.RadiusX, ring.RadiusY);
        RequireInside(map, ring.Center - reach, nameof(Reinforcements), "증원 타원의 왼쪽 위");
        RequireInside(map, ring.Center + reach, nameof(Reinforcements), "증원 타원의 오른쪽 아래");
        Fixed largest = Fixed.Max(EnemyDefinition.Grunt.Radius, Fixed.Max(EnemyDefinition.Breaker.Radius, EnemyDefinition.Warden.Radius));
        for (int i = 0; i < RingSamples; i++)
        {
            FixedVector direction = Angle.FromFraction(i, RingSamples).Direction();
            FixedVector point = new FixedVector(ring.Center.X + direction.X * ring.RadiusX, ring.Center.Y + direction.Y * ring.RadiusY);
            if (map.CanStand(point, largest))
            {
                return;
            }
        }
        throw new ArgumentException("증원 타원 위에 적이 설 수 있는 자리가 없습니다", nameof(Reinforcements));
    }

    // 상태 요약에 배치를 적는다. 진행 중에 바뀌지 않지만, 두 방이 같은 배치로 시작했는지도 요약값으로 확인한다.
    // 처음 벽은 BattleMap이 지금 벽과 함께 적고, 시설과 보급은 Match가 진행 상태와 함께 적는다.
    public void AddState(StateHasher hasher)
    {
        hasher.AddInt(Columns);
        hasher.AddInt(Rows);
        hasher.AddPoint(Camp);
        hasher.AddPoint(Extraction);
        hasher.AddInt(InitialEnemySpots.Count);
        for (int i = 0; i < InitialEnemySpots.Count; i++)
        {
            hasher.AddPoint(InitialEnemySpots[i].Position);
            hasher.AddText(InitialEnemySpots[i].Kind.Id);
        }
        hasher.AddPoint(Reinforcements.Center);
        hasher.AddInt(Reinforcements.RadiusX);
        hasher.AddInt(Reinforcements.RadiusY);
    }

    // 처음 손으로 만든 40×30 맵이다. 운영 방은 생성한 맵을 쓰고, 이 맵은 검사와 재현에 쓴다.
    // 맵 가운데의 시설 구역을 부술 수 있는 벽이 둘러싸고, 네 방향에 출입구가 있다. 캠프는 왼쪽, 탈출 지점은 아래쪽이다.
    public static MissionLayout CreateHandmade()
    {
        int columns = 40;
        int rows = 30;
        byte[] tiles = new byte[columns * rows];
        // 맵 가장자리는 부술 수 없는 벽이다.
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (x == 0 || y == 0 || x == columns - 1 || y == rows - 1)
                {
                    tiles[y * columns + x] = 2;
                }
            }
        }

        // 임무 시설을 둘러싼 벽을 만든다. 맵 안쪽 벽은 모두 부술 수 있다.
        for (int x = 15; x <= 29; x++)
        {
            tiles[7 * columns + x] = 1;
            tiles[21 * columns + x] = 1;
        }
        for (int y = 7; y <= 21; y++)
        {
            tiles[y * columns + 15] = 1;
            tiles[y * columns + 29] = 1;
        }

        // 벽 일부를 지워 출입구를 만든다.
        for (int y = 13; y <= 15; y++)
        {
            tiles[y * columns + 15] = 0;
            tiles[y * columns + 29] = 0;
        }
        for (int x = 21; x <= 23; x++)
        {
            tiles[7 * columns + x] = 0;
            tiles[21 * columns + x] = 0;
        }

        int[,] obstacles = new int[,]
        {
            { 19, 11 }, { 20, 11 }, { 25, 12 }, { 25, 13 },
            { 20, 17 }, { 21, 17 }, { 26, 18 }, { 10, 10 },
            { 10, 11 }, { 10, 18 }, { 11, 18 }, { 32, 10 },
            { 32, 11 }, { 33, 19 }
        };
        for (int i = 0; i < obstacles.GetLength(0); i++)
        {
            tiles[obstacles[i, 1] * columns + obstacles[i, 0]] = 1;
        }

        FixedVector camp = FixedVector.FromInt(300, 730);
        FixedVector[] facilities = new FixedVector[]
        {
            FixedVector.FromInt(960, 485),
            FixedVector.FromInt(1210, 695),
            FixedVector.FromInt(990, 920)
        };
        SupplySpot[] supplies = new SupplySpot[]
        {
            new SupplySpot(camp + FixedVector.FromInt(70, 40), false),
            new SupplySpot(FixedVector.FromInt(845, 640), true),
            new SupplySpot(FixedVector.FromInt(1150, 835), true),
            new SupplySpot(FixedVector.FromInt(815, 985), false),
            new SupplySpot(FixedVector.FromInt(1080, 740), false)
        };
        // 셋 모두 가장 가까운 벽에서 60 넘게 떨어진 빈 땅이라, 가장 큰 적(철위병, 반지름 23)도 넉넉히 들어간다.
        // (970, 640)은 시설 1 아래 장애물의 남쪽이다. 엘리트를 켠 작전에서는 뒤의 둘이 분쇄자와 철위병이다.
        EnemySpot[] initialEnemySpots = new EnemySpot[]
        {
            new EnemySpot(FixedVector.FromInt(610, 730), EnemyDefinition.Grunt),
            new EnemySpot(FixedVector.FromInt(970, 640), EnemyDefinition.Breaker),
            new EnemySpot(FixedVector.FromInt(1260, 770), EnemyDefinition.Warden)
        };
        // 맵 가운데(1080, 710)를 둘러싼 타원(가로 650, 세로 540)이다.
        SpawnRing reinforcements = new SpawnRing(FixedVector.FromInt(1080, 710), 650, 540);
        return new MissionLayout(columns, rows, tiles, camp, FixedVector.FromInt(1080, 1130), facilities, supplies,
            initialEnemySpots, reinforcements);
    }
}
