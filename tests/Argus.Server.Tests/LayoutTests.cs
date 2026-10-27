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
        tests.Add(new TestCase("A layout rejects spots outside the map or in walls and a reinforcement ring nobody can stand on", LayoutSpots));
        tests.Add(new TestCase("Each initial enemy spot names its kind, and elite spots hold grunts when elites are off", SpotKinds));
        tests.Add(new TestCase("A layout rejects initial enemies whose bodies touch a wall or overlap, so every accepted layout starts a room", EnemyBodies));
    }

    // 가장자리만 부술 수 없는 벽인 빈 맵이다.
    internal static byte[] BorderTiles(int columns, int rows)
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

    // 손으로 만든 맵(40×30)보다 작은 24×18 맵이다. 가운데에 부술 수 있는 벽이 하나 있다. 기록 검사도 쓴다.
    internal static MissionLayout SmallLayout()
    {
        int columns = 24;
        int rows = 18;
        byte[] tiles = BorderTiles(columns, rows);
        tiles[9 * columns + 12] = 1;
        FixedVector camp = FixedVector.FromInt(150, 430);
        FixedVector[] facilities = new FixedVector[] { FixedVector.FromInt(820, 200) };
        SupplySpot[] supplies = new SupplySpot[]
        {
            new SupplySpot(FixedVector.FromInt(220, 470), SupplyKind.Field),
            new SupplySpot(FixedVector.FromInt(600, 700), SupplyKind.Objective)
        };
        EnemySpot[] enemies = new EnemySpot[] { new EnemySpot(FixedVector.FromInt(900, 650), EnemyDefinition.Grunt) };
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
        Equal(fresh.Enemies[0].Position, layout.InitialEnemySpots[0].Position);

        Match match = DeployedRoom(layout, out Player player);

        // 스냅샷의 맵 크기와 캠프, 벽 칸 수도 이 배치를 따른다. 캠프에서 먼 탈출 지점은 아직 보지 못해 보내지 않는다.
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
            Equal(root.GetProperty("extraction").ValueKind, JsonValueKind.Null);
            byte[] walls = Convert.FromBase64String(root.GetProperty("walls").GetString()!);
            Equal(walls.Length, 24 * 18);
        }

        // 걷는 검사와 상태 요약 비교에 영향이 없도록 다른 방에서 탈출 지점을 둘러본 뒤 본다.
        Match scouted = DeployedRoom(SmallLayout(), out Player scout);
        Scout(scouted, scout, layout.Extraction);
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(scouted, scout, RoomHost.Json)))
        {
            JsonElement extraction = json.RootElement.GetProperty("extraction");
            Equal(extraction.GetProperty("x").GetDouble(), 1000.0);
            Equal(extraction.GetProperty("y").GetDouble(), 430.0);
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
        SupplySpot[] supplies = new SupplySpot[] { new SupplySpot(FixedVector.FromInt(150, far), SupplyKind.Objective) };
        EnemySpot[] enemies = new EnemySpot[] { new EnemySpot(FixedVector.FromInt(far, 150), EnemyDefinition.Grunt) };
        int center = size * BattleMap.Cell / 2;
        SpawnRing ring = new SpawnRing(FixedVector.FromInt(center, center), center - 150, center - 150);
        MissionLayout layout = new MissionLayout(size, size, BorderTiles(size, size), camp, FixedVector.FromInt(center, center),
            facilities, supplies, enemies, ring);
        Match match = DeployedRoom(layout, out Player player);
        Walk(match, player, 15);
        byte[] snapshot = Snapshot.Create(match, player, RoomHost.Json);
        Check(snapshot.Length > 0);

        // 맵을 대각선으로 가로지르는 계산을 직접 한다. 시야 검사는 거리에서 먼저 끝나 벽 판정까지 가지 않으므로 따로 부른다.
        // 벽 판정의 엇갈린 곱과 대각선 길이(제곱의 합)가 이 맵에서 가장 큰 좌표를 쓴다.
        FixedVector near = FixedVector.FromInt(60, 60);
        FixedVector opposite = FixedVector.FromInt(size * BattleMap.Cell - 60, size * BattleMap.Cell - 60);
        WallHit across = match.Map.RayWall(near, opposite);
        Check(!across.Hit, "빈 맵의 대각선이 벽에 막혔습니다");
        Check(match.Map.LineOfSight(opposite, near), "반대 방향 대각선이 막혔습니다");
        Fixed span = FixedVector.Distance(near, opposite);
        Near(span.ToDouble(), (size * BattleMap.Cell - 120) * Math.Sqrt(2), 0.01);
    }

    private static void LayoutLimits()
    {
        FixedVector camp = FixedVector.FromInt(150, 150);
        FixedVector[] none = new FixedVector[0];
        SupplySpot[] noSupplies = new SupplySpot[0];
        EnemySpot[] noEnemies = new EnemySpot[0];
        SpawnRing ring = new SpawnRing(camp, 100, 100);
        int small = MissionLayout.MinimumSize;
        int large = MissionLayout.MaximumSize;
        // 경계값의 크기는 받는다.
        new MissionLayout(small, small, BorderTiles(small, small), camp, camp, none, noSupplies, noEnemies, ring);
        new MissionLayout(large, large, BorderTiles(large, large), camp, camp, none, noSupplies, noEnemies, ring);
        // 범위를 벗어난 크기, 크기와 맞지 않는 칸 수, 모르는 벽 종류는 거절한다. 어느 쪽 크기가 틀렸는지 매개변수 이름으로 알린다.
        ArgumentOutOfRangeException narrow = Caught<ArgumentOutOfRangeException>(() => new MissionLayout(small - 1, small, new byte[(small - 1) * small], camp, camp, none, noSupplies, noEnemies, ring));
        Equal(narrow.ParamName, "columns");
        ArgumentOutOfRangeException tall = Caught<ArgumentOutOfRangeException>(() => new MissionLayout(small, large + 1, new byte[small * (large + 1)], camp, camp, none, noSupplies, noEnemies, ring));
        Equal(tall.ParamName, "rows");
        Throws<ArgumentException>(() => new MissionLayout(small, small, new byte[small * small - 1], camp, camp, none, noSupplies, noEnemies, ring));
        byte[] unknown = BorderTiles(small, small);
        unknown[small + 1] = 3;
        Throws<ArgumentException>(() => new MissionLayout(small, small, unknown, camp, camp, none, noSupplies, noEnemies, ring));

        // 배치는 받은 배열을 복사해 읽기 전용으로 둔다. 만든 뒤에 그 배열을 바꿔도, 목록을 배열로 형변환해도 고칠 수 없다.
        byte[] tiles = BorderTiles(small, small);
        MissionLayout layout = new MissionLayout(small, small, tiles, camp, camp, none, noSupplies, noEnemies, ring);
        tiles[small + 1] = 1;
        Equal(layout.Tiles[small + 1], (byte)0);
        Check(!(layout.Tiles is byte[]), "벽 목록이 배열 그대로라 형변환으로 고칠 수 있습니다");
        Check(!(layout.InitialEnemySpots is EnemySpot[]), "처음 적 목록이 배열 그대로라 형변환으로 고칠 수 있습니다");
    }

    // 작은 빈 맵(10×10, 480×480)에 캠프·탈출 지점·증원 타원을 두고, 그중 하나만 바꿔 거절되는지 본다.
    private static MissionLayout Spots(FixedVector camp, FixedVector extraction, FixedVector facility, FixedVector supply, FixedVector enemy,
        SpawnRing ring)
    {
        int size = 10;
        FixedVector[] facilities = new FixedVector[] { facility };
        SupplySpot[] supplies = new SupplySpot[] { new SupplySpot(supply, SupplyKind.Field) };
        EnemySpot[] enemies = new EnemySpot[] { new EnemySpot(enemy, EnemyDefinition.Grunt) };
        return new MissionLayout(size, size, BorderTiles(size, size), camp, extraction, facilities, supplies, enemies, ring);
    }

    private static void LayoutSpots()
    {
        FixedVector open = FixedVector.FromInt(240, 240);
        // 가장자리 벽 칸(0, 0) 안의 점이다.
        FixedVector inWall = FixedVector.FromInt(20, 20);
        FixedVector outside = FixedVector.FromInt(100000, 100000);
        SpawnRing ring = new SpawnRing(open, 120, 120);
        // 모두 빈 땅이면 받는다.
        Spots(open, open, open, open, open, ring);
        // 맵 밖의 자리는 어느 것이든 거절한다. 맵 밖 좌표는 고정소수점 계산의 범위 근거도 깬다.
        Throws<ArgumentException>(() => Spots(outside, open, open, open, open, ring));
        Throws<ArgumentException>(() => Spots(open, outside, open, open, open, ring));
        Throws<ArgumentException>(() => Spots(open, open, outside, open, open, ring));
        Throws<ArgumentException>(() => Spots(open, open, open, outside, open, ring));
        Throws<ArgumentException>(() => Spots(open, open, open, open, outside, ring));
        // 벽 칸 안의 자리도 거절한다. 캠프와 탈출 지점은 대원이 설 수 있어야 하므로 벽에 너무 붙어도 거절한다.
        Throws<ArgumentException>(() => Spots(inWall, open, open, open, open, ring));
        Throws<ArgumentException>(() => Spots(open, inWall, open, open, open, ring));
        Throws<ArgumentException>(() => Spots(open, open, inWall, open, open, ring));
        Throws<ArgumentException>(() => Spots(open, open, open, inWall, open, ring));
        Throws<ArgumentException>(() => Spots(open, open, open, open, inWall, ring));
        FixedVector touching = FixedVector.FromInt(55, 240);
        Throws<ArgumentException>(() => Spots(touching, open, open, open, open, ring));
        // 증원 타원은 반지름이 1 이상이고 맵 안에 들어가야 하며, 가장 큰 적이 설 수 있는 점이 하나는 있어야 한다.
        Throws<ArgumentException>(() => Spots(open, open, open, open, open, new SpawnRing(open, 0, 120)));
        Throws<ArgumentException>(() => Spots(open, open, open, open, open, new SpawnRing(open, 120, -5)));
        Throws<ArgumentException>(() => Spots(open, open, open, open, open, new SpawnRing(open, 300, 120)));
        Throws<ArgumentException>(() => Spots(open, open, open, open, open, new SpawnRing(inWall, 10, 10)));
    }

    private static void SpotKinds()
    {
        // 손으로 만든 맵의 처음 적 자리는 병사·분쇄자·철위병이다. 엘리트를 끄면 엘리트 자리에도 병사가 선다.
        MissionLayout layout = MissionLayout.CreateHandmade();
        Equal(layout.InitialEnemySpots[1].Kind, EnemyDefinition.Breaker);
        Equal(layout.InitialEnemySpots[2].Kind, EnemyDefinition.Warden);
        Match elites = new Match("KIND01", new Rules { AllowEliteEnemies = true }, 3);
        Match grunts = new Match("KIND02", new Rules(), 3);
        for (int i = 0; i < layout.InitialEnemySpots.Count; i++)
        {
            Equal(elites.Enemies[i].Definition.Id, layout.InitialEnemySpots[i].Kind.Id);
            Equal(grunts.Enemies[i].Definition.Id, EnemyDefinition.Grunt.Id);
        }
        Check(EnemyDefinition.Breaker.IsElite && EnemyDefinition.Warden.IsElite && !EnemyDefinition.Grunt.IsElite, "엘리트 판정이 틀립니다");
        Equal(EnemyDefinition.FromId("warden"), EnemyDefinition.Warden);
        Throws<ArgumentException>(() => EnemyDefinition.FromId("dragon"));
    }

    // 작은 빈 맵(10×10, 480×480)의 가운데에 캠프·탈출 지점·시설·보급을 두고 처음 적만 바꿔 본다.
    private static MissionLayout WithEnemies(EnemySpot[] enemies)
    {
        int size = 10;
        FixedVector middle = FixedVector.FromInt(240, 240);
        FixedVector[] facilities = new FixedVector[] { middle };
        SupplySpot[] supplies = new SupplySpot[] { new SupplySpot(middle, SupplyKind.Field) };
        SpawnRing ring = new SpawnRing(middle, 120, 120);
        return new MissionLayout(size, size, BorderTiles(size, size), middle, middle, facilities, supplies, enemies, ring);
    }

    private static void EnemyBodies()
    {
        // 처음 적은 그 종류의 몸이 벽에 닿지 않고 들어가야 한다. 가장자리 벽의 안쪽 면은 x = 48이라,
        // x = 70에 선 병사(반지름 13)는 들어가고 철위병(반지름 23)은 벽에 걸린다.
        FixedVector nearWall = FixedVector.FromInt(70, 240);
        WithEnemies(new EnemySpot[] { new EnemySpot(nearWall, EnemyDefinition.Grunt) });
        ArgumentException wall = Caught<ArgumentException>(() => WithEnemies(new EnemySpot[] { new EnemySpot(nearWall, EnemyDefinition.Warden) }));
        Equal(wall.ParamName, "InitialEnemySpots");

        // 두 적의 몸은 겹치지 않아야 한다. 분쇄자(22)와 철위병(23)은 가운데끼리 45 떨어지면 맞닿기만 하므로 받고, 44면 거절한다.
        FixedVector middle = FixedVector.FromInt(240, 240);
        EnemySpot breaker = new EnemySpot(middle, EnemyDefinition.Breaker);
        EnemySpot touching = new EnemySpot(middle + FixedVector.FromInt(45, 0), EnemyDefinition.Warden);
        EnemySpot overlapping = new EnemySpot(middle + FixedVector.FromInt(44, 0), EnemyDefinition.Warden);
        MissionLayout accepted = WithEnemies(new EnemySpot[] { breaker, touching });
        ArgumentException overlap = Caught<ArgumentException>(() => WithEnemies(new EnemySpot[] { breaker, overlapping }));
        Equal(overlap.ParamName, "InitialEnemySpots");
        // 같은 자리의 두 적도 거절한다. 생성기가 가까운 두 시설의 적을 같은 칸에 두던 결함이 이 경우였다.
        EnemySpot grunt = new EnemySpot(middle, EnemyDefinition.Grunt);
        Throws<ArgumentException>(() => WithEnemies(new EnemySpot[] { grunt, grunt }));

        // 받은 배치로는 방을 만들 수 있다. Match가 맞닿은 두 적을 모두 둔다.
        Match match = new Match("BODY01", new Rules { AllowEliteEnemies = true }, SeededRandom.SeedFromNumber(1), accepted);
        Equal(match.Enemies.Count, 2);
    }

    // action이 TException을 내면 그 예외를 돌려준다. 예외의 내용(매개변수 이름 등)까지 볼 때 쓴다.
    private static TException Caught<TException>(Func<object> action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new Exception($"{typeof(TException).Name} 예외가 나지 않았습니다");
    }
}
