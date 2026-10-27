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
    private const int SeedCount = 1000;
    // 적이 캠프까지 오는지 끝까지 따라가 보는 시드의 수다. 한 시드에 처음 적 자리 셋을 모두 본다.
    private const int ChaseSeedCount = 50;

    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("Generated maps for many seeds keep their size range, objectives, a clear camp and walkable routes to every goal", ManySeeds));
        tests.Add(new TestCase("Every generated map starts a room, keeps enemies and supplies apart and out of enclosures, and rarely regenerates", RoomsAndSpots));
        tests.Add(new TestCase("The same seed generates the same map and different seeds generate different maps", SameSeedSameMap));
        tests.Add(new TestCase("Map generation draws from its own stream so the public map does not repeat the room's random numbers", OwnStream));
        tests.Add(new TestCase("A room plays on generated maps: deploy at the camp, walk, and reinforcements arrive", PlaysOnGeneratedMaps));
        tests.Add(new TestCase("On generated maps every enemy kind reaches a player near the camp from every initial spot", EnemiesReachCamp));
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

    // 두 자리가 떨어진 칸 수다. 가로와 세로로 떨어진 칸 수 가운데 큰 쪽이다.
    private static int CellGap(FixedVector a, FixedVector b)
    {
        int dx = Math.Abs(CellOf(a.X) - CellOf(b.X));
        int dy = Math.Abs(CellOf(a.Y) - CellOf(b.Y));
        return Math.Max(dx, dy);
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
            // 보급은 캠프 상자 하나(첫 번째), 부목표 둘, 들판 상자 둘이다.
            Equal(layout.Supplies.Count, 5);
            Equal(layout.Supplies[0].Kind, SupplyKind.Camp);
            int objectives = 0;
            int fields = 0;
            for (int i = 1; i < layout.Supplies.Count; i++)
            {
                if (layout.Supplies[i].Kind == SupplyKind.Objective)
                {
                    objectives++;
                }
                if (layout.Supplies[i].Kind == SupplyKind.Field)
                {
                    fields++;
                }
            }
            Equal(objectives, 2);
            Equal(fields, 2);
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
        // 크기가 여러 가지로 나온다. 크기는 가로 17가지 × 세로 13가지다.
        Check(sizes.Count >= 200, $"{SeedCount}개 시드에서 맵 크기가 {sizes.Count}가지뿐입니다");
    }

    private static void RoomsAndSpots()
    {
        int mostAttempts = 0;
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            byte[] bytes = SeededRandom.SeedFromNumber(seed);
            int attempts;
            MissionLayout layout = MissionGenerator.Generate(bytes, out attempts);
            mostAttempts = Math.Max(mostAttempts, attempts);
            string name = $"시드 {seed}({layout.Columns}×{layout.Rows})";

            // 이 배치로 운영 방을 만들 수 있다. 처음 적이 벽이나 다른 적과 겹치면 Match 생성자가 예외를 낸다(PlaceInitialEnemy).
            Match match = new Match("GEN" + seed, Rules.ForGeneratedMission(), bytes, layout);
            Equal(match.Enemies.Count, layout.InitialEnemySpots.Count);

            // 처음 적과 보급은 시설 울타리(가운데에서 3칸 안) 안에 놓이지 않고, 서로 같은 칸이나 이웃 칸에 놓이지 않는다.
            List<FixedVector> spots = new List<FixedVector>();
            List<string> labels = new List<string>();
            for (int i = 0; i < layout.InitialEnemySpots.Count; i++)
            {
                spots.Add(layout.InitialEnemySpots[i].Position);
                labels.Add($"처음 적 {i + 1}");
            }
            for (int i = 0; i < layout.Supplies.Count; i++)
            {
                spots.Add(layout.Supplies[i].Position);
                labels.Add($"보급 {i + 1}");
            }
            for (int i = 0; i < spots.Count; i++)
            {
                for (int f = 0; f < layout.Facilities.Count; f++)
                {
                    Check(CellGap(spots[i], layout.Facilities[f]) > 3, $"{name}: {labels[i]}의 자리가 시설 {f + 1}의 울타리 안입니다");
                }
                for (int j = 0; j < i; j++)
                {
                    Check(CellGap(spots[i], spots[j]) >= 2, $"{name}: {labels[j]}의 자리와 {labels[i]}의 자리가 같은 칸이거나 이웃 칸입니다");
                }
            }
        }
        // 배치를 처음부터 다시 만드는 일은 드물다(시드 10만 개 가운데 99.5%가 한 번에 만들어졌다). 가장 많이 다시 만든 시드도
        // 최대 횟수의 1/8 안이어야 한다. 생성기를 고쳐 조건을 채우기 어려워지면, 운영에서 최대 횟수를 다 써 방을 못 만들기 훨씬 전에 여기서 알아챈다.
        Check(mostAttempts * 8 <= MissionGenerator.LayoutAttempts, $"배치를 한 시드에서 {mostAttempts}번까지 만들었습니다");
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

    private static void EnemiesReachCamp()
    {
        // 조향은 몸 크기에 따라 다르게 걸리므로(몸 통과 판단, 분리 방향) 모든 적 종류를 따로 걸어 본다.
        for (int seed = 1; seed <= ChaseSeedCount; seed++)
        {
            byte[] bytes = SeededRandom.SeedFromNumber(seed);
            MissionLayout layout = MissionGenerator.Generate(bytes);
            // 대원은 캠프 가운데에서 시드마다 정해진 만큼(가로·세로 60 안) 비켜 선다. 칸 가운데 줄에만 서면,
            // 대원에게 가는 직선이 모서리를 아슬아슬하게 스치는 경우를 보지 못한다. 캠프 둘레 4칸은 비어 있어 늘 설 수 있다.
            SeededRandom offsets = new SeededRandom(SeededRandom.Derive(bytes, "chase-test"));
            for (int s = 0; s < layout.InitialEnemySpots.Count; s++)
            {
                for (int k = 0; k < EnemyDefinition.All.Count; k++)
                {
                    EnemyDefinition kind = EnemyDefinition.All[k];
                    FixedVector offset = new FixedVector(Fixed.FromRaw(offsets.NextInt(-60 * 4096, 60 * 4096 + 1)), Fixed.FromRaw(offsets.NextInt(-60 * 4096, 60 * 4096 + 1)));
                    // 증원이 생기지 않게 적 수 상한을 1로 두고, 처음 적을 치운 뒤 검사할 적만 직접 넣는다. 행동 없이 이동만 본다.
                    Rules rules = new Rules { AllowEliteEnemies = true, EnemyWindupMultiplier = 1, MaxEnemies = 1 };
                    Match match = new Match("CHASE" + seed, rules, bytes, layout);
                    Player player = match.Join("p", "pilot")!;
                    player.Connected = true;
                    Check(match.Deploy(player, layout.Camp.X.ToDouble(), layout.Camp.Y.ToDouble()));
                    Advance(match, rules.DeploySeconds + 0.01);
                    player.Position = layout.Camp + offset;
                    Check(match.Map.CanStand(player.Position, Rules.PlayerRadius), $"시드 {seed}: 캠프 둘레 {player.Position}에 대원이 설 수 없습니다");
                    match.Enemies.Clear();
                    Enemy enemy = new Enemy(30060, layout.InitialEnemySpots[s].Position, kind);
                    enemy.NextActionTick = long.MaxValue;
                    match.Enemies.Add(enemy);
                    // 적이 행동을 고르는 거리(Match.EngageDistance) 안까지 90초 안에 와야 한다.
                    Fixed engage = Fixed.FromInt(Match.EngageDistance);
                    bool reached = false;
                    for (long tick = 0; tick < Rules.ToTicks(90); tick++)
                    {
                        match.Step();
                        if (FixedVector.DistanceSquared(enemy.Position, player.Position) < engage * engage)
                        {
                            reached = true;
                            break;
                        }
                    }
                    Check(reached, $"시드 {seed}: 처음 적 자리 {s + 1}에서 출발한 {kind.Name}: {player.Position}의 대원에게 오지 못하고 {enemy.Position}에 멈췄습니다");
                }
            }
        }
    }

    // 시드 1~5로 만든 배치 전체를 SHA-256으로 요약한 값이다. 생성기를 바꾸면 이 값도 바뀌므로, 의도한 변경이면 새 값으로 고친다.
    // 배치를 기록과 같은 JSON으로 적어 요약하므로 기록 모양을 바꿔도 바뀐다(2026-10-27 보급 종류 kind를 더했을 때, 맵은 그대로).
    // 결정론 묶음에도 들어 있어 CI가 Windows·macOS에서도 같은 맵이 나오는지 확인한다.
    private const string ExpectedChecksum = "F30893BFD9402C2E11420F4EA5493018B123C5C5E29217BDEBC8FFD4CE49354A";

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
