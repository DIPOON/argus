using System.Numerics;

namespace Argus.Server.Game;

// 벽과 충돌한 지점과 타일 번호를 함께 돌려준다.
public readonly struct WallHit
{
    public double Fraction { get; }
    public int Tile { get; }

    public WallHit(double fraction, int tile)
    {
        Fraction = fraction;
        Tile = tile;
    }
}

public sealed class BattleMap
{
    public const int Columns = 40;
    public const int Rows = 30;
    public const int Cell = 48;
    public const int Width = Columns * Cell;
    public const int Height = Rows * Cell;
    public static readonly Vector2 Camp = new Vector2(300, 730);
    public static readonly Vector2 Extraction = new Vector2(1080, 1130);
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

    public bool CanStand(Vector2 position, float radius = 13)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            return false;
        }
        if (position.X < radius || position.Y < radius ||
            position.X > Width - radius || position.Y > Height - radius)
        {
            return false;
        }

        int minX = (int)((position.X - radius) / Cell);
        int maxX = (int)((position.X + radius) / Cell);
        int minY = (int)((position.Y - radius) / Cell);
        int maxY = (int)((position.Y + radius) / Cell);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!Solid(x, y))
                {
                    continue;
                }

                Vector2 nearest = new Vector2(
                    Math.Clamp(position.X, x * Cell, (x + 1) * Cell),
                    Math.Clamp(position.Y, y * Cell, (y + 1) * Cell));
                if (Vector2.DistanceSquared(nearest, position) < radius * radius)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public Vector2 Move(Vector2 from, Vector2 delta, float radius = 13)
    {
        // 밀려나는 거리가 커도 벽을 통과하지 않도록 이동을 작은 단계로 나눈다.
        int steps = Math.Max(1, (int)Math.Ceiling(delta.Length() / 8));
        delta /= steps;
        for (int i = 0; i < steps; i++)
        {
            Vector2 nextX = new Vector2(from.X + delta.X, from.Y);
            if (CanStand(nextX, radius))
            {
                from = nextX;
            }

            Vector2 nextY = new Vector2(from.X, from.Y + delta.Y);
            if (CanStand(nextY, radius))
            {
                from = nextY;
            }
        }
        return from;
    }

    public WallHit RayWall(Vector2 from, Vector2 to)
    {
        // 타일 경계를 순서대로 지난다. 일정 간격 샘플링은 모서리를 조금 스친 벽을 놓칠 수 있다.
        int x = (int)Math.Floor(from.X / Cell);
        int y = (int)Math.Floor(from.Y / Cell);
        if (Solid(x, y))
        {
            return TileHit(0, x, y);
        }
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        int stepX = Math.Sign(dx);
        int stepY = Math.Sign(dy);
        double nextX = double.PositiveInfinity;
        double nextY = double.PositiveInfinity;
        double deltaX = double.PositiveInfinity;
        double deltaY = double.PositiveInfinity;
        if (dx != 0)
        {
            double boundary = x * Cell;
            if (dx > 0)
            {
                boundary += Cell;
            }
            nextX = (boundary - from.X) / dx;
            deltaX = Cell / Math.Abs(dx);
        }
        if (dy != 0)
        {
            double boundary = y * Cell;
            if (dy > 0)
            {
                boundary += Cell;
            }
            nextY = (boundary - from.Y) / dy;
            deltaY = Cell / Math.Abs(dy);
        }
        while (Math.Min(nextX, nextY) <= 1)
        {
            double fraction = Math.Min(nextX, nextY);
            if (Math.Abs(nextX - nextY) < 0.000000001)
            {
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
                nextX += deltaX;
                nextY += deltaY;
            }
            else if (nextX < nextY)
            {
                x += stepX;
                nextX += deltaX;
            }
            else
            {
                y += stepY;
                nextY += deltaY;
            }
            if (Solid(x, y))
            {
                return TileHit(fraction, x, y);
            }
        }
        return new WallHit(double.PositiveInfinity, -1);
    }

    private static WallHit TileHit(double fraction, int x, int y)
    {
        int tile = -1;
        if (x >= 0 && y >= 0 && x < Columns && y < Rows)
        {
            tile = y * Columns + x;
        }
        return new WallHit(fraction, tile);
    }

    public bool LineOfSight(Vector2 from, Vector2 to)
    {
        WallHit wall = RayWall(from, to);
        return double.IsPositiveInfinity(wall.Fraction);
    }

    public static double RayCircle(Vector2 from, Vector2 to, Vector2 center, float radius)
    {
        Vector2 direction = to - from;
        Vector2 offset = from - center;
        float a = Vector2.Dot(direction, direction);
        float c = Vector2.Dot(offset, offset) - radius * radius;
        if (c <= 0)
        {
            return 0;
        }
        if (a < 0.00001)
        {
            return double.PositiveInfinity;
        }

        float b = 2 * Vector2.Dot(offset, direction);
        float discriminant = b * b - 4 * a * c;
        if (discriminant < 0)
        {
            return double.PositiveInfinity;
        }

        double fraction = (-b - Math.Sqrt(discriminant)) / (2 * a);
        if (fraction >= 0 && fraction <= 1)
        {
            return fraction;
        }
        return double.PositiveInfinity;
    }
}
