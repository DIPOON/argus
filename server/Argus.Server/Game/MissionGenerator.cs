namespace Argus.Server.Game;

// 방의 비밀 시드로 작전 배치를 만든다. 맵 크기, 벽, 캠프, 탈출 지점, 시설 세 곳, 보급 다섯 개, 처음 적 셋, 증원 타원을 정한다.
// 같은 시드면 어느 기계에서나 같은 배치가 나온다. 정수 계산과 SeededRandom만 쓴다(결정론 규칙).
// 진행 난수와 같은 순번을 쓰지 않도록 용도별 시드("mission-layout")로 따로 뽑는다(SeededRandom.Derive).
//
// 만드는 순서:
// 1. 크기를 정하고 가장자리를 부술 수 없는 벽으로 두른다.
// 2. 캠프를 한 변 가까이에, 탈출 지점을 맞은편 변 가까이에 두고 둘레를 비워 둔다.
// 3. 시설 세 곳을 캠프에서 멀리, 서로 떨어지게 두고 부술 수 있는 벽으로 두른 뒤 출입구 두 개를 낸다.
// 4. 처음 적을 시설 근처에, 보급을 캠프 옆과 들판에 둔다. 자리마다 둘레 한 칸을 비워 둔다.
// 5. 남은 땅에 부술 수 있는 장애물을 서로, 그리고 다른 벽과 닿지 않게 흩어 놓는다. 그래서 통로가 막히지 않는다.
// 6. 캠프에서 모든 목표와 증원 타원 대부분에 걸어서 갈 수 있는지 확인한다. 안 되면 처음부터 다시 만든다.
public static class MissionGenerator
{
    public const int MinimumColumns = 34;
    public const int MaximumColumns = 50;
    public const int MinimumRows = 26;
    public const int MaximumRows = 38;
    public const int FacilityCount = 3;
    // 캠프와 탈출 지점을 변에서 몇 칸 안쪽에 둘지와, 둘레를 몇 칸까지 비울지다. 캠프 둘레는 투입하고 바로 움직일 수 있게 넓게 비운다.
    private const int CampInset = 5;
    private const int CampClearance = 4;
    private const int ExtractionInset = 4;
    private const int ExtractionClearance = 3;
    // 시설을 두르는 벽은 가운데에서 3칸 떨어진 7×7칸 테두리이고, 안쪽 5×5칸은 비어 있다. 벽 바깥 한 칸도 통로로 비워 둔다.
    private const int EnclosureHalf = 3;
    // 시설과 캠프, 시설끼리 떨어져야 하는 칸 수다.
    private const int FacilityFromCamp = 11;
    private const int FacilityApart = 9;
    // 처음 적은 시설 가운데에서 5~7칸 떨어진 곳에 두고, 캠프에서는 8칸 넘게 떨어뜨린다(캠프 시야 밖).
    private const int GuardNear = 5;
    private const int GuardFar = 7;
    private const int EnemyFromCamp = 8;
    // 증원 타원은 가장자리에서 3칸 안쪽을 지난다.
    private const int RingInset = 3;
    // 자리 하나를 찾을 때의 시도 횟수와, 배치 전체를 처음부터 다시 만드는 횟수다.
    private const int PlacementTries = 400;
    private const int LayoutAttempts = 80;
    // 증원 타원 위에서 고르게 살펴보는 점의 수와, 그중 캠프에서 걸어서 갈 수 있어야 하는 비율(%)이다.
    private const int RingSamples = 64;
    private const int RingReachablePercent = 60;
    // 장애물은 맵 안쪽 칸 이만큼마다 하나다(40×30 맵이면 약 19개).
    private const int CellsPerObstacle = 55;

    public static MissionLayout Generate(byte[] roomSeed)
    {
        SeededRandom random = new SeededRandom(SeededRandom.Derive(roomSeed, "mission-layout"));
        int columns = random.NextInt(MinimumColumns, MaximumColumns + 1);
        int rows = random.NextInt(MinimumRows, MaximumRows + 1);
        for (int attempt = 0; attempt < LayoutAttempts; attempt++)
        {
            MissionLayout? layout = TryGenerate(random, columns, rows);
            if (layout != null)
            {
                return layout;
            }
        }
        throw new InvalidOperationException($"{columns}×{rows} 작전 배치를 {LayoutAttempts}번 시도해도 만들지 못했습니다");
    }

