namespace Argus.Server.Game;

// 선분이 벽에 닿았는지와 그 지점이다. Hit가 false면 끝점까지 막힘이 없다.
// Fraction은 시작점에서 끝점까지 간 비율(0~1)이고, Tile은 닿은 타일 번호다(맵 밖이면 -1).
public readonly struct WallHit
{
    public bool Hit { get; }
    public Fixed Fraction { get; }
    public int Tile { get; }

    public WallHit(bool hit, Fixed fraction, int tile)
    {
        Hit = hit;
        Fraction = fraction;
        Tile = tile;
    }

    public static WallHit None { get; } = new WallHit(false, Fixed.Zero, -1);
}

public sealed class BattleMap
{
    public const int Cell = 48;
    // 캠프의 시야 반경이다. 캠프 둘레는 대원이 없어도 늘 보인다.
    public const int CampVision = 260;
    // 스냅샷의 벽 배열에서 분대가 아직 보지 못한 칸이다. 0 빈 땅, 1 부술 수 있는 벽, 2 부술 수 없는 벽 다음 값이다.
    public const byte UnknownTile = 3;
    // 부술 수 있는 벽(종류 1)의 체력이다. 부술 수 없는 벽(종류 2)은 무한대이고, 빈 땅은 0이다.
    // 체력을 읽는 코드가 벽 종류를 먼저 보지 않아도, 부술 수 없는 벽을 이미 부서진 벽(체력 0 이하)으로 보지 않는다.
    private const double BreakableWallHp = 3;
    public int Columns { get; }
    public int Rows { get; }
    public byte[] Tiles { get; }
    private readonly double[] _hp;
    public int Revision { get; private set; }

    // 작전 배치의 처음 벽으로 맵을 만든다. 진행 중에는 벽이 부서지며 Tiles가 바뀐다.
    public BattleMap(MissionLayout layout)
    {
        Columns = layout.Columns;
        Rows = layout.Rows;
        Tiles = new byte[Columns * Rows];
        _hp = new double[Columns * Rows];
        for (int i = 0; i < Tiles.Length; i++)
        {
            Tiles[i] = layout.Tiles[i];
            if (Tiles[i] == 1)
            {
                _hp[i] = BreakableWallHp;
            }
            else if (Tiles[i] == 2)
            {
                _hp[i] = double.PositiveInfinity;
            }
        }
    }

    public int Width
    {
        get
        {
            return Columns * Cell;
        }
    }

    public int Height
    {
        get
        {
            return Rows * Cell;
        }
    }

