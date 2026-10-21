using System.Text.Json;
using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

// 작전 배치(MissionLayout)의 검사다. 손으로 만든 맵과 크기가 다른 맵에서도 방이 그 배치대로 진행되는지 본다.
internal static class LayoutTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("A room plays on a map of another size with the camp, objectives and enemies its layout names", OtherSize));
        tests.Add(new TestCase("The largest map size runs and sends snapshots without leaving the fixed-point range", LargestSize));
        tests.Add(new TestCase("A layout rejects map sizes out of range, a wrong tile count and unknown wall kinds", LayoutLimits));
    }

    // 가장자리만 부술 수 없는 벽인 빈 맵이다.
    private static byte[] BorderTiles(int columns, int rows)
    {
        byte[] tiles = new byte[columns * rows];
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
        return tiles;
    }

    // 손으로 만든 맵(40×30)보다 작은 24×18 맵이다. 가운데에 부술 수 있는 벽이 하나 있다.
    private static MissionLayout SmallLayout()
    {
        int columns = 24;
        int rows = 18;
        byte[] tiles = BorderTiles(columns, rows);
        tiles[9 * columns + 12] = 1;
        FixedVector camp = FixedVector.FromInt(150, 430);
        FixedVector[] facilities = new FixedVector[] { FixedVector.FromInt(820, 200) };
        SupplySpot[] supplies = new SupplySpot[]
        {
            new SupplySpot(FixedVector.FromInt(220, 470), false),
            new SupplySpot(FixedVector.FromInt(600, 700), true)
        };
        FixedVector[] enemies = new FixedVector[] { FixedVector.FromInt(900, 650) };
        SpawnRing ring = new SpawnRing(FixedVector.FromInt(576, 432), 420, 320);
        return new MissionLayout(columns, rows, tiles, camp, FixedVector.FromInt(1000, 430), facilities, supplies, enemies, ring);
    }

    // 배치대로 방을 만들고 대원 하나를 캠프에 투입한다.
    private static Match DeployedRoom(MissionLayout layout, out Player player)
    {
        Match match = new Match("LAYOUT", new Rules(), SeededRandom.SeedFromNumber(5), layout);
        player = match.Join("p", "pilot")!;
        Check(match.Deploy(player, layout.Camp.X.ToDouble(), layout.Camp.Y.ToDouble()));
        Advance(match, match.Rules.DeploySeconds + 0.01);
        Equal(player.State, "alive");
        return match;
    }

    // 원을 그리며 걷는 입력을 seconds초 동안 넣는다. 그동안 적이 다가와 대원이 쓰러질 수도 있다.
    private static void Walk(Match match, Player player, double seconds)
    {
        long ticks = Rules.ToTicks(seconds);
        for (long tick = 0; tick < ticks; tick++)
        {
            double heading = tick * 0.03;
            match.Input(player, tick, Math.Cos(heading), Math.Sin(heading), 0, 0);
            match.Step();
        }
    }

    private static void OtherSize()
    {
        MissionLayout layout = SmallLayout();
        // 시설·보급·처음 적은 배치의 자리에 번호 순서대로 놓인다. 투입이 끝나면 적이 움직이므로 만든 직후에 본다.
        Match fresh = new Match("LAYOUT", new Rules(), SeededRandom.SeedFromNumber(5), layout);
        Equal(fresh.Map.Columns, 24);
        Equal(fresh.Map.Rows, 18);
        Equal(fresh.Map.Width, 24 * BattleMap.Cell);
        Equal(fresh.Map.Height, 18 * BattleMap.Cell);
        Equal(fresh.Facilities.Count, 1);
        Equal(fresh.Facilities[0].Position, layout.Facilities[0]);
        Equal(fresh.Supplies.Count, 2);
        Equal(fresh.Supplies[1].Id, 2);
        Equal(fresh.Supplies[1].Position, layout.Supplies[1].Position);
        Check(fresh.Supplies[1].Objective, "부목표 보급이 아닙니다");
        Equal(fresh.Enemies.Count, 1);
        Equal(fresh.Enemies[0].Position, layout.InitialEnemySpots[0]);

        Match match = DeployedRoom(layout, out Player player);

        // 스냅샷의 맵 크기와 캠프·탈출 지점, 벽 칸 수도 이 배치를 따른다.
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, player, RoomHost.Json)))
        {
            JsonElement root = json.RootElement;
            JsonElement map = root.GetProperty("map");
            Equal(map.GetProperty("width").GetInt32(), 24 * BattleMap.Cell);
            Equal(map.GetProperty("height").GetInt32(), 18 * BattleMap.Cell);
            Equal(map.GetProperty("cell").GetInt32(), BattleMap.Cell);
            Equal(map.GetProperty("columns").GetInt32(), 24);
            Equal(map.GetProperty("rows").GetInt32(), 18);
            Equal(map.GetProperty("campX").GetDouble(), 150.0);
            Equal(map.GetProperty("campY").GetDouble(), 430.0);
            Equal(root.GetProperty("extraction").GetProperty("x").GetDouble(), 1000.0);
            byte[] walls = Convert.FromBase64String(root.GetProperty("walls").GetString()!);
            Equal(walls.Length, 24 * 18);
        }

        // 40초 동안 걸으면 증원이 생기고, 흐름장과 적 조향이 이 크기의 맵에서 돈다. 적은 모두 맵 안의 설 수 있는 자리에 있다.
        Walk(match, player, 40);
        Check(match.Enemies.Count > 1, "증원이 하나도 생기지 않았습니다");
        for (int i = 0; i < match.Enemies.Count; i++)
        {
            Enemy enemy = match.Enemies[i];
            Check(match.Map.CanStand(enemy.Position, enemy.Definition.Radius), $"적 {enemy.Id}가 설 수 없는 자리 {enemy.Position}에 있습니다");
        }

        // 같은 배치와 시드, 같은 입력이면 같은 상태가 된다. 배치도 상태 요약에 들어간다.
        Match again = DeployedRoom(SmallLayout(), out Player second);
        Walk(again, second, 40);
        Equal(again.StateHash(), match.StateHash());
    }

    // 가장 큰 128×128 맵이다. 캠프를 맵 한쪽 끝에 두어, 맵을 가로지르는 거리와 시야 계산이 가장 큰 좌표를 쓰게 한다.
    private static void LargestSize()
    {
        int size = MissionLayout.MaximumSize;
        int far = size * BattleMap.Cell - 150;
        FixedVector camp = FixedVector.FromInt(far, far);
        FixedVector[] facilities = new FixedVector[] { FixedVector.FromInt(150, 150) };
        SupplySpot[] supplies = new SupplySpot[] { new SupplySpot(FixedVector.FromInt(150, far), true) };
        FixedVector[] enemies = new FixedVector[] { FixedVector.FromInt(far, 150) };
        int center = size * BattleMap.Cell / 2;
        SpawnRing ring = new SpawnRing(FixedVector.FromInt(center, center), center - 150, center - 150);
        MissionLayout layout = new MissionLayout(size, size, BorderTiles(size, size), camp, FixedVector.FromInt(center, center),
            facilities, supplies, enemies, ring);
        Match match = DeployedRoom(layout, out Player player);
        Walk(match, player, 15);
        // 맵 반대편 끝의 점이 보이는지도 계산할 수 있다.
        Check(!match.Visible(FixedVector.FromInt(150, 150)), "맵 반대편 끝이 보입니다");
        byte[] snapshot = Snapshot.Create(match, player, RoomHost.Json);
        Check(snapshot.Length > 0);
    }

    private static void LayoutLimits()
    {
        FixedVector camp = FixedVector.FromInt(150, 150);
        FixedVector[] none = new FixedVector[0];
        SupplySpot[] noSupplies = new SupplySpot[0];
        SpawnRing ring = new SpawnRing(camp, 100, 100);
        int small = MissionLayout.MinimumSize;
        int large = MissionLayout.MaximumSize;
        // 경계값의 크기는 받는다.
        new MissionLayout(small, small, BorderTiles(small, small), camp, camp, none, noSupplies, none, ring);
        new MissionLayout(large, large, BorderTiles(large, large), camp, camp, none, noSupplies, none, ring);
        // 범위를 벗어난 크기, 크기와 맞지 않는 칸 수, 모르는 벽 종류는 거절한다.
        Throws<ArgumentOutOfRangeException>(() => new MissionLayout(small - 1, small, new byte[(small - 1) * small], camp, camp, none, noSupplies, none, ring));
        Throws<ArgumentOutOfRangeException>(() => new MissionLayout(small, large + 1, new byte[small * (large + 1)], camp, camp, none, noSupplies, none, ring));
        Throws<ArgumentException>(() => new MissionLayout(small, small, new byte[small * small - 1], camp, camp, none, noSupplies, none, ring));
        byte[] unknown = BorderTiles(small, small);
        unknown[small + 1] = 3;
        Throws<ArgumentException>(() => new MissionLayout(small, small, unknown, camp, camp, none, noSupplies, none, ring));

        // 배치는 받은 배열을 복사해 둔다. 만든 뒤에 그 배열을 바꿔도 배치는 그대로다.
        byte[] tiles = BorderTiles(small, small);
        MissionLayout layout = new MissionLayout(small, small, tiles, camp, camp, none, noSupplies, none, ring);
        tiles[small + 1] = 1;
        Equal(layout.Tiles[small + 1], (byte)0);
    }
}