    // 칸 좌표다.
    private readonly struct Cell
    {
        public int X { get; }
        public int Y { get; }

        public Cell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int DistanceSquared(Cell other)
        {
            int dx = X - other.X;
            int dy = Y - other.Y;
            return dx * dx + dy * dy;
        }

        // 칸 가운데의 좌표다.
        public FixedVector Center()
        {
            return FixedVector.FromInt(X * BattleMap.Cell + BattleMap.Cell / 2, Y * BattleMap.Cell + BattleMap.Cell / 2);
        }
    }

    // 만들고 있는 맵이다. 칸마다 벽 종류와, 장애물을 두지 않고 비워 둘 칸인지를 둔다.
    private sealed class Draft
    {
        public int Columns { get; }
        public int Rows { get; }
        public byte[] Tiles { get; }
        private readonly bool[] _reserved;

        public Draft(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
            Tiles = new byte[columns * rows];
            _reserved = new bool[columns * rows];
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    if (x == 0 || y == 0 || x == columns - 1 || y == rows - 1)
                    {
                        Tiles[y * columns + x] = 2;
                    }
                }
            }
        }

        public bool Inside(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Columns && y < Rows;
        }

        public bool Wall(int x, int y)
        {
            if (!Inside(x, y))
            {
                return true;
            }
            return Tiles[y * Columns + x] != 0;
        }

        public void SetWall(int x, int y)
        {
            Tiles[y * Columns + x] = 1;
        }

        public void Clear(int x, int y)
        {
            Tiles[y * Columns + x] = 0;
        }

        public void Reserve(int x, int y)
        {
            if (Inside(x, y))
            {
                _reserved[y * Columns + x] = true;
            }
        }

        // center에서 radius칸 안(가운데끼리의 거리)의 칸을 비워 둔다.
        public void ReserveDisk(Cell center, int radius)
        {
            for (int y = center.Y - radius; y <= center.Y + radius; y++)
            {
                for (int x = center.X - radius; x <= center.X + radius; x++)
                {
                    if (center.DistanceSquared(new Cell(x, y)) <= radius * radius)
                    {
                        Reserve(x, y);
                    }
                }
            }
        }

        public void ReserveBox(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Reserve(x, y);
                }
            }
        }

        // 상자 안의 칸이 모두 맵 안쪽이고, 벽도 비워 둘 칸도 아니면 참이다.
        public bool BoxFree(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!Inside(x, y) || Wall(x, y) || _reserved[y * Columns + x])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // 상자 안의 칸이 모두 맵 안이고 벽이 아니면 참이다. 비워 둘 칸이어도 된다(그 자리에 무엇을 두는 경우).
        public bool BoxOpen(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (Wall(x, y))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    private static MissionLayout? TryGenerate(SeededRandom random, int columns, int rows)
    {
        Draft draft = new Draft(columns, rows);
        int side = random.NextInt(0, 4);
        Cell camp = SidePoint(random, columns, rows, side, CampInset);
        Cell extraction = SidePoint(random, columns, rows, Opposite(side), ExtractionInset);
        draft.ReserveDisk(camp, CampClearance);
        draft.ReserveDisk(extraction, ExtractionClearance);

        Cell[] facilities = new Cell[FacilityCount];
        for (int i = 0; i < FacilityCount; i++)
        {
            Cell? facility = PlaceFacility(random, draft, camp, facilities, i);
            if (facility == null)
            {
                return null;
            }
            facilities[i] = facility.Value;
            BuildEnclosure(random, draft, facility.Value);
        }

        // 처음 적은 시설마다 하나씩, 병사·분쇄자·철위병 순서다. 엘리트를 끈 작전에서는 Match가 모두 병사로 둔다.
        EnemyDefinition[] kinds = new EnemyDefinition[] { EnemyDefinition.Grunt, EnemyDefinition.Breaker, EnemyDefinition.Warden };
        EnemySpot[] enemies = new EnemySpot[FacilityCount];
        for (int i = 0; i < FacilityCount; i++)
        {
            Cell? spot = PlaceGuard(random, draft, camp, facilities[i]);
            if (spot == null)
            {
                return null;
            }
            draft.ReserveBox(spot.Value.X - 1, spot.Value.Y - 1, spot.Value.X + 1, spot.Value.Y + 1);
            enemies[i] = new EnemySpot(spot.Value.Center(), kinds[i]);
        }

        // 보급: 캠프 옆(손으로 만든 맵처럼 캠프에서 (70, 40))에 하나, 들판에 부목표 둘과 일반 둘이다.
        FixedVector campCenter = camp.Center();
        List<SupplySpot> supplies = new List<SupplySpot>();
        supplies.Add(new SupplySpot(campCenter + FixedVector.FromInt(70, 40), false));
        List<Cell> fieldSupplies = new List<Cell>();
        for (int i = 0; i < 4; i++)
        {
            bool objective = i < 2;
            Cell? spot = PlaceSupply(random, draft, camp, fieldSupplies, objective);
            if (spot == null)
            {
                return null;
            }
            fieldSupplies.Add(spot.Value);
            draft.ReserveBox(spot.Value.X - 1, spot.Value.Y - 1, spot.Value.X + 1, spot.Value.Y + 1);
            supplies.Add(new SupplySpot(spot.Value.Center(), objective));
        }

        PlaceObstacles(random, draft);

        SpawnRing ring = CreateRing(columns, rows);
        if (!Connected(draft, camp, extraction, facilities, enemies, supplies, ring))
        {
            return null;
        }
        FixedVector[] facilityPoints = new FixedVector[FacilityCount];
        for (int i = 0; i < FacilityCount; i++)
        {
            facilityPoints[i] = facilities[i].Center();
        }
        return new MissionLayout(columns, rows, draft.Tiles, campCenter, extraction.Center(), facilityPoints, supplies.ToArray(),
            enemies, ring);
    }

    // 변 side(0 왼쪽, 1 오른쪽, 2 위, 3 아래)에서 inset칸 안쪽의 한 칸이다. 변을 따라서는 가운데 60% 안에서 고른다.
    private static Cell SidePoint(SeededRandom random, int columns, int rows, int side, int inset)
    {
        if (side == 0 || side == 1)
        {
            int y = random.NextInt(rows * 2 / 10, rows * 8 / 10);
            int x = inset;
            if (side == 1)
            {
                x = columns - 1 - inset;
            }
            return new Cell(x, y);
        }
        int along = random.NextInt(columns * 2 / 10, columns * 8 / 10);
        int edge = inset;
        if (side == 3)
        {
            edge = rows - 1 - inset;
        }
        return new Cell(along, edge);
    }

    private static int Opposite(int side)
    {
        if (side == 0)
        {
            return 1;
        }
        if (side == 1)
        {
            return 0;
        }
        if (side == 2)
        {
            return 3;
        }
        return 2;
    }

    // 시설 가운데 칸을 고른다. 두르는 벽과 그 바깥 한 칸(9×9칸)이 맵 안쪽의 빈 땅이어야 하고,
    // 캠프와 앞서 둔 시설에서 충분히 떨어져야 한다.
    private static Cell? PlaceFacility(SeededRandom random, Draft draft, Cell camp, Cell[] placed, int count)
    {
        int reach = EnclosureHalf + 1;
        for (int t = 0; t < PlacementTries; t++)
        {
            int x = random.NextInt(1 + reach, draft.Columns - 1 - reach);
            int y = random.NextInt(1 + reach, draft.Rows - 1 - reach);
            Cell candidate = new Cell(x, y);
            if (candidate.DistanceSquared(camp) < FacilityFromCamp * FacilityFromCamp)
            {
                continue;
            }
            bool apart = true;
            for (int i = 0; i < count; i++)
            {
                if (candidate.DistanceSquared(placed[i]) < FacilityApart * FacilityApart)
                {
                    apart = false;
                }
            }
            if (!apart || !draft.BoxFree(x - reach, y - reach, x + reach, y + reach))
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    // 시설을 7×7칸 테두리 벽으로 두르고, 네 변 가운데 두 변의 가운데 3칸을 출입구로 연다.
    // 안쪽 5×5칸과 바깥 한 칸, 출입구는 장애물을 두지 않게 비워 둔다.
    private static void BuildEnclosure(SeededRandom random, Draft draft, Cell center)
    {
        int half = EnclosureHalf;
        for (int y = center.Y - half; y <= center.Y + half; y++)
        {
            for (int x = center.X - half; x <= center.X + half; x++)
            {
                bool edge = x == center.X - half || x == center.X + half || y == center.Y - half || y == center.Y + half;
                if (edge)
                {
                    draft.SetWall(x, y);
                }
            }
        }
        // 두 출입구가 같은 변이 되지 않게 첫 변을 고른 뒤 나머지 셋 가운데 하나를 고른다.
        int first = random.NextInt(0, 4);
        int second = (first + random.NextInt(1, 4)) % 4;
        OpenEntrance(draft, center, first);
        OpenEntrance(draft, center, second);
        draft.ReserveBox(center.X - half - 1, center.Y - half - 1, center.X + half + 1, center.Y + half + 1);
    }

    private static void OpenEntrance(Draft draft, Cell center, int side)
    {
        for (int offset = -1; offset <= 1; offset++)
        {
            if (side == 0)
            {
                draft.Clear(center.X - EnclosureHalf, center.Y + offset);
            }
            else if (side == 1)
            {
                draft.Clear(center.X + EnclosureHalf, center.Y + offset);
            }
            else if (side == 2)
            {
                draft.Clear(center.X + offset, center.Y - EnclosureHalf);
            }
            else
            {
                draft.Clear(center.X + offset, center.Y + EnclosureHalf);
            }
        }
    }

    // 시설을 지키는 처음 적의 자리다. 시설 가운데에서 5~7칸, 캠프에서 8칸 넘게 떨어지고 둘레 3×3칸에 벽이 없어야 한다.
    private static Cell? PlaceGuard(SeededRandom random, Draft draft, Cell camp, Cell facility)
    {
        for (int t = 0; t < PlacementTries; t++)
        {
            int x = facility.X + random.NextInt(-GuardFar, GuardFar + 1);
            int y = facility.Y + random.NextInt(-GuardFar, GuardFar + 1);
            Cell candidate = new Cell(x, y);
            int distance = candidate.DistanceSquared(facility);
            if (distance < GuardNear * GuardNear || distance > GuardFar * GuardFar)
            {
                continue;
            }
            if (candidate.DistanceSquared(camp) <= EnemyFromCamp * EnemyFromCamp || !draft.BoxOpen(x - 1, y - 1, x + 1, y + 1))
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    // 들판의 보급 자리다. 부목표는 캠프에서 7칸, 일반 보급은 5칸 넘게 떨어지고, 다른 보급과 5칸 넘게 떨어지며 둘레 3×3칸에 벽이 없어야 한다.
    private static Cell? PlaceSupply(SeededRandom random, Draft draft, Cell camp, List<Cell> placed, bool objective)
    {
        int fromCamp = 5;
        if (objective)
        {
            fromCamp = 7;
        }
        for (int t = 0; t < PlacementTries; t++)
        {
            int x = random.NextInt(2, draft.Columns - 2);
            int y = random.NextInt(2, draft.Rows - 2);
            Cell candidate = new Cell(x, y);
            if (candidate.DistanceSquared(camp) <= fromCamp * fromCamp || !draft.BoxOpen(x - 1, y - 1, x + 1, y + 1))
            {
                continue;
            }
            bool apart = true;
            for (int i = 0; i < placed.Count; i++)
            {
                if (candidate.DistanceSquared(placed[i]) <= 25)
                {
                    apart = false;
                }
            }
            if (apart)
            {
                return candidate;
            }
        }
        return null;
    }

    // 1~3칸 크기의 장애물을 흩어 놓는다. 장애물과 그 둘레 한 칸이 모두 비워 둘 칸이 아닌 빈 땅일 때만 둔다.
    // 그래서 장애물은 서로, 그리고 가장자리·시설 벽과 닿지 않아 그 사이로 늘 지나갈 수 있다.
    private static void PlaceObstacles(SeededRandom random, Draft draft)
    {
        int count = (draft.Columns - 2) * (draft.Rows - 2) / CellsPerObstacle;
        int placed = 0;
        for (int t = 0; t < count * 20 && placed < count; t++)
        {
            int width = random.NextInt(1, 4);
            int height = random.NextInt(1, 4);
            int x = random.NextInt(1, draft.Columns - 1 - width);
            int y = random.NextInt(1, draft.Rows - 1 - height);
            if (!draft.BoxFree(x - 1, y - 1, x + width, y + height))
            {
                continue;
            }
            for (int dy = 0; dy < height; dy++)
            {
                for (int dx = 0; dx < width; dx++)
                {
                    draft.SetWall(x + dx, y + dy);
                }
            }
            placed++;
        }
    }

    // 맵 가운데를 둘러싸고 가장자리에서 RingInset칸 안쪽을 지나는 타원이다.
    private static SpawnRing CreateRing(int columns, int rows)
    {
        int width = columns * BattleMap.Cell;
        int height = rows * BattleMap.Cell;
        int inset = RingInset * BattleMap.Cell;
        return new SpawnRing(FixedVector.FromInt(width / 2, height / 2), width / 2 - inset, height / 2 - inset);
    }

    // 캠프 칸에서 빈 칸만 밟아(상하좌우) 갈 수 있는 칸을 모두 찾고, 목표가 모두 그 안에 있는지 본다.
    // 증원은 타원 위의 무작위 자리에 생기므로 타원 위의 점 대부분(RingReachablePercent)에도 갈 수 있어야 한다.
    private static bool Connected(Draft draft, Cell camp, Cell extraction, Cell[] facilities, EnemySpot[] enemies, List<SupplySpot> supplies,
        SpawnRing ring)
    {
        bool[] reached = new bool[draft.Columns * draft.Rows];
        Queue<Cell> queue = new Queue<Cell>();
        reached[camp.Y * draft.Columns + camp.X] = true;
        queue.Enqueue(camp);
        int[] stepX = new int[] { 1, -1, 0, 0 };
        int[] stepY = new int[] { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            Cell cell = queue.Dequeue();
            for (int i = 0; i < 4; i++)
            {
                int x = cell.X + stepX[i];
                int y = cell.Y + stepY[i];
                if (draft.Wall(x, y) || reached[y * draft.Columns + x])
                {
                    continue;
                }
                reached[y * draft.Columns + x] = true;
                queue.Enqueue(new Cell(x, y));
            }
        }
        if (!Reached(draft, reached, extraction.Center()))
        {
            return false;
        }
        for (int i = 0; i < facilities.Length; i++)
        {
            if (!Reached(draft, reached, facilities[i].Center()))
            {
                return false;
            }
        }
        for (int i = 0; i < enemies.Length; i++)
        {
            if (!Reached(draft, reached, enemies[i].Position))
            {
                return false;
            }
        }
        for (int i = 0; i < supplies.Count; i++)
        {
            if (!Reached(draft, reached, supplies[i].Position))
            {
                return false;
            }
        }
        int reachable = 0;
        for (int i = 0; i < RingSamples; i++)
        {
            FixedVector direction = Angle.FromFraction(i, RingSamples).Direction();
            FixedVector point = new FixedVector(ring.Center.X + direction.X * ring.RadiusX, ring.Center.Y + direction.Y * ring.RadiusY);
            if (Reached(draft, reached, point))
            {
                reachable++;
            }
        }
        return reachable * 100 >= RingSamples * RingReachablePercent;
    }

    private static bool Reached(Draft draft, bool[] reached, FixedVector point)
    {
        int x = point.X.FloorDivide(BattleMap.Cell);
        int y = point.Y.FloorDivide(BattleMap.Cell);
        if (!draft.Inside(x, y))
        {
            return false;
        }
        return reached[y * draft.Columns + x];
    }
}
