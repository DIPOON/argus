using System.Numerics;

namespace Argus.Server.Game;

public sealed class BattleMap
{
    public const int Columns = 40;
    public const int Rows = 30;
    public const int Cell = 48;
    public const int Width = Columns * Cell;
    public const int Height = Rows * Cell;
    public static readonly Vector2 Camp = new(300, 730);
    public static readonly Vector2 Extraction = new(1080, 1130);
    public const int CampVision = 260;
    public byte[] Tiles { get; } = new byte[Columns * Rows];
    private readonly double[] _hp = new double[Columns * Rows];
    public int Revision { get; private set; }

    public BattleMap()
    {
        for (var y = 0; y < Rows; y++)
        for (var x = 0; x < Columns; x++)
            if (x == 0 || y == 0 || x == Columns - 1 || y == Rows - 1) Set(x, y, 2);
        // Compact objective compound; gaps are real entrances, every inner wall is destructible.
        for (var x = 15; x <= 29; x++) { Set(x, 7, 1); Set(x, 21, 1); }
        for (var y = 7; y <= 21; y++) { Set(15, y, 1); Set(29, y, 1); }
        foreach (var y in new[] { 13, 14, 15 }) { Set(15, y, 0); Set(29, y, 0); }
        foreach (var x in new[] { 21, 22, 23 }) { Set(x, 7, 0); Set(x, 21, 0); }
        foreach (var (x, y) in new[] { (19, 11), (20, 11), (25, 12), (25, 13), (20, 17), (21, 17), (26, 18), (10, 10), (10, 11), (10, 18), (11, 18), (32, 10), (32, 11), (33, 19) }) Set(x, y, 1);
    }

    private void Set(int x, int y, byte kind)
    {
        var index = y * Columns + x;
        Tiles[index] = kind;
        _hp[index] = kind == 1 ? 100 : double.PositiveInfinity;
    }

    public bool Solid(int x, int y) => x < 0 || y < 0 || x >= Columns || y >= Rows || Tiles[y * Columns + x] != 0;
    public bool Damage(int index, double damage)
    {
        if (index < 0 || index >= Tiles.Length || Tiles[index] != 1) return false;
        _hp[index] -= damage;
        if (_hp[index] > 0) return false;
        Tiles[index] = 0;
        Revision++;
        return true;
    }

    public bool CanStand(Vector2 p, float radius = 13)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || p.X < radius || p.Y < radius || p.X > Width - radius || p.Y > Height - radius) return false;
        var minX = (int)((p.X - radius) / Cell);
        var maxX = (int)((p.X + radius) / Cell);
        var minY = (int)((p.Y - radius) / Cell);
        var maxY = (int)((p.Y + radius) / Cell);
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            if (!Solid(x, y)) continue;
            var nearest = new Vector2(Math.Clamp(p.X, x * Cell, (x + 1) * Cell), Math.Clamp(p.Y, y * Cell, (y + 1) * Cell));
            if (Vector2.DistanceSquared(nearest, p) < radius * radius) return false;
        }
        return true;
    }

    public Vector2 Move(Vector2 from, Vector2 delta, float radius = 13)
    {
        // Substeps prevent tunnelling through a wall during knockback.
        var steps = Math.Max(1, (int)Math.Ceiling(delta.Length() / 8));
        delta /= steps;
        for (var i = 0; i < steps; i++)
        {
            var x = new Vector2(from.X + delta.X, from.Y);
            if (CanStand(x, radius)) from = x;
            var y = new Vector2(from.X, from.Y + delta.Y);
            if (CanStand(y, radius)) from = y;
        }
        return from;
    }

    public (double Fraction, int Tile) RayWall(Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var steps = Math.Max(1, (int)Math.Ceiling(delta.Length() / 5));
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var p = from + delta * (float)t;
            var x = (int)Math.Floor(p.X / Cell);
            var y = (int)Math.Floor(p.Y / Cell);
            if (Solid(x, y)) return (t, x >= 0 && y >= 0 && x < Columns && y < Rows ? y * Columns + x : -1);
        }
        return (double.PositiveInfinity, -1);
    }

    public bool LineOfSight(Vector2 from, Vector2 to) => double.IsPositiveInfinity(RayWall(from, to).Fraction);

    public static double RayCircle(Vector2 from, Vector2 to, Vector2 center, float radius)
    {
        var d = to - from;
        var f = from - center;
        var a = Vector2.Dot(d, d);
        var c = Vector2.Dot(f, f) - radius * radius;
        if (c <= 0) return 0;
        if (a < 0.00001) return double.PositiveInfinity;
        var b = 2 * Vector2.Dot(f, d);
        var disc = b * b - 4 * a * c;
        if (disc < 0) return double.PositiveInfinity;
        var t = (-b - Math.Sqrt(disc)) / (2 * a);
        return t >= 0 && t <= 1 ? t : double.PositiveInfinity;
    }
}
