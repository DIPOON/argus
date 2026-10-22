using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

// 생성기(MissionGenerator)의 검사다. 여러 시드로 만든 맵이 모두 쓸 수 있는지, 같은 시드면 같은 맵인지, 방이 그 맵에서 도는지 본다.
// 연결 확인은 생성기의 확인을 믿지 않고 여기서 따로 한다.
internal static class GeneratorTests
{
    private const int SeedCount = 300;

    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("Generated maps for many seeds keep their size range, objectives, a clear camp and walkable routes to every goal", ManySeeds));
        tests.Add(new TestCase("The same seed generates the same map and different seeds generate different maps", SameSeedSameMap));
        tests.Add(new TestCase("Map generation draws from its own stream so the public map does not repeat the room's random numbers", OwnStream));
        tests.Add(new TestCase("A room plays on generated maps: deploy at the camp, walk, and reinforcements arrive", PlaysOnGeneratedMaps));
        tests.Add(new TestCase("Generated maps match fixed checksums so every machine generates the same maps", FixedChecksums));
    }

    private static MissionLayout Generate(int seed)
    {
        return MissionGenerator.Generate(SeededRandom.SeedFromNumber(seed));
    }

    private static int CellOf(Fixed coordinate)
    {
        return coordinate.FloorDivide(BattleMap.Cell);
    }

    // 캠프 칸에서 빈 칸만 밟아(상하좌우) 갈 수 있는 칸이다.
    private static bool[] Walkable(MissionLayout layout)
    {
        bool[] reached = new bool[layout.Columns * layout.Rows];
        Queue<int> queue = new Queue<int>();
        int start = CellOf(layout.Camp.Y) * layout.Columns + CellOf(layout.Camp.X);
        reached[start] = true;
        queue.Enqueue(start);
        int[] stepX = new int[] { 1, -1, 0, 0 };
        int[] stepY = new int[] { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % layout.Columns;
            int y = index / layout.Columns;
            for (int i = 0; i < 4; i++)
            {
                int nx = x + stepX[i];
                int ny = y + stepY[i];
                if (nx < 0 || ny < 0 || nx >= layout.Columns || ny >= layout.Rows)
                {
                    continue;
                }
                int next = ny * layout.Columns + nx;
                if (layout.Tiles[next] != 0 || reached[next])
                {
                    continue;
                }
                reached[next] = true;
                queue.Enqueue(next);
            }
        }
        return reached;
    }

    private static bool Reached(MissionLayout layout, bool[] reached, FixedVector point)
    {
        return reached[CellOf(point.Y) * layout.Columns + CellOf(point.X)];
    }

    private static void ManySeeds()
    {
        HashSet<int> sizes = new HashSet<int>();
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            MissionLayout layout = Generate(seed);
            string name = $"시드 {seed}({layout.Columns}×{layout.Rows})";
            Check(layout.Columns >= MissionGenerator.MinimumColumns && layout.Columns <= MissionGenerator.MaximumColumns, $"{name}: 가로 칸 수가 범위 밖입니다");
            Check(layout.Rows >= MissionGenerator.MinimumRows && layout.Rows <= MissionGenerator.MaximumRows, $"{name}: 세로 칸 수가 범위 밖입니다");
            sizes.Add(layout.Columns * 1000 + layout.Rows);
            Equal(layout.Facilities.Count, MissionGenerator.FacilityCount);
            Equal(layout.Supplies.Count, 5);
            int objectives = 0;
            for (int i = 0; i < layout.Supplies.Count; i++)
            {
                if (layout.Supplies[i].Objective)
                {
                    objectives++;
                }
            }
            Equal(objectives, 2);
            Equal(layout.InitialEnemySpots.Count, 3);
            Equal(layout.InitialEnemySpots[0].Kind, EnemyDefinition.Grunt);
            Equal(layout.InitialEnemySpots[1].Kind, EnemyDefinition.Breaker);
            Equal(layout.InitialEnemySpots[2].Kind, EnemyDefinition.Warden);

            // 캠프 둘레 4칸 안은 빈 땅이라, 투입한 뒤 어느 쪽으로든 바로 움직일 수 있다.
            int campX = CellOf(layout.Camp.X);
            int campY = CellOf(layout.Camp.Y);
            for (int y = campY - 4; y <= campY + 4; y++)
            {
                for (int x = campX - 4; x <= campX + 4; x++)
                {
                    int dx = x - campX;
                    int dy = y - campY;
                    if (dx * dx + dy * dy <= 16)
                    {
                        Equal(layout.Tiles[y * layout.Columns + x], (byte)0);
                    }
                }
            }

            // 캠프에서 벽을 부수지 않고 걸어서 모든 목표에 갈 수 있다. 증원 타원 위의 점도 60% 넘게 갈 수 있다.
            bool[] reached = Walkable(layout);
            Check(Reached(layout, reached, layout.Extraction), $"{name}: 탈출 지점에 갈 수 없습니다");
            for (int i = 0; i < layout.Facilities.Count; i++)
            {
                Check(Reached(layout, reached, layout.Facilities[i]), $"{name}: 시설 {i + 1}에 갈 수 없습니다");
            }
            for (int i = 0; i < layout.Supplies.Count; i++)
            {
                Check(Reached(layout, reached, layout.Supplies[i].Position), $"{name}: 보급 {i + 1}에 갈 수 없습니다");
            }
            for (int i = 0; i < layout.InitialEnemySpots.Count; i++)
            {
                Check(Reached(layout, reached, layout.InitialEnemySpots[i].Position), $"{name}: 처음 적 {i + 1}에게 갈 수 없습니다");
            }
            SpawnRing ring = layout.Reinforcements;
            int reachable = 0;
            for (int i = 0; i < 64; i++)
            {
                FixedVector direction = Angle.FromFraction(i, 64).Direction();
                FixedVector point = new FixedVector(ring.Center.X + direction.X * ring.RadiusX, ring.Center.Y + direction.Y * ring.RadiusY);
                if (Reached(layout, reached, point))
                {
                    reachable++;
                }
            }
            Check(reachable * 100 >= 64 * 60, $"{name}: 증원 타원 위의 점에 {reachable}/64개만 갈 수 있습니다");
        }
        // 크기가 여러 가지로 나온다.
        Check(sizes.Count >= 100, $"{SeedCount}개 시드에서 맵 크기가 {sizes.Count}가지뿐입니다");
    }

    // 배치를 기록과 같은 모양의 JSON으로 적는다. 두 배치가 같은지 비교할 때 쓴다.
    private static string Text(MissionLayout layout)
    {
        return JsonSerializer.Serialize(LayoutRecord.From(layout), RoomHost.Json);
    }

    private static void SameSeedSameMap()
    {
        Equal(Text(Generate(7)), Text(Generate(7)));
        HashSet<string> maps = new HashSet<string>();
        for (int seed = 1; seed <= 20; seed++)
        {
            maps.Add(Text(Generate(seed)));
        }
        Equal(maps.Count, 20);
    }

    private static void OwnStream()
    {
        byte[] seed = SeededRandom.SeedFromNumber(11);
        byte[] derived = SeededRandom.Derive(seed, "mission-layout");
        Check(!derived.AsSpan().SequenceEqual(seed), "맵 생성 시드가 방의 시드와 같습니다");
        SeededRandom room = new SeededRandom(seed);
        SeededRandom map = new SeededRandom(derived);
        for (int i = 0; i < 8; i++)
        {
            Check(room.NextUInt64() != map.NextUInt64(), $"{i}번째 수가 방의 수열과 맵의 수열에서 같습니다");
        }
        // 용도가 다르면 다른 시드다.
        Check(!derived.AsSpan().SequenceEqual(SeededRandom.Derive(seed, "other")), "용도가 달라도 같은 시드가 나옵니다");
    }

    private static void PlaysOnGeneratedMaps()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            byte[] bytes = SeededRandom.SeedFromNumber(seed);
            MissionLayout layout = MissionGenerator.Generate(bytes);
            Match match = new Match("GEN" + seed, Rules.ForGeneratedMission(), bytes, layout);
            Player player = match.Join("p", "pilot")!;
            Check(match.Deploy(player, layout.Camp.X.ToDouble(), layout.Camp.Y.ToDouble()), $"시드 {seed}: 캠프에 투입하지 못했습니다");
            Advance(match, match.Rules.DeploySeconds + 0.01);
            Equal(player.State, "alive");
            // 캠프 둘레는 비어 있으므로 어느 쪽으로 걸어도 1초에 180만큼 간다.
            FixedVector start = player.Position;
            for (long tick = 0; tick < Rules.TicksPerSecond; tick++)
            {
                match.Input(player, tick, 1, 0, 0, 0);
                match.Step();
            }
            Near(Distance(start, player.Position), 180, 0.1);
            // 40초 동안 원을 그리며 걸으면 증원이 생긴다. 적은 모두 맵 안의 설 수 있는 자리에 있다.
            long ticks = Rules.ToTicks(40);
            for (long tick = 0; tick < ticks; tick++)
            {
                double heading = tick * 0.03;
                match.Input(player, Rules.TicksPerSecond + tick, Math.Cos(heading), Math.Sin(heading), 0, 0);
                match.Step();
            }
            Check(match.Enemies.Count > 3, $"시드 {seed}: 증원이 생기지 않았습니다");
            for (int i = 0; i < match.Enemies.Count; i++)
            {
                Enemy enemy = match.Enemies[i];
                Check(match.Map.CanStand(enemy.Position, enemy.Definition.Radius), $"시드 {seed}: 적 {enemy.Id}가 설 수 없는 자리 {enemy.Position}에 있습니다");
            }
            byte[] snapshot = Snapshot.Create(match, player, RoomHost.Json);
            using (JsonDocument json = JsonDocument.Parse(snapshot))
            {
                JsonElement map = json.RootElement.GetProperty("map");
                Equal(map.GetProperty("columns").GetInt32(), layout.Columns);
                Equal(map.GetProperty("rows").GetInt32(), layout.Rows);
            }
        }
    }

    // 시드 1~5로 만든 배치 전체를 SHA-256으로 요약한 값이다. 생성기를 바꾸면 이 값도 바뀌므로, 의도한 변경이면 새 값으로 고친다.
    // 결정론 묶음에도 들어 있어 CI가 Windows·macOS에서도 같은 맵이 나오는지 확인한다.
    private const string ExpectedChecksum = "656438A18466235F31638F1F6864F6B5B406FB09BADFA0616E060C06415112F9";

    private static void FixedChecksums()
    {
        StringBuilder text = new StringBuilder();
        for (int seed = 1; seed <= 5; seed++)
        {
            text.Append(Text(Generate(seed)));
            text.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        Equal(Convert.ToHexString(hash), ExpectedChecksum);
    }
}
