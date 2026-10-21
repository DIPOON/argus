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

// 작전 하나의 맵과 임무 배치다. 맵 크기와 처음 벽, 캠프, 탈출 지점, 시설, 보급, 처음 배치하는 적의 자리, 증원 구역을 담는다.
// 진행 중에는 바뀌지 않는다. 진행 중 상태(벽 체력, 시설 체력, 먹은 보급)는 BattleMap과 Match가 따로 들고 있다.
public sealed class MissionLayout
{
    // 맵 한 변의 칸 수 범위다. 가장 큰 맵도 한 변이 128칸(6144)이라 좌표는 Raw로 2^25 미만이고,
    // 좌표끼리 곱해도 2^50, 제곱을 더해도 2^51이라 고정소수점이 JavaScript의 범위(2^53) 안에서 계산된다.
    public const int MinimumSize = 8;
    public const int MaximumSize = 128;

    public int Columns { get; }
    public int Rows { get; }
    // 칸마다의 처음 벽 종류다. 0은 빈 땅, 1은 부술 수 있는 벽, 2는 부술 수 없는 벽이다.
    public IReadOnlyList<byte> Tiles { get; }
    public FixedVector Camp { get; }
    public FixedVector Extraction { get; }
    public IReadOnlyList<FixedVector> Facilities { get; }
    public IReadOnlyList<SupplySpot> Supplies { get; }
    public IReadOnlyList<FixedVector> InitialEnemySpots { get; }
    public SpawnRing Reinforcements { get; }

    public MissionLayout(int columns, int rows, byte[] tiles, FixedVector camp, FixedVector extraction, FixedVector[] facilities,
        SupplySpot[] supplies, FixedVector[] initialEnemySpots, SpawnRing reinforcements)
    {
        if (columns < MinimumSize || columns > MaximumSize || rows < MinimumSize || rows > MaximumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), $"맵 크기 {columns}×{rows}가 {MinimumSize}~{MaximumSize}칸을 벗어납니다");
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
        // 받은 배열을 그대로 두면 바깥에서 바꿀 수 있으므로 복사해 둔다.
        Tiles = (byte[])tiles.Clone();
        Camp = camp;
        Extraction = extraction;
        Facilities = (FixedVector[])facilities.Clone();
        Supplies = (SupplySpot[])supplies.Clone();
        InitialEnemySpots = (FixedVector[])initialEnemySpots.Clone();
        Reinforcements = reinforcements;
    }

    public int Width
    {
        get
        {
            return Columns * BattleMap.Cell;
        }
    }

    public int Height
    {
        get
        {
            return Rows * BattleMap.Cell;
        }
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
            hasher.AddPoint(InitialEnemySpots[i]);
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
        // 셋 모두 가장 가까운 벽에서 60 넘게 떨어진 빈 땅이라, 가장 큰 적(철위병, 반지름 24)도 넉넉히 들어간다.
        // (970, 640)은 시설 1 아래 장애물의 남쪽이다.
        FixedVector[] initialEnemySpots = new FixedVector[]
        {
            FixedVector.FromInt(610, 730),
            FixedVector.FromInt(970, 640),
            FixedVector.FromInt(1260, 770)
        };
        // 맵 가운데(1080, 710)를 둘러싼 타원(가로 650, 세로 540)이다.
        SpawnRing reinforcements = new SpawnRing(FixedVector.FromInt(1080, 710), 650, 540);
        return new MissionLayout(columns, rows, tiles, camp, FixedVector.FromInt(1080, 1130), facilities, supplies,
            initialEnemySpots, reinforcements);
    }
}