    public bool Solid(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Columns || y >= Rows)
        {
            return true;
        }
        return Tiles[y * Columns + x] != 0;
    }

    public bool Damage(int index, double damage)
    {
        if (index < 0 || index >= Tiles.Length || Tiles[index] != 1)
        {
            return false;
        }

        _hp[index] -= damage;
        if (_hp[index] > 0)
        {
            return false;
        }

        Tiles[index] = 0;
        Revision++;
        return true;
    }

    public bool CanStand(FixedVector position, Fixed radius)
    {
        return CanStand(position, radius, null);
    }

    // known이 있으면 그 칸이 참인 벽만 본다(분대가 본 칸의 벽). 투입 요청에 답할 때처럼 안 본 칸의 벽에 따라 답이 달라지면
    // 안 되는 곳에서 쓴다. 안 본 칸은 빈 땅으로 본다.
    public bool CanStand(FixedVector position, Fixed radius, bool[]? known)
    {
        if (position.X < radius || position.Y < radius ||
            position.X > Fixed.FromInt(Width) - radius || position.Y > Fixed.FromInt(Height) - radius)
        {
            return false;
        }

        int minX = (position.X - radius).FloorDivide(Cell);
        int maxX = (position.X + radius).FloorDivide(Cell);
        int minY = (position.Y - radius).FloorDivide(Cell);
        int maxY = (position.Y + radius).FloorDivide(Cell);
        Fixed radiusSquared = radius * radius;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!Solid(x, y))
                {
                    continue;
                }
                // 맵 밖 칸은 늘 벽이다. 몸이 맵 끝에 딱 닿으면 맵 밖 칸이 범위에 들어오므로 칸 번호를 쓰기 전에 거른다.
                bool inside = x >= 0 && y >= 0 && x < Columns && y < Rows;
                if (known != null && inside && !known[y * Columns + x])
                {
                    continue;
                }

                // 타일 사각형에서 몸의 중심에 가장 가까운 점이 반지름보다 가까우면 겹친다.
                FixedVector nearest = new FixedVector(
                    Fixed.Clamp(position.X, Fixed.FromInt(x * Cell), Fixed.FromInt((x + 1) * Cell)),
                    Fixed.Clamp(position.Y, Fixed.FromInt(y * Cell), Fixed.FromInt((y + 1) * Cell)));
                if (FixedVector.DistanceSquared(nearest, position) < radiusSquared)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // from에서 to까지의 선분이 처음 닿는 벽을 찾는다. 타일 경계를 지나는 순서대로 한 칸씩 나아간다.
    // 일정 간격으로 점을 찍어 보는 방식은 모서리를 조금 스친 벽을 놓칠 수 있다.
    // 경계마다 지나는 비율은 (경계까지 남은 거리) / (선분의 길이)다. 두 축 중 어느 경계를 먼저 지나는지는 나누지 않고
    // 엇갈려 곱해 비교한다(a / b < c / d ⟺ a × d < c × b). 그래서 순서가 반올림 없이 정확하고, 곱은 가장 큰 맵(MissionLayout.MaximumSize)에서도 2^50 아래라
    // JavaScript로도 같은 계산을 할 수 있다.
    public WallHit RayWall(FixedVector from, FixedVector to)
    {
        return Trace(from, to, null, null);
    }

    // from에서 to로 가는 선분이 지나는 칸을 known에 참으로 적는다. 처음 닿는 벽 칸까지 적고 멈춘다(그 벽은 앞면이 보인다).
    // 새로 적은 칸 번호는 revealed에 더한다. 맵 밖 칸은 적지 않는다. 지나는 칸과 멈추는 곳은 RayWall과 같다.
    // 꼭짓점을 정확히 지날 때 옆 칸은 그 한 점만 닿으므로, 벽이라 멈출 때가 아니면 적지 않는다.
    public WallHit Reveal(FixedVector from, FixedVector to, bool[] known, List<int> revealed)
    {
        return Trace(from, to, known, revealed);
    }

    // RayWall과 Reveal이 함께 쓰는 칸 따라가기다. known이 있으면 지나는 칸을 적는다.
    private WallHit Trace(FixedVector from, FixedVector to, bool[]? known, List<int>? revealed)
    {
        int x = from.X.FloorDivide(Cell);
        int y = from.Y.FloorDivide(Cell);
        MarkKnown(known, revealed, x, y);
        if (Solid(x, y))
        {
            return TileHit(Fixed.Zero, x, y);
        }
        long dx = to.X.Raw - from.X.Raw;
        long dy = to.Y.Raw - from.Y.Raw;
        int stepX = Math.Sign(dx);
        int stepY = Math.Sign(dy);
        long width = Math.Abs(dx);
        long height = Math.Abs(dy);
        long cell = Cell * Fixed.Scale;
        // 다음 세로 경계와 가로 경계까지 남은 거리(Raw)다. 그 축으로 움직이지 않으면 쓰지 않는다.
        long untilX = 0;
        if (dx > 0)
        {
            untilX = (x + 1) * cell - from.X.Raw;
        }
        else if (dx < 0)
        {
            untilX = from.X.Raw - x * cell;
        }
        long untilY = 0;
        if (dy > 0)
        {
            untilY = (y + 1) * cell - from.Y.Raw;
        }
        else if (dy < 0)
        {
            untilY = from.Y.Raw - y * cell;
        }
        while (true)
        {
            // 비율이 1 이하(남은 거리 ≤ 선분의 길이)인 경계만 선분 안에 있다.
            bool reachesX = dx != 0 && untilX <= width;
            bool reachesY = dy != 0 && untilY <= height;
            if (!reachesX && !reachesY)
            {
                return WallHit.None;
            }
            // -1이면 세로 경계를 먼저, 1이면 가로 경계를 먼저, 0이면 꼭짓점을 동시에 지난다.
            int order;
            if (reachesX && reachesY)
            {
                long crossX = Fixed.Safe(checked(untilX * height));
                long crossY = Fixed.Safe(checked(untilY * width));
                order = crossX.CompareTo(crossY);
            }
            else if (reachesX)
            {
                order = -1;
            }
            else
            {
                order = 1;
            }
            // 이번에 지나는 경계까지의 거리와 그 축의 선분 길이다. 벽에 닿았을 때만 둘로 비율을 구한다.
            long crossed;
            long length;
            if (order == 0)
            {
                crossed = untilX;
                length = width;
                // 정확한 꼭짓점 통과도 양옆 타일을 확인한다.
                if (Solid(x + stepX, y))
                {
                    MarkKnown(known, revealed, x + stepX, y);
                    return TileHit(Ratio(crossed, length), x + stepX, y);
                }
                if (Solid(x, y + stepY))
                {
                    MarkKnown(known, revealed, x, y + stepY);
                    return TileHit(Ratio(crossed, length), x, y + stepY);
                }
                x += stepX;
                y += stepY;
                untilX += cell;
                untilY += cell;
            }
            else if (order < 0)
            {
                crossed = untilX;
                length = width;
                x += stepX;
                untilX += cell;
            }
            else
            {
                crossed = untilY;
                length = height;
                y += stepY;
                untilY += cell;
            }
            MarkKnown(known, revealed, x, y);
            if (Solid(x, y))
            {
                return TileHit(Ratio(crossed, length), x, y);
            }
        }
    }

    // 맵 안 칸을 아직 적지 않았으면 known에 적고 revealed에 더한다. known이 없으면(RayWall) 아무것도 하지 않는다.
    private void MarkKnown(bool[]? known, List<int>? revealed, int x, int y)
    {
        if (known == null || x < 0 || y < 0 || x >= Columns || y >= Rows)
        {
            return;
        }
        int index = y * Columns + x;
        if (known[index])
        {
            return;
        }
        known[index] = true;
        if (revealed != null)
        {
            revealed.Add(index);
        }
    }

    // 거리의 비율(part / whole, 0~1)을 고정소수점으로 바꾼다.
    private static Fixed Ratio(long part, long whole)
    {
        return Fixed.FromRaw(Fixed.DivideRounded(part * Fixed.Scale, whole));
    }

    private WallHit TileHit(Fixed fraction, int x, int y)
    {
        int tile = -1;
        if (x >= 0 && y >= 0 && x < Columns && y < Rows)
        {
            tile = y * Columns + x;
        }
        return new WallHit(true, fraction, tile);
    }

    public bool LineOfSight(FixedVector from, FixedVector to)
    {
        WallHit wall = RayWall(from, to);
        return !wall.Hit;
    }

    // 상태 요약에 벽 종류, 벽 체력, 판 번호를 적는다.
    public void AddState(StateHasher hasher)
    {
        hasher.AddBytes(Tiles);
        hasher.AddDoubleArray(_hp);
        hasher.AddInt(Revision);
    }
}
