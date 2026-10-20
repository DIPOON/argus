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
    public const int Columns = 40;
    public const int Rows = 30;
    public const int Cell = 48;
    public const int Width = Columns * Cell;
    public const int Height = Rows * Cell;
    public static readonly FixedVector Camp = FixedVector.FromInt(300, 730);
    public static readonly FixedVector Extraction = FixedVector.FromInt(1080, 1130);
    public const int CampVision = 260;
    public byte[] Tiles { get; } = new byte[Columns * Rows];
    private readonly double[] _hp = new double[Columns * Rows];
    public int Revision { get; private set; }

    public BattleMap()
    {
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                if (x == 0 || y == 0 || x == Columns - 1 || y == Rows - 1)
                {
                    Set(x, y, 2);
                }
            }
        }

        // 임무 시설을 둘러싼 벽을 만든다. 맵 안쪽 벽은 모두 파괴할 수 있다.
        for (int x = 15; x <= 29; x++)
        {
            Set(x, 7, 1);
            Set(x, 21, 1);
        }
        for (int y = 7; y <= 21; y++)
        {
            Set(15, y, 1);
            Set(29, y, 1);
        }

        // 벽 일부를 지워 출입구를 만든다.
        for (int y = 13; y <= 15; y++)
        {
            Set(15, y, 0);
            Set(29, y, 0);
        }
        for (int x = 21; x <= 23; x++)
        {
            Set(x, 7, 0);
            Set(x, 21, 0);
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
            Set(obstacles[i, 0], obstacles[i, 1], 1);
        }
    }

    private void Set(int x, int y, byte kind)
    {
        int index = y * Columns + x;
        Tiles[index] = kind;
        if (kind == 1)
        {
            _hp[index] = 3;
        }
        else
        {
            _hp[index] = double.PositiveInfinity;
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
    // 엇갈려 곱해 비교한다(a / b < c / d ⟺ a × d < c × b). 그래서 순서가 반올림 없이 정확하고, 곱은 맵 안에서 2^47 아래라
    // JavaScript로도 같은 계산을 할 수 있다.
    public WallHit RayWall(FixedVector from, FixedVector to)
    {
        int x = from.X.FloorDivide(Cell);
        int y = from.Y.FloorDivide(Cell);
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
            Fixed fraction;
            if (order == 0)
            {
                fraction = Ratio(untilX, width);
                // 정확한 꼭짓점 통과도 양옆 타일을 확인한다.
                if (Solid(x + stepX, y))
                {
                    return TileHit(fraction, x + stepX, y);
                }
                if (Solid(x, y + stepY))
                {
                    return TileHit(fraction, x, y + stepY);
                }
                x += stepX;
                y += stepY;
                untilX += cell;
                untilY += cell;
            }
            else if (order < 0)
            {
                fraction = Ratio(untilX, width);
                x += stepX;
                untilX += cell;
            }
            else
            {
                fraction = Ratio(untilY, height);
                y += stepY;
                untilY += cell;
            }
            if (Solid(x, y))
            {
                return TileHit(fraction, x, y);
            }
        }
    }

    // 거리의 비율(part / whole, 0~1)을 고정소수점으로 바꾼다.
    private static Fixed Ratio(long part, long whole)
    {
        return Fixed.FromRaw(Fixed.DivideRounded(part * Fixed.Scale, whole));
    }

    private static WallHit TileHit(Fixed fraction, int x, int y)
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
